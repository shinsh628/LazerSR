using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LazerSR.SunnyCalculator.Tuning;

namespace LazerSR.Hook.PersonalSunny;

/// <summary>
/// The "skill ceiling" pool: the player's top <see cref="Capacity"/> charts by
/// <see cref="PersonalSunnyTopPoolEntry.Performance"/> (a PP-shaped value from sunny SR + accuracy, not
/// raw SR - see that property's doc for why, and the 2026-08-21 design discussion for the full reasoning).
/// An ordered map - <see cref="Dictionary{TKey,TValue}"/> for O(1) "is this chart already in the pool"
/// lookups, paired with a <see cref="SortedSet{T}"/> ordered by that value for O(log n) min-eviction.
/// <para>
/// Replaying a chart already in the pool only replaces its entry if the new attempt's <see cref="PersonalSunnyTopPoolEntry.Performance"/>
/// is higher (2026-08-22 fix) - the chart's SR is fixed by (map, rate, chart mod) and the current
/// <see cref="UniversalDiff"/>, but accuracy isn't, so <see cref="PersonalSunnyTopPoolEntry.Performance"/>
/// moves with every attempt. Unconditionally overwriting on every replay let a worse retry silently erase
/// a personal best - confirmed on real data: a 95.0% play followed an hour later by a 92.3% retry on the
/// same chart left only the 92.3% one in the pool, which is exactly backwards for a "skill ceiling" pool.
/// </para>
/// <para>
/// This is the "top <see cref="Capacity"/>" side of the two-pool design; <see cref="PersonalSunnyQueueStore"/> (recency FIFO)
/// is the "recent 100" side. Complementary, not a replacement - see the 2026-08-20 design discussion for
/// why both are needed (SR alone gives too narrow a spread for the ridge fit).
/// </para>
/// <c>%LocalAppData%\LazerSR\personalsunny\top_pool.json</c>.
/// </summary>
public static class PersonalSunnyTopPoolStore
{
    public const int Capacity = 200;

    /// <summary>
    /// Bump whenever a change to this pool's capacity, ranking formula, or eviction/replace semantics
    /// would make an already-saved file inconsistent with the new logic - <see cref="Offer"/> only ever
    /// improves the pool in place, so unlike <see cref="PersonalSunnyQueueStore"/> (fully replaced on
    /// every collect), a stale file doesn't self-correct on its own without this. 2 as of 2026-08-22
    /// (bumped for the 300->200 capacity cut - a 1-tagged file could be carrying up to 300 entries under
    /// logic that now only trusts 200).
    /// </summary>
    private const int schema_version = 2;

    private const string folder_name = "personalsunny";
    private const string file_name = "top_pool.json";

