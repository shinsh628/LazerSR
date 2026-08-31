using System.Diagnostics;
using System.IO;
using System.Windows;
using LazerSR.Launcher.Configuration;
using LazerSR.Launcher.Ipc;
using LazerSR.Launcher.Replay;
using Microsoft.Win32;

namespace LazerSR.Launcher;

public partial class MainWindow : Window
{
    private readonly InstallPathProvider _installPathProvider;
    private PipeClient? _pipeClient;
    private Process? _osuProcess;

    private bool _collectInFlight;
    private bool _syncInFlight;

    public MainWindow() : this(new InstallPathProvider(new LauncherSettingsStore())) { }

    public MainWindow(InstallPathProvider installPathProvider)
    {
        _installPathProvider = installPathProvider;
        InitializeComponent();
        ApplyInstallLocation(_installPathProvider.Load());

        Loaded += async (_, _) =>
        {
            await RefreshReplayCountAsync();
            await DrainQueueAndReportAsync(quiet: true); // 지난 세션에 남거나 런처 없이 친 판 회수
        };
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select osu!.exe",
            Filter = "osu! executable|osu!.exe|All executables|*.exe",
            FilterIndex = 1,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
            InstallPathTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;

        string path = InstallPathTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            try { ApplyInstallLocation(_installPathProvider.Save(path)); }
            catch (Exception ex) { StatusTextBlock.Text = $"Save failed: {ex.Message}"; }
        }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectPipe();
        _osuProcess?.Dispose();
        _osuProcess = null;

        var location = _installPathProvider.Current;
        if (!location.IsValid) { StatusTextBlock.Text = "Configure osu! path first."; return; }

        string? exePath = FindOsuExecutable(location.Path!);
        if (exePath == null) { StatusTextBlock.Text = "osu!.exe not found."; return; }

        string hookDll = Path.Combine(AppContext.BaseDirectory, "LazerSR.Hook.dll");
        if (!File.Exists(hookDll)) { StatusTextBlock.Text = $"LazerSR.Hook.dll not found in {AppContext.BaseDirectory}"; return; }

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        psi.Environment["DOTNET_STARTUP_HOOKS"] = hookDll;
        psi.Environment["OSU_EXTERNAL_UPDATE_PROVIDER"] = "1";
        psi.Environment["OSU_DISABLE_ERROR_REPORTING"] = "1";
        psi.Environment["LAZERSR_SUNNYPLUS"] = SunnyPlusCheckBox.IsChecked == true ? "1" : "0";

        try { _osuProcess = Process.Start(psi); }
        catch (Exception ex) { StatusTextBlock.Text = $"Launch failed: {ex.Message}"; return; }

        if (_osuProcess == null) { StatusTextBlock.Text = "Process.Start returned null."; return; }

        _osuProcess.EnableRaisingEvents = true;
        var launchedProcess = _osuProcess;
        _osuProcess.Exited += (_, _) => Dispatcher.Invoke(() =>
        {
            if (ReferenceEquals(_osuProcess, launchedProcess))
                DisconnectPipe();
        });

