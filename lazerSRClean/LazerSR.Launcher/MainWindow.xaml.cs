using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LazerSR.Launcher.Configuration;
using LazerSR.Launcher.Ipc;
using LazerSR.Launcher.Replay;
using LazerSR.Launcher.Sunny;
using Microsoft.Win32;

namespace LazerSR.Launcher;

public partial class MainWindow : Window
{
    private readonly InstallPathProvider _installPathProvider;
    private PipeClient? _pipeClient;
    private Process? _osuProcess;

    private bool _collectInFlight;
    private bool _syncInFlight;

    // sunny 정렬: Hook이 sunnyup:으로 보내온 계산값을 모아 서버에 배치 업로드한다.
    private readonly List<SunnySortClient.Entry> _sunnyBuf = new();
    private readonly object _sunnyBufLock = new();
    private DispatcherTimer? _sunnyFlushTimer;

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

        // sunny 정렬: 실행 시 Hook이 서버 덤프를 요청한다 (트리거 ③).
        if (line.StartsWith("sunnysyncreq:"))
        {
            var p = line.Split(':', 3);
            if (p.Length >= 3)
                _ = HandleSunnySyncAsync(p[1], p[2]);
            return;
        }

        // sunny 정렬: Hook 워커가 계산값 하나를 보고한다. 버퍼에 모아 배치 업로드.
        if (line.StartsWith("sunnyup:"))
        {
            QueueSunnyUpload(line);
            return;
        }

        // lazerSR 리더보드: Hook은 네트워크를 못 쓰므로 서버 조회를 런처가 대신한다.
        if (line.StartsWith("lbreq:"))
        {
            var p = line.Split(':', 4);
            if (p.Length >= 3)
                _ = HandleLeaderboardRequestAsync(p[1], p[2], p.Length > 3 ? p[3] : "*");
            return;
        }

        if (line.StartsWith("lbdl:"))
        {
            var p = line.Split(':', 3);
            if (p.Length >= 3)
                _ = HandleReplayDownloadRequestAsync(p[1], p[2]);
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

    // ── sunny 정렬 ─────────────────────────────────────────────────────

    private async Task HandleSunnySyncAsync(string reqId, string calcVersionRaw)
    {
        try
        {
            int calcVersion = int.TryParse(calcVersionRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            string json = await SunnySortClient.GetAllRawAsync(calcVersion);
            await SendPipeAsync($"sunnysyncreqok:{reqId}:{json}");
        }
        catch (Exception ex)
        {
            await SendPipeAsync($"sunnysyncreqerr:{reqId}:{ex.Message}");
        }
    }

    private void QueueSunnyUpload(string line)
    {
        // sunnyup:<hash>:<rate>:<sr>:<calcVersion>
        var p = line.Split(':');
        if (p.Length < 5)
            return;

        if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double rate)) return;
        if (!double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double sr)) return;
        if (!int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int calcVersion)) return;

        lock (_sunnyBufLock)
            _sunnyBuf.Add(new SunnySortClient.Entry(p[1], rate, sr, calcVersion));

        _sunnyFlushTimer ??= CreateSunnyFlushTimer();
        _sunnyFlushTimer.Stop();
        _sunnyFlushTimer.Start();

        bool full;
        lock (_sunnyBufLock)
            full = _sunnyBuf.Count >= 100;

        if (full)
            _ = FlushSunnyAsync();
    }

    private DispatcherTimer CreateSunnyFlushTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); _ = FlushSunnyAsync(); };
        return t;
    }

    private async Task FlushSunnyAsync()
    {
        List<SunnySortClient.Entry> batch;
        lock (_sunnyBufLock)
        {
            if (_sunnyBuf.Count == 0)
                return;
            batch = new List<SunnySortClient.Entry>(_sunnyBuf);
            _sunnyBuf.Clear();
        }

        try
        {
            await SunnySortClient.PostBatchAsync(batch);
        }
        catch (Exception ex)
        {
            // 실패한 배치는 다시 넣어 다음 flush 때 재시도.
            lock (_sunnyBufLock)
                _sunnyBuf.InsertRange(0, batch);

            Dispatcher.Invoke(() => StatusTextBlock.Text = $"sunny 업로드 실패: {ex.Message}");
        }
    }

    private async Task HandleLeaderboardRequestAsync(string reqId, string beatmapMd5, string modsToken)
    {
        try
        {
            string json = await ReplayServerClient.GetLeaderboardRawAsync(beatmapMd5, modsToken);
            await SendPipeAsync($"lbreqok:{reqId}:{json}");
        }
        catch (Exception ex)
        {
            await SendPipeAsync($"lbreqerr:{reqId}:{ex.Message}");
        }
    }

    private async Task HandleReplayDownloadRequestAsync(string reqId, string scoreGuid)
    {
        try
        {
            string path = await ReplayServerClient.DownloadReplayToCacheAsync(scoreGuid);
            await SendPipeAsync($"lbdlok:{reqId}:{path}");
        }
        catch (Exception ex)
        {
            await SendPipeAsync($"lbdlerr:{reqId}:{ex.Message}");
        }
    }

    private async Task SendPipeAsync(string line)
    {
        var client = _pipeClient;
        if (client is { Status: PipeStatus.Connected })
            await client.SendAsync(line);
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