    private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Every mutation (both collections must move together, so a plain lock is simpler and safer here
    /// than trying to keep two independently-thread-safe collections in sync) goes through this.
    /// </summary>
    private static readonly object mutation_lock = new();

    private static readonly Dictionary<PersonalSunnyJacKey, PersonalSunnyTopPoolEntry> byKey;
    private static readonly SortedSet<(double Performance, PersonalSunnyJacKey Key)> ordered = new(new PerformanceOrderComparer());

    static PersonalSunnyTopPoolStore()
    {
        byKey = load();

        foreach (var entry in byKey.Values)
            ordered.Add((entry.Performance, entry.Key));
    }

    public static IReadOnlyList<PersonalSunnyTopPoolEntry> Entries
    {
        get
        {
            lock (mutation_lock)
                return byKey.Values.ToList();
        }
    }

    public static bool Contains(PersonalSunnyJacKey key)
    {
        lock (mutation_lock)
            return byKey.ContainsKey(key);
    }

    /// <summary>
    /// Empties the pool. Unlike <see cref="PersonalSunnyQueueStore"/>, which <see cref="PersonalSunnyService.ReplaceQueueAndRun"/>
    /// already fully replaces on every collect, <see cref="Offer"/> only ever improves this pool in place
    /// (a worse candidate is rejected, never wipes anything) - so pool logic changes (capacity, ranking
    /// formula) don't retroactively fix an already-saved pool on their own. The manual "리플레이 수집"
    /// button calls this before re-collecting so a full re-run is guaranteed to reflect current logic
    /// exactly, not whatever converges from the old saved state (2026-08-22).
    /// </summary>
    public static void Clear()
    {
        lock (mutation_lock)
        {
            byKey.Clear();
            ordered.Clear();
            save();
        }
    }

    /// <summary>
    /// Offers a chart at a known SR/accuracy. Three outcomes: the chart is already in the pool and this
    /// attempt's <see cref="PersonalSunnyTopPoolEntry.Performance"/> beats what's stored (entry replaced
    /// in place, no slot consumed - a worse repeat is rejected instead); the pool has room or this entry's
    /// Performance beats the current minimum (inserted, evicting the minimum if the pool was full); or
    /// neither (rejected, pool unchanged). Returns true if the pool actually changed.
    /// </summary>
    /// <param name="save">
    /// <see langword="false"/> lets a bulk caller (broad-phase's <c>Parallel.ForEach</c>) skip the
    /// per-item full-pool rewrite and call <see cref="Flush"/> once after the batch instead - see
    /// <see cref="PersonalSunnyChartSrStore.Put"/>'s doc for why this matters at scale (2026-08-22 fix).
    /// </param>
    public static bool Offer(PersonalSunnyTopPoolEntry entry, bool save = true)
    {
        lock (mutation_lock)
        {
            var key = entry.Key;

            if (byKey.TryGetValue(key, out var existing))
            {
                if (entry.Performance <= existing.Performance)
                    return false;

                ordered.Remove((existing.Performance, key));
                byKey[key] = entry;
                ordered.Add((entry.Performance, key));
                if (save)
                    PersonalSunnyTopPoolStore.save();
                return true;
            }

            if (byKey.Count < Capacity)
            {
                byKey[key] = entry;
                ordered.Add((entry.Performance, key));
                if (save)
                    PersonalSunnyTopPoolStore.save();
                return true;
            }

            var min = ordered.Min;

            if (entry.Performance <= min.Performance)
                return false;

            ordered.Remove(min);
            byKey.Remove(min.Key);

            byKey[key] = entry;
            ordered.Add((entry.Performance, key));
            if (save)
                PersonalSunnyTopPoolStore.save();
            return true;
        }
    }

    /// <summary>Persists the pool immediately - pair with <see cref="Offer"/>'s <c>save: false</c> after a batch of offers.</summary>
    public static void Flush()
    {
        lock (mutation_lock)
            save();
    }

    private static string currentDiffVersion() => string.Join(",", UniversalDiff.Deltas.Select(d => d.ToString("R")));

    private static string filePath()
    {
        string folder = LazerSrStorage.GetFolder(folder_name);
        return string.IsNullOrEmpty(folder) ? string.Empty : Path.Combine(folder, file_name);
    }

    private static Dictionary<PersonalSunnyJacKey, PersonalSunnyTopPoolEntry> load()
    {
        var result = new Dictionary<PersonalSunnyJacKey, PersonalSunnyTopPoolEntry>();

        string path = filePath();
        if (string.IsNullOrEmpty(path))
            return result;

        string? text = LazerSrStorage.ReadText(path);
        if (string.IsNullOrEmpty(text))
            return result;

        try
        {
            var file = JsonSerializer.Deserialize<PoolFile>(text, json_options);

            if (file == null || file.Version != schema_version || file.DiffVersion != currentDiffVersion() || file.Entries == null)
                return result;

            foreach (var entry in file.Entries)
            {
                if (!string.IsNullOrEmpty(entry.BeatmapMd5))
                    result[entry.Key] = entry;
            }

            return result;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyTopPoolStore load failed: {e}");
            return new Dictionary<PersonalSunnyJacKey, PersonalSunnyTopPoolEntry>();
        }
    }

    /// <summary>Caller must already hold <see cref="mutation_lock"/>.</summary>
    private static void save()
    {
        string path = filePath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            string text = JsonSerializer.Serialize(new PoolFile(schema_version, currentDiffVersion(), byKey.Values.ToList()), json_options);
            LazerSrStorage.WriteText(path, text);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyTopPoolStore save failed: {e}");
        }
    }

    /// <summary>Performance ascending, chart key string as a stable tie-break (<see cref="PersonalSunnyJacKey"/> isn't itself comparable).</summary>
    private class PerformanceOrderComparer : IComparer<(double Performance, PersonalSunnyJacKey Key)>
    {
        public int Compare((double Performance, PersonalSunnyJacKey Key) x, (double Performance, PersonalSunnyJacKey Key) y)
        {
            int cmp = x.Performance.CompareTo(y.Performance);
            return cmp != 0 ? cmp : string.CompareOrdinal(x.Key.ToString(), y.Key.ToString());
        }
    }

    private class PoolFile
    {
        public PoolFile()
        {
        }

        public PoolFile(int version, string diffVersion, List<PersonalSunnyTopPoolEntry>? entries)
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
        public List<PersonalSunnyTopPoolEntry>? Entries { get; set; }
    }
}