        StatusTextBlock.Text = $"osu! launched (PID {_osuProcess.Id})";
        StartPipeClient(_osuProcess.Id);
    }

    private async void SunnyPlusCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_pipeClient == null) return;
        await _pipeClient.SendAsync(SunnyPlusCheckBox.IsChecked == true ? "sunnyplus:on" : "sunnyplus:off");
    }

    private void StartPipeClient(int osuPid)
    {
        var client = new PipeClient(osuPid);
        _pipeClient = client;
        client.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(_pipeClient, client)) return;
            ApplyPipeStatus(status);
        });
        client.MessageReceived += line => Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(_pipeClient, client)) return;
            HandlePipeMessage(line);
        });
        ApplyPipeStatus(PipeStatus.Connecting);
        client.ConnectAsync();
    }

    // ── 리플레이 저장 서버 ──────────────────────────────────────────────

    private async void CollectReplaysButton_Click(object sender, RoutedEventArgs e)
    {
        if (_collectInFlight) return;

        if (_pipeClient == null || _pipeClient.Status != PipeStatus.Connected)
        {
            StatusTextBlock.Text = "osu!를 먼저 실행하세요.";
            return;
        }

        _collectInFlight = true;
        CollectReplaysButton.IsEnabled = false;
        StatusTextBlock.Text = "로컬 리플레이 확인 중…";
        await _pipeClient.SendAsync("replaycollect:scan");
        // 응답은 HandlePipeMessage에서 비동기로 온다.
    }

    private async void HandlePipeMessage(string line)
    {
        // 트리거 #2 — Hook이 매 게임 후 큐 파일을 쓰고 이 신호를 쏜다. 바로 드레인해 서버로 올린다.
        if (line == "replayqueued")
        {
            await DrainQueueAndReportAsync(quiet: false);
            return;
        }

        if (!line.StartsWith("replaycollect:")) return;

        string payload = line["replaycollect:".Length..];

        if (payload == "notready")
        {
            StatusTextBlock.Text = "osu! 로그인 상태를 확인한 뒤 선곡 화면에서 다시 시도하세요.";
            FinishCollect();
        }
        else if (payload == "error")
        {
            StatusTextBlock.Text = "리플레이 수집 중 오류가 발생했습니다.";
            FinishCollect();
        }
        else if (payload.StartsWith("queued:"))
        {
            int.TryParse(payload["queued:".Length..], out int n);
            StatusTextBlock.Text = n == 0 ? "새로 올릴 리플레이가 없습니다." : $"{n}개 발견 — 업로드 중…";
            try { await DrainQueueAndReportAsync(quiet: false); }
            finally { FinishCollect(); }
        }
    }

    private void FinishCollect()
    {
        _collectInFlight = false;
        CollectReplaysButton.IsEnabled = true;
    }

    private async Task DrainQueueAndReportAsync(bool quiet)
    {
        if (_syncInFlight) return;
        _syncInFlight = true;

        try
        {
            var result = await ReplayServerClient.DrainQueueAsync(s => Dispatcher.Invoke(() => StatusTextBlock.Text = s));

            if (result.Total > 0)
            {
                string msg = $"동기화 완료 — 업로드 {result.Uploaded} · 중복 {result.Duplicate} · 실패 {result.Failed}";
                if (result.FirstError != null)
                    msg += $"\n{result.FirstError}";
                StatusTextBlock.Text = msg;
            }
            else if (!quiet)
            {
                StatusTextBlock.Text = "동기화할 리플레이가 없습니다.";
            }

            await RefreshReplayCountAsync();
        }
        finally
        {
            _syncInFlight = false;
        }
    }

    private async Task RefreshReplayCountAsync()
    {
        int? count = await ReplayServerClient.GetReplayCountAsync();
        ReplayCountTextBlock.Text = count is { } n
            ? $"리플레이 {n}개 저장됨"
            : "리플레이 서버에 연결하지 못함";
    }

    private void DisconnectPipe()
    {
        _pipeClient?.Dispose();
        _pipeClient = null;
        ApplyPipeStatus(PipeStatus.Disconnected);
    }

    private void ApplyPipeStatus(PipeStatus status)
    {
        PipeStatusTextBlock.Text = status switch
        {
            PipeStatus.Connected  => $"Connected (PID {_osuProcess?.Id})",
            PipeStatus.Connecting => "Connecting…",
            _                     => "Disconnected",
        };
    }

    private void ApplyInstallLocation(LazerInstallLocation loc)
    {
        InstallPathTextBox.Text = loc.Path ?? string.Empty;
        StatusTextBlock.Text = !loc.IsConfigured ? "Browse for osu!.exe."
            : loc.IsValid ? "osu! path loaded."
            : "Saved path no longer valid.";
    }

    private static string? FindOsuExecutable(string installPath)
    {
        foreach (string name in new[] { "osu!.exe", "osu.exe" })
        {
            string full = Path.Combine(installPath, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
