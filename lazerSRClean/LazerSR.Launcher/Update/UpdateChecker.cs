using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace LazerSR.Launcher.Update;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string FileName);

/// <summary>
/// GitHub Releases 기반 런처 자동 업데이트. public repo라 인증 토큰이 필요 없다.
/// 어떤 실패도(네트워크 없음, API 제한, JSON 변경 등) 예외를 밖으로 내보내지 않고 null/false로만 알린다 —
/// 업데이트 검사는 런처 실행을 막아서는 안 된다.
/// </summary>
public static class UpdateChecker
{
    private const string repo = "shinsh628/LazerSR";
    private const string latestReleaseApi = "https://api.github.com/repos/" + repo + "/releases/latest";

    private static readonly HttpClient http = CreateClient();

    private static HttpClient CreateClient()
    {
        // 다운로드까지 커버하도록 넉넉히. 검사 자체는 CancellationToken으로 5초 컷.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LazerSR-Launcher");
        return client;
    }

    public static Version CurrentVersion { get; } = ResolveCurrentVersion();

    /// <summary>최신 릴리즈가 현재 버전보다 높으면 그 정보를, 아니면 null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var stream = await http.GetStreamAsync(latestReleaseApi, cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (tag == null || !TryParseVersion(tag, out var remote))
                return null;

            if (Normalize(remote) <= Normalize(CurrentVersion))
                return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;

                if (name != null && url != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo(remote, tag, url, name);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// setup exe를 %TEMP%에 받아 실행한다. 성공하면 true — 호출부는 즉시 런처를 종료해야
    /// 인스톨러가 파일을 교체할 수 있다(인스톨러 자체도 CloseApplications로 남은 프로세스를 정리한다).
    /// </summary>
    public static async Task<bool> DownloadAndRunAsync(UpdateInfo info)
    {
        var progress = new DownloadWindow(info.Version);
        progress.Show();

        try
        {
            string target = Path.Combine(Path.GetTempPath(), info.FileName);

            using (var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                long? total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var destination = File.Create(target);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    if (total is > 0)
                        progress.Report((double)received / total.Value);
                }
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            progress.Close();
        }
    }

    private static Version ResolveCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational != null && TryParseVersion(informational, out var fromInformational))
            return fromInformational;

        return assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        // "6.6.3+abcdef", "6.6.3-beta" 같은 SemVer 꼬리표 제거
        int cut = trimmed.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0)
            trimmed = trimmed[..cut];

        return Version.TryParse(trimmed, out version!);
    }

    private static Version Normalize(Version value) =>
        new(value.Major, value.Minor, Math.Max(value.Build, 0));

    private sealed class DownloadWindow : Window
    {
        private readonly ProgressBar bar;

        public DownloadWindow(Version version)
        {
            Title = "LazerSR 업데이트";
            Width = 340;
            Height = 120;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true, Height = 20 };

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"버전 {version} 다운로드 중…",
                        Margin = new Thickness(0, 0, 0, 10),
                    },
                    bar,
                },
            };
        }

        public void Report(double fraction) => Dispatcher.Invoke(() =>
        {
            bar.IsIndeterminate = false;
            bar.Value = Math.Clamp(fraction, 0, 1);
        });
    }
}
