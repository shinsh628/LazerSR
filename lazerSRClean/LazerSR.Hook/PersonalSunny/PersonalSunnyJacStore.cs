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
/// Baked Jacobians, keyed by <see cref="PersonalSunnyJacKey"/>. <c>%LocalAppData%\LazerSR\personalsunny\jac_cache.json</c>.
/// Separate file from the queue on purpose - a corrupt cache shouldn't take the queue down with it, and
/// it can always be rebaked; the queue can't be reconstructed once scores scroll out of local history.
/// Pruned to only the keys the current queue still references, so it never grows past what's in use.
/// <para>
/// Thread-safe: narrow-phase baking runs candidates in parallel, so <see cref="TryGet"/>/<see cref="Put"/>
/// can be called concurrently from multiple threads.
/// </para>
/// </summary>
public static class PersonalSunnyJacStore
{
    private const int schema_version = 1;
    private const string folder_name = "personalsunny";
    private const string file_name = "jac_cache.json";

    private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object save_lock = new();

    private static readonly ConcurrentDictionary<PersonalSunnyJacKey, PersonalSunnyJacEntry> cache = load();

    public static bool TryGet(PersonalSunnyJacKey key, out PersonalSunnyJacEntry entry) => cache.TryGetValue(key, out entry!);

    /// <param name="save">
    /// <see langword="false"/> lets a bulk caller (narrow-phase's <c>Parallel.ForEach</c>) skip the
    /// per-item full-cache rewrite and call <see cref="Flush"/> once after the batch instead - see
    /// <see cref="PersonalSunnyChartSrStore.Put"/>'s doc for why this matters at scale (2026-08-22 fix).
    /// </param>
    public static void Put(PersonalSunnyJacEntry entry, bool save = true)
    {
        cache[entry.Key] = entry;
        if (save)
            PersonalSunnyJacStore.save();
    }

    /// <summary>Persists the cache immediately - pair with <see cref="Put"/>'s <c>save: false</c> after a batch of puts.</summary>
    public static void Flush() => save();

    /// <summary>Drops cached entries no queued item references any more.</summary>
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
    /// universal diff has been retuned since this cache was written, so every baked Sr0/Jacobian is stale.
    /// </summary>
    private static string currentDiffVersion() => string.Join(",", UniversalDiff.Deltas.Select(d => d.ToString("R")));

    private static string filePath()
    {
        string folder = LazerSrStorage.GetFolder(folder_name);
        return string.IsNullOrEmpty(folder) ? string.Empty : Path.Combine(folder, file_name);
    }

    private static ConcurrentDictionary<PersonalSunnyJacKey, PersonalSunnyJacEntry> load()
    {
        var result = new ConcurrentDictionary<PersonalSunnyJacKey, PersonalSunnyJacEntry>();

        string path = filePath();
        if (string.IsNullOrEmpty(path))
            return result;

        string? text = LazerSrStorage.ReadText(path);
        if (string.IsNullOrEmpty(text))
            return result;

        try
        {
            var file = JsonSerializer.Deserialize<JacFile>(text, json_options);

            if (file == null || file.Version != schema_version || file.DiffVersion != currentDiffVersion() || file.Entries == null)
                return result;

            // A bad entry (wrong jacobian length, say) is dropped, not fatal to the rest.
            foreach (var entry in file.Entries)
            {
                if (!string.IsNullOrEmpty(entry.BeatmapMd5) && entry.Jacobian is { Length: > 0 })
                    result[entry.Key] = entry;
            }

            return result;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyJacStore load failed: {e}");
            return new ConcurrentDictionary<PersonalSunnyJacKey, PersonalSunnyJacEntry>();
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
                string text = JsonSerializer.Serialize(new JacFile(schema_version, currentDiffVersion(), cache.Values.ToList()), json_options);
                LazerSrStorage.WriteText(path, text);
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] PersonalSunnyJacStore save failed: {e}");
            }
        }
    }

    private class JacFile
    {
        public JacFile()
        {
        }

        public JacFile(int version, string diffVersion, List<PersonalSunnyJacEntry>? entries)
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
        public List<PersonalSunnyJacEntry>? Entries { get; set; }
    }
}
