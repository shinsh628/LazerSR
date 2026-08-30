using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Extensions;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerSR.Hook.ReplayUpload;

/// <summary>
/// "일괄 리플레이 동기화" 버튼 하나가 트리거하는 전부. 로컬 realm에서 리플레이 파일이 붙은
/// mania 점수를 전부 훑어 <see cref="LazerSrStorage"/> 큐 폴더에 메타데이터를 써둔다.
/// 실제 업로드(네트워크)는 Launcher가 한다 — 여기는 읽기 + 로컬 쓰기만.
/// </summary>
internal static class ReplayUploadService
{
    private const int SCHEMA_VERSION = 1;

    /// <summary>
    /// 성공하면 큐에 쓴 개수, realm/storage가 아직 준비 안 됐으면 null.
    /// </summary>
    public static int? EnqueueAllLocalMania()
    {
        var realm = HookRuntimeContext.Realm;
        var storage = HookRuntimeContext.Storage;
        var api = HookRuntimeContext.Api;
        if (realm == null || storage == null || api == null) return null;

        // 남의 리플레이를 인게임에서 열어보면 그것도 로컬 realm에 ScoreInfo로 남는다 - 지금
        // osu!에 로그인된 계정 것만 골라야 한다. 로그인 안 돼 있으면(오프라인/게스트) 아무것도 안 보낸다.
        int localUserId = api.LocalUser.Value.Id;
        if (localUserId <= 0) return null;

        string folder = LazerSrStorage.GetFolder("replayupload");
        if (string.IsNullOrEmpty(folder)) return 0;

        var filesStorage = storage.GetStorageForDirectory("files");
        int written = 0;

        realm.Run(r =>
        {
            var scores = r.All<ScoreInfo>()
                .Where(s => !s.DeletePending)
                .ToList();

            foreach (var score in scores)
            {
                try
                {
                    if (score.Ruleset.OnlineID != 3) continue; // mania만
                    if (score.RealmUser.OnlineID != localUserId) continue; // 남의 리플레이 제외

                    var replayFile = score.Files.FirstOrDefault(
                        f => f.Filename.EndsWith(".osr", StringComparison.OrdinalIgnoreCase));
                    if (replayFile == null) continue;

                    string replayPath = filesStorage.GetFullPath(replayFile.File.GetStoragePath());
                    if (!File.Exists(replayPath)) continue;

                    if (WriteQueueEntry(score, replayPath))
                        written++;
                }
                catch (Exception e)
                {
                    HookLog.Write($"[LazerSR] ReplayUploadService: score {score.ID} skipped: {e}");
                }
            }
        });

        return written;
    }

    private static bool WriteQueueEntry(ScoreInfo score, string replayPath)
    {
        var beatmap = score.BeatmapInfo;
        var stats = score.Statistics;

        var entry = new
        {
            schema_version = SCHEMA_VERSION,
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
            replay_path = replayPath,
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

        string path = Path.Combine(LazerSrStorage.GetFolder("replayupload"), $"{score.ID}.json");
        return LazerSrStorage.WriteText(path, JsonSerializer.Serialize(entry));
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

    private static double? GetSpeedChange(IEnumerable<osu.Game.Online.API.APIMod> mods)
    {
        foreach (var m in mods)
        {
            if (m.Settings.TryGetValue("speed_change", out var v) && v is double d)
                return d;
        }
        return null;
    }
}
