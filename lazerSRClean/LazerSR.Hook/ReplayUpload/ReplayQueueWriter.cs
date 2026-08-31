using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Extensions;
using osu.Game.Online.API;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerSR.Hook.ReplayUpload;

/// <summary>
/// 큐 폴더(<c>%LocalAppData%\LazerSR\replayupload\</c>)에 <c>{score_guid}.json</c> 엔트리를 쓴다.
/// 두 트리거(런처 수집 버튼 / 결과창 자동 전송)가 공용으로 쓰는 유일한 작성 지점.
/// <b>여기는 읽기 + 로컬 쓰기만</b> — 실제 업로드(네트워크)는 런처가 한다.
/// </summary>
internal static class ReplayQueueWriter
{
    private const int schema_version = 1;
    private const string queue_folder = "replayupload";

    /// <summary>이번 스캔 결과만 큐에 남기기 위해 전체를 비운다. 런처는 큐 폴더 내용을 소유자
    /// 구분 없이 다 올리므로, 걸러진 채로 남는 파일이 없어야 한다(과거 버그의 직접 원인).</summary>
    public static void ClearQueue()
    {
        try
        {
            string folder = LazerSrStorage.GetFolder(queue_folder);
            if (string.IsNullOrEmpty(folder)) return;

            foreach (string file in Directory.GetFiles(folder, "*.json"))
            {
                try { File.Delete(file); }
                catch (Exception e) { HookLog.Write($"[LazerSR] ReplayQueueWriter.ClearQueue: {file}: {e}"); }
            }
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] ReplayQueueWriter.ClearQueue failed: {e}");
        }
    }

    /// <summary><paramref name="score"/>의 <c>.osr</c> 파일 절대경로. 없으면 null.</summary>
    public static string? ResolveReplayPath(ScoreInfo score, Storage storage)
    {
        try
        {
            var replayFile = score.Files.FirstOrDefault(
                f => f.Filename.EndsWith(".osr", StringComparison.OrdinalIgnoreCase));
            if (replayFile == null) return null;

            string path = storage.GetStorageForDirectory("files").GetFullPath(replayFile.File.GetStoragePath());
            return File.Exists(path) ? path : null;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] ReplayQueueWriter.ResolveReplayPath({score.ID}) failed: {e}");
            return null;
        }
    }

    /// <summary>큐 엔트리 1개 작성. 성공하면 true.</summary>
    public static bool WriteEntry(ScoreInfo score, string replayAbsPath)
    {
        try
        {
            string folder = LazerSrStorage.GetFolder(queue_folder);
            if (string.IsNullOrEmpty(folder)) return false;

            var beatmap = score.BeatmapInfo;
            var stats = score.Statistics;

            var entry = new
            {
                schema_version,
                score_guid = score.ID.ToString(),
                osu_username = score.RealmUser.Username,
                beatmap_md5 = score.BeatmapHash,
                played_at = score.Date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                mods = score.APIMods.Select(m => new { acronym = m.Acronym }).ToArray(),
                rate = GetSpeedChange(score.APIMods) ?? 1.0,
                passed = score.Passed,
                rank = score.Rank.ToString(),
                max_combo = score.MaxCombo,
                accuracy = score.Accuracy,
                total_score = score.TotalScore,
                count_perfect = stats.GetValueOrDefault(HitResult.Perfect),
                count_great = stats.GetValueOrDefault(HitResult.Great),
                count_good = stats.GetValueOrDefault(HitResult.Good),
                count_ok = stats.GetValueOrDefault(HitResult.Ok),
                count_meh = stats.GetValueOrDefault(HitResult.Meh),
                count_miss = stats.GetValueOrDefault(HitResult.Miss),
                pp = score.PP,
                replay_path = replayAbsPath,
                beatmap = beatmap == null
                    ? null
                    : new
                    {
                        beatmap_id = beatmap.OnlineID > 0 ? beatmap.OnlineID : (int?)null,
                        beatmapset_id = beatmap.BeatmapSet?.OnlineID > 0 ? beatmap.BeatmapSet!.OnlineID : (int?)null,
                        artist = beatmap.Metadata?.Artist,
                        title = beatmap.Metadata?.Title,
                        creator = beatmap.Metadata?.Author?.Username,
                        difficulty_name = beatmap.DifficultyName,
                        key_count = (int)beatmap.Difficulty.CircleSize,
                        status = MapStatus(beatmap.Status),
                        object_count = beatmap.TotalObjectCount >= 0 ? beatmap.TotalObjectCount : (int?)null,
                        drain_time_ms = beatmap.Length > 0 ? (long)beatmap.Length : (long?)null,
                    },
            };

            string path = Path.Combine(folder, $"{score.ID}.json");
            return LazerSrStorage.WriteText(path, JsonSerializer.Serialize(entry));
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] ReplayQueueWriter.WriteEntry({score.ID}) failed: {e}");
            return false;
        }
    }

    private static string MapStatus(BeatmapOnlineStatus status) => status switch
    {
        BeatmapOnlineStatus.Ranked => "ranked",
        BeatmapOnlineStatus.Approved => "ranked",
        BeatmapOnlineStatus.Qualified => "ranked",
        BeatmapOnlineStatus.Loved => "loved",
        BeatmapOnlineStatus.Graveyard => "graveyard",
        _ => "unknown",
    };

    private static double? GetSpeedChange(IEnumerable<APIMod> mods)
    {
        foreach (var m in mods)
        {
            if (m.Settings.TryGetValue("speed_change", out var v) && v is double d)
                return d;
        }
        return null;
    }
}
