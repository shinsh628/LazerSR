using System.Windows;
using LazerSR.Launcher.Update;

namespace LazerSR.Launcher;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // GUI가 뜨기 전에 업데이트를 검사한다. 아직 창이 없으므로 검사 도중 종료되지 않게 잡아둔다.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update != null && await PromptAndUpdateAsync(update))
                return; // 업데이트 진행 — 인스톨러가 뜨고 프로세스는 종료됨
        }
        catch
        {
            // 업데이트 경로에서 무슨 일이 있어도 런처는 평소대로 실행한다
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;
        new MainWindow().Show();
    }

    private static async Task<bool> PromptAndUpdateAsync(UpdateInfo update)
    {
        var choice = MessageBox.Show(
            $"새 버전 {update.Version} 이(가) 있습니다. (현재 {UpdateChecker.CurrentVersion})\n\n지금 업데이트할까요?",
            "LazerSR 업데이트",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (choice != MessageBoxResult.Yes)
            return false;

        if (await UpdateChecker.DownloadAndRunAsync(update))
        {
            Current.Shutdown();
            Environment.Exit(0);
            return true;
        }

        MessageBox.Show(
            "업데이트를 시작하지 못했습니다. 그대로 실행합니다.",
            "LazerSR 업데이트",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }
}
