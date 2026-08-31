using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using osu.Game.Beatmaps;
using osu.Game.Models;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerSR.Hook.LazerSrLeaderboard;

/// <summary>
/// 우리 서버가 준 리더보드 JSON(<c>{"scores":[...]}</c>)을 osu의 <see cref="ScoreInfo"/> 배열로 바꾼다.
/// osu 리더보드/결과창 파이프라인이 그대로 소비할 수 있는 형태로 채운다.
/// <para>
/// <c>Hash</c>에 <c>lazersr:{guid}</c> 마커를 넣어 두면 리플레이 다운로드 패치가 우리 스코어임을
/// 알아보고 서버에서 <c>.osr</c>을 받아 온다.
/// </para>
/// </summary>
internal static class LazerSrScoreFactory
{
    public const string HashPrefix = "lazersr:";

    public static bool IsOurs(ScoreInfo score) =>
        score.Hash?.StartsWith(HashPrefix, StringComparison.Ordinal) == true;

    public static string? ExtractGuid(ScoreInfo score) =>
        IsOurs(score) ? score.Hash[HashPrefix.Length..] : null;

    /// <summary>파싱/변환 실패 시 빈 배열. 예외를 밖으로 내보내지 않는다.</summary>
    public static ScoreInfo[] Parse(string json, BeatmapInfo beatmap, RulesetInfo maniaRuleset)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("scores", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<ScoreInfo>();

            var mania = maniaRuleset.CreateInstance();
            var result = new List<ScoreInfo>();
            int position = 1;

            foreach (var row in arr.EnumerateArray())
            {
                try
                {
                    result.Add(build(row, beatmap, maniaRuleset, mania, position++));
                }
                catch (Exception e)
                {
                    HookLog.Write($"[LazerSR] LazerSrScoreFactory: row skipped: {e}");
                }
            }

            return result.ToArray();
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrScoreFactory.Parse failed: {e}");
            return Array.Empty<ScoreInfo>();
        }
    }

    private static ScoreInfo build(JsonElement row, BeatmapInfo beatmap, RulesetInfo maniaRuleset, Ruleset mania, int position)
    {
        string guid = row.GetProperty("score_guid").GetString() ?? throw new InvalidOperationException("no score_guid");

        int userId = row.TryGetProperty("osu_user_id", out var uid) && uid.ValueKind == JsonValueKind.Number ? uid.GetInt32() : 1;
        string username = row.TryGetProperty("username", out var un) ? un.GetString() ?? "?" : "?";

        var score = new ScoreInfo(beatmap, maniaRuleset, new RealmUser { OnlineID = userId > 1 ? userId : 1, Username = username })
        {
            Hash = HashPrefix + guid,
            BeatmapHash = beatmap.Hash,
            HasOnlineReplay = true,
            Position = position,
            TotalScore = getLong(row, "total_score"),
            MaxCombo = (int)getLong(row, "max_combo"),
            Accuracy = getDouble(row, "accuracy"),
            Rank = parseRank(row.TryGetProperty("rank", out var rk) ? rk.GetString() : null),
            Date = parseDate(row.TryGetProperty("played_at", out var pa) ? pa.GetString() : null),
            Statistics = new Dictionary<HitResult, int>
            {
                [HitResult.Perfect] = (int)getLong(row, "count_perfect"),
                [HitResult.Great] = (int)getLong(row, "count_great"),
                [HitResult.Good] = (int)getLong(row, "count_good"),
                [HitResult.Ok] = (int)getLong(row, "count_ok"),
                [HitResult.Meh] = (int)getLong(row, "count_meh"),
                [HitResult.Miss] = (int)getLong(row, "count_miss"),
            },
        };

        if (row.TryGetProperty("pp", out var pp) && pp.ValueKind == JsonValueKind.Number)
            score.PP = pp.GetDouble();

        var mods = new List<Mod>();
        if (row.TryGetProperty("mods", out var modArr) && modArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in modArr.EnumerateArray())
            {
                string? acr = m.GetString();
                if (string.IsNullOrEmpty(acr)) continue;
                var mod = mania.CreateModFromAcronym(acr);
                if (mod != null) mods.Add(mod);
            }
        }
        score.Mods = mods.ToArray();

        return score;
    }

    private static long getLong(JsonElement row, string name) =>
        row.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;

    private static double getDouble(JsonElement row, string name) =>
        row.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

    private static ScoreRank parseRank(string? s) =>
        Enum.TryParse<ScoreRank>(s, out var r) ? r : ScoreRank.D;

    private static DateTimeOffset parseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.UtcNow;
}
