using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LazerSR.Hook.Ipc;
using LazerSR.Hook.PersonalSunny;
using LazerSR.Hook.ReplayUpload;
using LazerSR.SunnyCalculator;
using LazerSR.SunnyCalculator.Tuning;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;

namespace LazerSR.Hook.SunnySort;

/// <summary>
/// 저우선순위 단일 워커. 큐에 들어온 맵 해시마다 3개 rate(1.0/0.75/1.5)의 오리지널 sunny SR을
/// 계산해 <see cref="SunnySortCache"/>에 넣고, 파이프로 <c>sunnyup:</c>를 브로드캐스트해 런처가
/// 서버에 올리게 한다. 트리거 ②(선곡화면 이동)와 ①(일괄계산 버튼)이 이 큐로 모인다.
/// </summary>
public static class SunnySortWorker
{
    private static readonly ConcurrentQueue<string> queue = new();
    private static readonly HashSet<string> known = new();
    private static readonly object known_lock = new();
    private static readonly AutoResetEvent signal = new(false);

    private static Thread? thread;
    private static int threadStarted;

    // 위젯 진행률 표시용.
    public static volatile bool Running;
    public static int ScopeTotal;
    public static int ScopeDone;

    public static void Enqueue(string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return;
        if (SunnySortCache.HasAllRates(hash))
            return;

        lock (known_lock)
        {
            if (!known.Add(hash))
                return;
        }

        queue.Enqueue(hash);
        ensureThread();
        signal.Set();
    }

    /// <summary>realm의 모든 mania 맵(키 수 무관) 중 캐시가 덜 된 것을 큐에 넣는다. 일괄계산 버튼용.</summary>
    public static void EnqueueMissingFromRealm()
    {
        var realm = HookRuntimeContext.Realm;
        if (realm == null)
        {
            return;
        }

        List<string> hashes;
        try
        {
            // 모든 mania 맵(키 수 무관). Realm LINQ는 링크 프로퍼티(b.Ruleset.OnlineID)를 Where에서
            // 못 받으므로, realm 쪽 필터는 직접 persisted 필드(!b.Hidden)만 걸고 룰셋은 메모리에서 거른다.
            hashes = realm.Run(r => r.All<BeatmapInfo>()
                                     .Where(b => !b.Hidden)
                                     .AsEnumerable()
                                     .Where(b => b.Ruleset.OnlineID == 3)
                                     .Select(b => b.Hash)
                                     .Where(h => !string.IsNullOrEmpty(h))
                                     .Distinct()
                                     .ToList());
        }
        catch (Exception)
        {
            return;
        }

        // 스코프는 "캐시가 덜 된 맵 수" - queue.Count로 재면 워커가 동시에 큐를 비우며 과소집계된다.
        var missing = hashes.Where(h => !SunnySortCache.HasAllRates(h)).ToList();

        ScopeTotal = missing.Count;
        ScopeDone = 0;

        foreach (string h in missing)
            Enqueue(h);

    }

    private static void ensureThread()
    {
        if (Interlocked.CompareExchange(ref threadStarted, 1, 0) != 0)
            return;

        thread = new Thread(loop)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "LazerSR-SunnySortWorker",
        };
        thread.Start();
    }

    private static void loop()
    {
        while (true)
        {
            if (!queue.TryDequeue(out string? hash))
            {
                Running = false;
                signal.WaitOne();
                continue;
            }

            Running = true;

            try
            {
                computeOne(hash);
            }
            catch (Exception)
            {
            }
            finally
            {
                lock (known_lock)
                    known.Remove(hash);

                ScopeDone++;
            }
        }
    }

    private static void computeOne(string hash)
    {
        var bm = HookRuntimeContext.BeatmapManager;
        var rulesets = HookRuntimeContext.Rulesets;
        var mania = rulesets?.GetRuleset("mania");

        if (bm == null || mania == null)
        {
            return;
        }

        var info = bm.QueryBeatmap(b => b.Hash == hash);
        if (info == null)
        {
            return;
        }

        var working = bm.GetWorkingBeatmap(info);

        foreach (var mode in new[] { SunnySortMode.NoMod, SunnySortMode.HalfTime, SunnySortMode.DoubleTime })
        {
            double rate = SunnySortState.RateFor(mode);

            if (SunnySortCache.TryGet(hash, rate, out _))
                continue;

            IReadOnlyList<Mod> mods = rate == 1.0
                ? Array.Empty<Mod>()
                : PersonalSunnyModWhitelist.Reconstruct(rate, null);

            // 캐시/서버에 올라가는 값은 만인 sunny+ 체크박스 상태와 무관하게 항상 순정값이어야 한다 -
            // 격리(zero delta + forceVanillaTail)로 라이브 게임 화면의 sunny 계산과 레이스 없이 강제한다.
            double sr = SunnyConstants.WithIsolatedDiff(
                new double[SunnyConstants.Count],
                forceVanillaTail: true,
                () => SunnyRunner.Calculate(working, mania, mods));

            SunnySortCache.Put(hash, rate, sr, save: false);
            _ = PipeServer.BroadcastAsync(
                $"sunnyup:{hash}:{rate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}:" +
                $"{sr.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}:{SunnySortState.CalcVersion}");

        }

        SunnySortCache.Flush();
    }
}
