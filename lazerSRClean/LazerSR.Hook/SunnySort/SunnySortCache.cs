using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazerSR.Hook.SunnySort;

/// <summary>
/// 맵당 오리지널 sunny SR 캐시. 키 = (SHA-256 <c>BeatmapInfo.Hash</c>, rate). rate는 1.0 / 0.75 / 1.5.
/// <c>%LocalAppData%\LazerSR\sunnysort\cache.json</c>. 전 유저 동일값이라 서버와 공유된다.
/// 스레드 안전(워커가 병렬은 아니지만 patch/widget이 동시에 읽는다).
/// </summary>
public static class SunnySortCache
{
    private const string folder_name = "sunnysort";
    private const string file_name = "cache.json";

    private static readonly JsonSerializerOptions json_options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object save_lock = new();
    private static readonly ConcurrentDictionary<string, Entry> map = load();

    private static string keyOf(string hash, double rate) =>
        $"{hash}|{rate.ToString("0.###", CultureInfo.InvariantCulture)}";

    public static bool TryGet(string hash, double rate, out double sr)
    {
        if (!string.IsNullOrEmpty(hash) && map.TryGetValue(keyOf(hash, rate), out var e))
        {
            sr = e.Sr;
            return true;
        }

        sr = 0;
        return false;
    }

    /// <summary>맵의 3개 rate가 전부 캐시돼 있으면 true — 워커 dedup용.</summary>
    public static bool HasAllRates(string hash) =>
        TryGet(hash, 1.0, out _) && TryGet(hash, 0.75, out _) && TryGet(hash, 1.5, out _);

    public static void Put(string hash, double rate, double sr, bool save = true)
    {
        if (string.IsNullOrEmpty(hash))
            return;

        map[keyOf(hash, rate)] = new Entry(hash, rate, sr, SunnySortState.CalcVersion);

        if (save)
            SunnySortCache.save();
    }

    public static void Flush() => save();

    /// <summary>캐시된 (map, rate) 항목 총수.</summary>
    public static int RateCount => map.Count;

    /// <summary>캐시에 최소 1개 rate가 있는 서로 다른 맵 수.</summary>
    public static int DistinctMapCount => map.Values.Select(e => e.Hash).Distinct().Count();

    public static IReadOnlyList<Entry> Snapshot() => map.Values.ToList();

    /// <summary>서버 덤프 JSON(<c>{"entries":[{beatmap_md5,rate,sr,calc_version}]}</c>)을 병합. 병합 건수 반환.</summary>
    public static int MergeServerJson(string json)
    {
        int merged = 0;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return 0;

            var pending = new List<Entry>();

            foreach (var el in arr.EnumerateArray())
            {
                string? h = el.TryGetProperty("beatmap_md5", out var hp) ? hp.GetString() : null;
                if (string.IsNullOrEmpty(h))
                    continue;
                if (!el.TryGetProperty("rate", out var rp) || !el.TryGetProperty("sr", out var sp))
                    continue;

                int cv = el.TryGetProperty("calc_version", out var cvp) && cvp.TryGetInt32(out int v) ? v : -1;
                if (cv != SunnySortState.CalcVersion)
                    continue;

                double rate = rp.GetDouble();
                if (map.ContainsKey(keyOf(h, rate)))
                    continue;

                pending.Add(new Entry(h, rate, sp.GetDouble(), cv));
            }

            foreach (var e in pending)
            {
                map[keyOf(e.Hash, e.Rate)] = e;
                merged++;
            }

            if (merged > 0)
                save();
        }
        catch (Exception)
        {
        }

        return merged;
    }

    private static string filePath()
    {
        string folder = LazerSrStorage.GetFolder(folder_name);
        return string.IsNullOrEmpty(folder) ? string.Empty : Path.Combine(folder, file_name);
    }

    private static ConcurrentDictionary<string, Entry> load()
    {
        var result = new ConcurrentDictionary<string, Entry>();

        string path = filePath();
        if (string.IsNullOrEmpty(path))
            return result;

        string? text = LazerSrStorage.ReadText(path);
        if (string.IsNullOrEmpty(text))
            return result;

        try
        {
            var file = JsonSerializer.Deserialize<CacheFile>(text, json_options);
            if (file?.Entries == null)
                return result;

            foreach (var e in file.Entries)
            {
                if (string.IsNullOrEmpty(e.Hash))
                    continue;

                // 계산기 버전이 다른 옛 항목은 버린다.
                if (e.CalcVersion != SunnySortState.CalcVersion)
                    continue;

                result[keyOf(e.Hash, e.Rate)] = e;
            }
        }
        catch (Exception)
        {
        }

        return result;
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
                var file = new CacheFile { Entries = map.Values.ToList() };
                LazerSrStorage.WriteText(path, JsonSerializer.Serialize(file, json_options));
            }
            catch (Exception)
            {
            }
        }
    }

    public sealed class Entry
    {
        public Entry()
        {
        }

        public Entry(string hash, double rate, double sr, int calcVersion)
        {
            Hash = hash;
            Rate = rate;
            Sr = sr;
            CalcVersion = calcVersion;
        }

        [JsonPropertyName("h")] public string Hash { get; set; } = string.Empty;
        [JsonPropertyName("r")] public double Rate { get; set; }
        [JsonPropertyName("sr")] public double Sr { get; set; }
        [JsonPropertyName("v")] public int CalcVersion { get; set; }
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("entries")] public List<Entry>? Entries { get; set; }
    }
}
