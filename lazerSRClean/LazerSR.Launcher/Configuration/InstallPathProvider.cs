using System.IO;

namespace LazerSR.Launcher.Configuration;

public sealed class InstallPathProvider
{
    private static readonly string[] autoDetectCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osulazer", "current"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "osu!"),
    ];

    private static readonly string[] osuExecutableNames = ["osu!.exe", "osu.exe"];

    private readonly LauncherSettingsStore settingsStore;

    public InstallPathProvider(LauncherSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
        Current = new LazerInstallLocation(null);
    }

    public LazerInstallLocation Current { get; private set; }

    public LazerInstallLocation Load()
    {
        var location = new LazerInstallLocation(settingsStore.Load().OsuLazerInstallPath);

        if (!location.IsValid)
            location = TryAutoDetect() ?? location;

        Current = location;
        return Current;
    }

    public LazerInstallLocation Save(string installPath)
    {
        Current = new LazerInstallLocation(installPath);
        settingsStore.Save(new LauncherSettings(Current.Path));
        return Current;
    }

    public static bool HasOsuExecutable(string path) =>
        osuExecutableNames.Any(name => File.Exists(Path.Combine(path, name)));

    private static LazerInstallLocation? TryAutoDetect()
    {
        foreach (string candidate in autoDetectCandidates)
        {
            if (!Directory.Exists(candidate))
                continue;

            foreach (string exeName in osuExecutableNames)
            {
                if (File.Exists(Path.Combine(candidate, exeName)))
                    return new LazerInstallLocation(candidate);
            }
        }

        return null;
    }
}
