using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazerSR.Hook.PersonalSunny;

/// <summary>
/// The latest personal fit result. <c>%LocalAppData%\LazerSR\personalsunny\fit.json</c>.
/// <para>
/// Persists <see cref="Alpha"/>/<see cref="Beta"/> alongside the deltas even though the offline
/// reference tool (<c>personal_fit.py</c>) discards them - the widget's "accuracy 95% at what SR"
/// figure is their inverse (<c>SR = (yTarget - alpha) / beta</c>), so they have to survive a restart
/// just like the deltas do.
/// </para>
/// </summary>
public static class PersonalSunnyFitStore
{
    private const int schema_version = 1;
    private const string folder_name = "personalsunny";
    private const string file_name = "fit.json";

    private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public record FitResult(double[] UnitStep, double Alpha, double Beta, int RecordCount, DateTimeOffset ComputedAt);

    private static FitResult? cached;
    private static bool loaded;

    /// <summary><c>null</c> if no fit has ever run (or the file is missing/corrupt).</summary>
    public static FitResult? Current
    {
        get
        {
            if (!loaded)
            {
                cached = load();
                loaded = true;
            }

            return cached;
        }
    }

    public static void Save(FitResult result)
    {
        cached = result;
        loaded = true;

        string path = filePath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            string text = JsonSerializer.Serialize(new FitFile(schema_version, result), json_options);
            LazerSrStorage.WriteText(path, text);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyFitStore save failed: {e}");
        }
    }

    private static string filePath()
    {
        string folder = LazerSrStorage.GetFolder(folder_name);
        return string.IsNullOrEmpty(folder) ? string.Empty : Path.Combine(folder, file_name);
    }

    private static FitResult? load()
    {
        string path = filePath();
        if (string.IsNullOrEmpty(path))
            return null;

        string? text = LazerSrStorage.ReadText(path);
        if (string.IsNullOrEmpty(text))
            return null;

        try
        {
            var file = JsonSerializer.Deserialize<FitFile>(text, json_options);

            if (file == null || file.Version != schema_version || file.Result?.UnitStep == null)
                return null;

            return file.Result;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyFitStore load failed: {e}");
            return null;
        }
    }

    private class FitFile
    {
        public FitFile()
        {
        }

        public FitFile(int version, FitResult? result)
        {
            Version = version;
            Result = result;
        }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("result")]
        public FitResult? Result { get; set; }
    }
}
