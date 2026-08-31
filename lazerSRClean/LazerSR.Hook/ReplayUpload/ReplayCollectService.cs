using System;
using System.Linq;
using osu.Game.Scoring;

namespace LazerSR.Hook.ReplayUpload;

/// <summary>
/// 트리거 #1 — 런처 "리플레이 수집" 버튼. 로컬 realm을 훑어 <b>지금 로그인된 계정이 친</b> mania
/// 리플레이만 골라 큐에 쌓는다. 모드·정확도·성공 여부는 안 가린다.
/// <para>
/// <b>남의 리플레이 배제(2중 검사, 둘 다 통과해야 함)</b>:
/// (1) <c>ScoreInfo.RealmUser.OnlineID == 로그인 유저 id</c>,
/// (2) <c>.osr</c> 헤더에 박힌 플레이어 이름 == 로그인 유저네임.
/// osu!는 랭킹창에서 남의 리플레이를 받아 로컬 realm에 저장할 수 있는데, 그 경우 (2)에서 걸린다.
/// </para>
/// </summary>
internal static class ReplayCollectService
{
    /// <summary>큐에 쓴 개수. realm/storage/api가 아직 준비 안 됐거나 로그인이 안 돼 있으면 null.</summary>
    public static int? CollectAll()
    {
        var realm = HookRuntimeContext.Realm;
        var storage = HookRuntimeContext.Storage;
        var api = HookRuntimeContext.Api;
        if (realm == null || storage == null || api == null) return null;

        int localUserId = api.LocalUser.Value.Id;
        string localUsername = api.LocalUser.Value.Username;
        if (localUserId <= 1 || string.IsNullOrEmpty(localUsername)) return null; // 게스트/오프라인 = 아무것도 안 함

        ReplayQueueWriter.ClearQueue();

        int written = 0;

        realm.Run(r =>
        {
            var scores = r.All<ScoreInfo>().Where(s => !s.DeletePending).ToList();

            foreach (var score in scores)
            {
                try
                {
                    if (score.Ruleset.OnlineID != 3) continue;            // mania만
                    if (score.RealmUser.OnlineID != localUserId) continue; // 소유권 검사 1

                    string? replayPath = ReplayQueueWriter.ResolveReplayPath(score, storage);
                    if (replayPath == null) continue;

                    if (!OsrHeader.PlayerNameMatches(replayPath, localUsername)) continue; // 소유권 검사 2

                    if (ReplayQueueWriter.WriteEntry(score, replayPath))
                        written++;
                }
                catch (Exception e)
                {
                    HookLog.Write($"[LazerSR] ReplayCollectService: score {score.ID} skipped: {e}");
                }
            }
        });

        return written;
    }
}
