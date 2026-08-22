namespace LazerSR.Hook.PersonalSunny;

/// <summary>One cheap broad-phase SR value (universal point, single sunny call) for one <see cref="PersonalSunnyJacKey"/>.</summary>
public record PersonalSunnyChartSrEntry(string BeatmapMd5, double Rate, string? ChartMod, double Sr)
{
    public PersonalSunnyJacKey Key => new(BeatmapMd5, Rate, ChartMod);
}
