using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LazerSR.SunnyCalculator.Tuning;

namespace LazerSR.Hook.PersonalSunny;

/// <summary>
/// Cheap broad-phase cache: one universal-point sunny SR per chart (<see cref="PersonalJacobianBaker.CalculateUniversalSr"/>),
/// keyed the same way as <see cref="PersonalSunnyJacStore"/> but far lighter - a single double, not an
/// 11-dim Jacobian. This is what a broad-phase ranking pass reads/writes so a chart's SR is computed at
/// most once, ever, regardless of how many times it gets re-ranked across sessions.
/// <c>%LocalAppData%\LazerSR\personalsunny\chart_sr_cache.json</c>.
/// <para>
/// Separate file from <see cref="PersonalSunnyJacStore"/> on purpose - most candidates a broad-phase pass
/// looks at never make the top-300/recent-100 cut and so never need a full Jacobian bake; this cache only
/// ever holds the cheap value, for all of them, not just the survivors.
/// </para>
/// <para>
/// Thread-safe: broad-phase ranking runs candidates in parallel, so <see cref="TryGet"/>/<see cref="Put"/>
/// can be called concurrently from multiple threads.
/// </para>
/// </summary>
public static class PersonalSunnyChartSrStore
{
    private const int schema_version = 1;
    private const string folder_name = "personalsunny";
    private const string file_name = "chart_sr_cache.json";

    private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object save_lock = new();

    private static readonly ConcurrentDictionary<PersonalSunnyJacKey, double> cache = load();

    public static bool TryGet(PersonalSunnyJacKey key, out double sr) => cache.TryGetValue(key, out sr);

    /// <param name="save">
    /// <see langword="false"/> lets a bulk caller (broad-phase's <c>Parallel.ForEach</c>) skip the
    /// per-item full-cache rewrite and call <see cref="Flush"/> once after the batch instead - saving on
    /// every item is an O(n) rewrite done n times (O(n^2) total, plus lock contention across threads) once
    /// the cache holds thousands of entries (2026-08-22 fix).
    /// </param>
    public static void Put(PersonalSunnyJacKey key, double sr, bool save = true)
    {
        cache[key] = sr;
        if (save)
            PersonalSunnyChartSrStore.save();
    }

    /// <summary>Persists the cache immediately - pair with <see cref="Put"/>'s <c>save: false</c> after a batch of puts.</summary>
    public static void Flush() => save();

    /// <summary>Drops cached entries no longer relevant - mirrors <see cref="PersonalSunnyJacStore.PruneTo"/>.</summary>
    public static void PruneTo(IEnumerable<PersonalSunnyJacKey> keysStillInUse)
    {
        var keep = new HashSet<PersonalSunnyJacKey>(keysStillInUse);
        var stale = cache.Keys.Where(k => !keep.Contains(k)).ToList();

        if (stale.Count == 0)
            return;

        foreach (var key in stale)
            cache.TryRemove(key, out _);

        save();
    }

    /// <summary>
    /// Stable stringified snapshot of <see cref="UniversalDiff.Deltas"/> - a mismatch on load means the
    /// universal diff has been retuned since this cache was written, so every cached SR is stale (sunny's
    /// output for the same chart would now differ). No hashing - direct string comparison, so there's no
    /// stability-across-runs question to worry about.
    /// </summary>
    private static string currentDiffVersion() => string.Join(",", UniversalDiff.Deltas.Select(d => d.ToString("R")));

    private static string filePath()
    {
        string folder = LazerSrStorage.GetFolder(folder_name);
        return string.IsNullOrEmpty(folder) ? string.Empty : Path.Combine(folder, file_name);
    }

    private static ConcurrentDictionary<PersonalSunnyJacKey, double> load()
    {
        var result = new ConcurrentDictionary<PersonalSunnyJacKey, double>();

        string path = filePath();
        if (string.IsNullOrEmpty(path))
            return result;

        string? text = LazerSrStorage.ReadText(path);
        if (string.IsNullOrEmpty(text))
            return result;

        try
        {
            var file = JsonSerializer.Deserialize<SrFile>(text, json_options);

            if (file == null || file.Version != schema_version || file.DiffVersion != currentDiffVersion() || file.Entries == null)
                return result;

            foreach (var entry in file.Entries)
            {
                if (!string.IsNullOrEmpty(entry.BeatmapMd5))
                    result[entry.Key] = entry.Sr;
            }

            return result;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyChartSrStore load failed: {e}");
            return new ConcurrentDictionary<PersonalSunnyJacKey, double>();
        }
    }

    private static void save()
    {
        lock (save_lock)
        {
            string path = filePath();
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var entries = cache.Select(kv => new PersonalSunnyChartSrEntry(kv.Key.BeatmapMd5, kv.Key.Rate, kv.Key.ChartMod, kv.Value)).ToList();
                string text = JsonSerializer.Serialize(new SrFile(schema_version, currentDiffVersion(), entries), json_options);
                LazerSrStorage.WriteText(path, text);
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] PersonalSunnyChartSrStore save failed: {e}");
            }
        }
    }

    private class SrFile
    {
        public SrFile()
        {
        }

        public SrFile(int version, string diffVersion, List<PersonalSunnyChartSrEntry>? entries)
        {
            Version = version;
            DiffVersion = diffVersion;
            Entries = entries;
        }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("diffVersion")]
        public string? DiffVersion { get; set; }

        [JsonPropertyName("entries")]
        public List<PersonalSunnyChartSrEntry>? Entries { get; set; }
    }
}
