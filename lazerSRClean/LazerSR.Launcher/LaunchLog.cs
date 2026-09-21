using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace LazerSR.Launcher;

/// <summary>
/// 런처 시작 진단 로그 — <c>%LocalAppData%\LazerSR\launcher.log</c>.
/// 2026-09-21 v8.0.0이 일부 PC에서 "더블클릭해도 무반응"이었는데 흔적이 전혀 없어 추가했다.
/// 매 실행마다 단계를 적고, 처리 안 된 예외는 로그 + 메시지 박스로 남긴다. 로그 실패는 조용히 무시한다.
/// </summary>
public static class LaunchLog
{
    private const long max_bytes = 512 * 1024;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LazerSR", "launcher.log");

    private static readonly object gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > max_bytes)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    /// <summary>프로세스 시작 직후 1회. 환경 정보 + 전역 예외 핸들러 설치.</summary>
    public static void Start()
    {
        Write("==================== launcher start ====================");
        Write($"version={typeof(LaunchLog).Assembly.GetName().Version} runtime={RuntimeInformation.FrameworkDescription} " +
              $"os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture}");
        Write($"exe={Environment.ProcessPath} base={AppContext.BaseDirectory} temp={Path.GetTempPath()} admin={IsAdmin()}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write($"UnobservedTaskException: {e.Exception}");
    }

    public static void AttachDispatcher(Dispatcher dispatcher)
    {
        dispatcher.UnhandledException += (_, e) =>
        {
            Fatal("Dispatcher.UnhandledException", e.Exception);
            e.Handled = false; // 기존처럼 프로세스는 종료 — 단 이제는 로그와 메시지를 남긴다
        };
    }

    private static void Fatal(string source, Exception? ex)
    {
        Write($"FATAL {source}: {ex}");
        try
        {
            MessageBox.Show(
                $"LazerSR 런처가 오류로 종료됩니다.\n\n{ex?.GetType().Name}: {ex?.Message}\n\n로그: {FilePath}",
                "LazerSR 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
        }
    }

    private static bool IsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
