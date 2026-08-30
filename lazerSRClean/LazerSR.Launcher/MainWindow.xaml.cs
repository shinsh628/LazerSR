using System.Diagnostics;
using System.IO;
using System.Windows;
using LazerSR.Launcher.Configuration;
using LazerSR.Launcher.Ipc;
using Microsoft.Win32;

namespace LazerSR.Launcher;

public partial class MainWindow : Window
{
    private readonly InstallPathProvider _installPathProvider;
    private PipeClient? _pipeClient;
    private Process? _osuProcess;

    public MainWindow() : this(new InstallPathProvider(new LauncherSettingsStore())) { }

    public MainWindow(InstallPathProvider installPathProvider)
    {
        _installPathProvider = installPathProvider;
        InitializeComponent();
        ApplyInstallLocation(_installPathProvider.Load());
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
        ApplyPipeStatus(PipeStatus.Connecting);
        client.ConnectAsync();
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
