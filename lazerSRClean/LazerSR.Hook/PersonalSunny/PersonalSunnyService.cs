using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.SunnyCalculator;
using LazerSR.SunnyCalculator.Tuning;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Scoring;

namespace LazerSR.Hook.PersonalSunny;

/// <summary>
/// Orchestrates the personal-diff pipeline: queue -> bake missing Jacobians -> ridge-solve -> publish
/// to <see cref="PersonalDiff"/> and persist. Both the automatic path (a real score just imported,
/// <see cref="RecordScore"/>) and the manual one (the widget's collect button,
/// <see cref="CollectFromRealmAsync"/>) funnel into the same <see cref="runPipeline"/>.
/// <para>
/// Everything here runs on a background thread the caller provides (<c>Task.Run</c>) - nothing here
/// touches a Drawable. The widget polls the plain state properties below from its own <c>Update()</c>,
/// the same pattern <c>ManiaSimulationProgressDisplay</c> uses, rather than marshalling events back to
/// the update thread.
/// </para>
/// </summary>
public static class PersonalSunnyService
{
    // ---- state the widget polls ----

    public static bool IsBaking { get; private set; }
    public static int BakeTotal { get; private set; }
    public static int BakeDone { get; private set; }
    public static double Alpha { get; private set; }
    public static double Beta { get; private set; }
    public static int FitRecordCount { get; private set; }

    /// <summary>How many of <see cref="FitRecordCount"/> came from Pool A ("all-time") vs Pool B ("recent") - widget breakdown display.</summary>
    public static int TopPoolRecordCount { get; private set; }

    public static int RecentPoolRecordCount { get; private set; }

    /// <summary>Bumped whenever any of the state above changes - the widget compares this each frame instead of subscribing to an event.</summary>
    public static int Version { get; private set; }

    /// <summary>
    /// Set by <c>Patches/PersonalSunnyGameplayActivityPatch.cs</c> - true while a <c>Player</c> screen is
    /// current. Read by <see cref="currentParallelism"/> so the background warmup worker (and, harmlessly,
    /// the reactive collect path - it can only ever run from song select anyway) backs off during real
    /// gameplay instead of competing with it for CPU.
    /// </summary>
    public static volatile bool GameplayActive;

    private static readonly object pipeline_lock = new();
    private static readonly object progress_lock = new();

    private static BeatmapManager? beatmapManager;
    private static RulesetInfo? maniaRuleset;

    /// <summary>Generous headroom for the broad-phase free filter per bucket - see the 2026-08-20 design discussion for why this is a fixed constant rather than derived.</summary>
    private const int free_filter_margin = 2000;

    /// <summary>Full concurrency normally; drops to 1 (no parallelism) while <see cref="GameplayActive"/> - a soft throttle, not a hard pause.</summary>
    private static int currentParallelism() => GameplayActive ? 1 : Environment.ProcessorCount;

    private static int backgroundWarmupStarted;

    /// <summary>
    /// Starts the one-shot proactive background pre-computation - the exact same pipeline
    /// <see cref="CollectFromRealmAsync"/> runs for the collect button, just triggered automatically
    /// (from <c>PersonalSunnyWidget</c>'s first load) instead of by a click, so both pools are already
    /// warm by the time the player would otherwise wait on them. Idempotent - a second call (e.g. the
    /// widget loading again on another screen) is a no-op. Not tied to the calling widget's lifecycle;
    /// keeps running across screen transitions since it's just a background <see cref="Task"/>.
    /// </summary>
    public static void StartBackgroundWarmup(RealmAccess realm, IAPIProvider api)
    {
        if (Interlocked.CompareExchange(ref backgroundWarmupStarted, 1, 0) != 0)
            return;

        Task.Run(() => CollectFromRealmAsync(realm, api));
    }

    static PersonalSunnyService()
    {
        var fit = PersonalSunnyFitStore.Current;

        if (fit != null)
        {
            PersonalDiff.Update(PersonalFitSolver.ToRealDeltas(fit.UnitStep));
            Alpha = fit.Alpha;
            Beta = fit.Beta;
            FitRecordCount = fit.RecordCount;
        }
    }

    /// <summary>Call from anywhere dependencies are resolvable (widget BDL, or a patch's reflection lookup) - first caller wins, later callers are no-ops.</summary>
    public static void EnsureDependencies(BeatmapManager manager, RulesetStore rulesets)
    {
        beatmapManager ??= manager;
        maniaRuleset ??= rulesets.GetRuleset("mania") as RulesetInfo;
    }

    /// <summary>
    /// Filters and queues one just-completed score, then runs the pipeline for it. Call on a background
    /// thread. Silently does nothing if <paramref name="scoreInfo"/> doesn't qualify (wrong ruleset, not
    /// 4K, didn't pass, not the local player, or a mod outside <see cref="PersonalSunnyModWhitelist"/>).
    /// </summary>
    public static void RecordScore(ScoreInfo scoreInfo, int? localUserOnlineId)
    {
        try
        {
            if (!qualifies(scoreInfo, localUserOnlineId))
                return;

            var (rate, chartMod) = PersonalSunnyModWhitelist.Describe(scoreInfo.Mods);
            var key = new PersonalSunnyJacKey(scoreInfo.BeatmapInfo!.MD5Hash, rate, chartMod);

            offerToTopPool(key, scoreInfo.Accuracy, scoreInfo.Date);

            if (PersonalSunnyChartSrStore.TryGet(key, out double sr) && passesRecentPoolFloor(sr, scoreInfo.Accuracy))
            {
                var entry = new PersonalSunnyQueueEntry(key.BeatmapMd5, key.Rate, key.ChartMod, scoreInfo.Accuracy, scoreInfo.Date);
                PersonalSunnyQueueStore.Add(entry);
            }

            runPipeline();
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyService.RecordScore failed: {e}");
        }
    }

    /// <summary>
    /// Resolves one chart's universal SR (cache-first via <see cref="resolveUniversalSr"/>) and offers it
    /// to <see cref="PersonalSunnyTopPoolStore"/> - the automatic-path equivalent of one step of
    /// <see cref="runBroadPhase"/>, minus the free filter and parallelism (neither is needed for a single
    /// new chart). Also what populates <see cref="PersonalSunnyChartSrStore"/> for <see cref="RecordScore"/>'s
    /// own <see cref="passesRecentPoolFloor"/> check right after it.
    /// </summary>
    private static void offerToTopPool(PersonalSunnyJacKey key, double accuracy, DateTimeOffset endedAt)
    {
        if (beatmapManager == null || maniaRuleset == null)
            return; // Dependencies never resolved - the top pool just won't see this one until a full re-collect.

        double sr = resolveUniversalSr(key);
        if (double.IsNaN(sr))
            return;

        PersonalSunnyTopPoolStore.Offer(new PersonalSunnyTopPoolEntry(key.BeatmapMd5, key.Rate, key.ChartMod, sr, accuracy, endedAt));
    }

    /// <summary>
    /// Pool B's floor - flat, not Performance-relative. A relative floor (fraction of Pool A's ceiling)
    /// forces low-SR charts to carry near-saturated accuracy just to clear it, which packs the low end of
    /// the fit with (low SR, near-max Y) points and over-steepens the ridge slope. 0.85 matches the
    /// outlier cutoff from the 2026-08-21 analysis (real outlier at 0.1347, next legitimate value 0.4238,
    /// normal tail from ~0.60) - see the 2026-08-21 design discussion for the full reasoning.
    /// </summary>
    private const double recent_pool_accuracy_floor = 0.85;

    /// <summary>
    /// Floor for Pool B: a chart only qualifies if its accuracy clears <see cref="recent_pool_accuracy_floor"/>,
    /// regardless of the chart's own SR - "recent, and an honest attempt" rather than "recent, whatever it
    /// was". <paramref name="sr"/> is intentionally unused; kept in the signature to match the call sites,
    /// which already have it in hand from resolving the chart's SR beforehand.
    /// </summary>
    private static bool passesRecentPoolFloor(double sr, double accuracy) => accuracy >= recent_pool_accuracy_floor;

    /// <summary>
    /// Pool B contributes only its best <see cref="RecentPoolEffectiveCount"/> (by Performance) out of
    /// <see cref="PersonalSunnyQueueStore.MaxEntries"/> most-recent plays to the fit - mirrors Arcaea's
    /// potential system, where Recent10 is the 10 highest Play Ratings out of the 30 most recent plays
    /// (same ~1:2 ratio here as Arcaea's ~1:3), each chart counted once by its best occurrence in that
    /// window. <see cref="PersonalSunnyQueueStore"/> itself is untouched - still the full 100-entry FIFO
    /// window; this reduction only happens where <see cref="combinedEntries"/> builds the fit input.
    /// Public (unlike the other pool constants living as private consts here) so the widget can show it
    /// as Pool B's denominator alongside <see cref="PersonalSunnyTopPoolStore.Capacity"/> for Pool A.
    /// </summary>
    public const int RecentPoolEffectiveCount = 50;

    private static bool qualifies(ScoreInfo scoreInfo, int? localUserOnlineId)
    {
        if (!scoreInfo.Passed) return false;
        if (scoreInfo.Ruleset.OnlineID != 3) return false;
        if (scoreInfo.BeatmapInfo == null) return false;
        // ManiaBeatmapConverter rounds CircleSize to get the column count - match that, not exact equality.
        if (Math.Round(scoreInfo.BeatmapInfo.Difficulty.CircleSize) != 4) return false;
        if (localUserOnlineId is > 0 && scoreInfo.RealmUser.OnlineID != localUserOnlineId) return false;
        if (!PersonalSunnyModWhitelist.IsAllowed(scoreInfo.Mods)) return false;

        return true;
    }

    /// <summary>
    /// <see cref="PersonalSunnyTopPoolStore.Clear"/> then <see cref="CollectFromRealmAsync"/> - the manual
    /// "리플레이 수집" button's entry point (2026-08-22). Pool B already gets a full replace on every
    /// collect (<see cref="ReplaceQueueAndRun"/>), but Pool A's <see cref="PersonalSunnyTopPoolStore.Offer"/>
    /// only ever improves in place, so without this, a manual re-collect after a pool-logic change (a
    /// capacity change, say) would converge toward the new logic's result over several collects rather
    /// than reflecting it immediately. Not used by the background warmup or real-time score path - those
    /// stay incremental on purpose.
    /// </summary>
    public static void ResetAndCollectFromRealmAsync(RealmAccess realm, IAPIProvider api)
    {
        PersonalSunnyTopPoolStore.Clear();
        CollectFromRealmAsync(realm, api);
    }

    /// <summary>
    /// Bulk backfill from local realm history. Populates both pools: the top-<see cref="PersonalSunnyTopPoolStore.Capacity"/>
    /// by-universal-SR "skill ceiling" pool (<see cref="PersonalSunnyTopPoolStore"/>, via <see cref="runBroadPhase"/>)
    /// from every qualifying chart, and the recent-<see cref="PersonalSunnyQueueStore.MaxEntries"/> FIFO
    /// (<see cref="PersonalSunnyQueueStore"/>) from the player's most recent qualifying passes. Call on a
    /// background thread (this itself is synchronous).
    /// </summary>
    public static void CollectFromRealmAsync(RealmAccess realm, IAPIProvider api)
    {
        try
        {
            int localUserId = api.LocalUser.Value.Id;

            // ScoreInfo.Passed is [Ignored] (not a persisted realm column) - it can't appear in a
            // realm-side LINQ predicate. qualifies() below re-checks it per candidate.
            var qualifying = realm.Run(r =>
            {
                var candidates = r.All<ScoreInfo>()
                                  .Where(s => !s.DeletePending)
                                  .OrderByDescending(s => s.Date)
                                  .ToList();

                var result = new List<PersonalSunnyQueueEntry>();

                foreach (var score in candidates)
                {
                    if (!qualifies(score, localUserId))
                        continue;

                    var (rate, chartMod) = PersonalSunnyModWhitelist.Describe(score.Mods);
                    result.Add(new PersonalSunnyQueueEntry(score.BeatmapInfo!.MD5Hash, rate, chartMod, score.Accuracy, score.Date));
                }

                return result; // still date-descending, since candidates was.
            });

            // Top pool first (no pipeline run yet) - broad-phase over every qualifying chart. Also
            // populates PersonalSunnyChartSrStore, which the recent-pool floor below reads from.
            runBroadPhase(qualifying);

            // Recent pool: walk qualifying (already date-descending) picking the first MaxEntries that
            // also clear passesRecentPoolFloor - "recent AND still representative", not just "recent".
            var recent = new List<PersonalSunnyQueueEntry>();

            foreach (var entry in qualifying)
            {
                if (recent.Count >= PersonalSunnyQueueStore.MaxEntries)
                    break;

                var key = PersonalSunnyJacKey.From(entry);

                if (!PersonalSunnyChartSrStore.TryGet(key, out double sr))
                    continue; // Broad-phase never ranked this chart (shouldn't normally happen) - skip rather than guess.

                if (!passesRecentPoolFloor(sr, entry.Accuracy))
                    continue;

                recent.Add(entry);
            }

            recent.Reverse(); // oldest first, to match the FIFO order Add() would have produced.

            // The single pipeline run (bake+refit) that reflects both pools.
            ReplaceQueueAndRun(recent);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyService.CollectFromRealmAsync failed: {e}");
        }
    }

    /// <summary>
    /// Free-filter -> parallel cheap-SR broad-phase -> <see cref="PersonalSunnyTopPoolStore"/>. Ranks by
    /// <see cref="BeatmapInfo.StarRating"/> (osu!'s own already-computed, NoMod value - free to read) as a
    /// proxy, split into three buckets - NM / DT&#183;NC@1.5x / HT&#183;DC@0.75x, since rate is now exactly one
    /// of those three (<see cref="PersonalSunnyModWhitelist"/> only allows osu!-ranked rates) - so a
    /// rate-modded chart's true (higher or lower) difficulty never has to compete against an NM chart's
    /// raw stored value in one shared ranking. Each bucket is capped at <see cref="free_filter_margin"/>
    /// before the real (but still cheap - one sunny call) universal SR gets computed, in parallel, only
    /// for survivors - see the 2026-08-20 design discussion for the full reasoning.
    /// </summary>
    private static void runBroadPhase(IReadOnlyList<PersonalSunnyQueueEntry> qualifying)
    {
        if (beatmapManager == null || maniaRuleset == null)
            return; // Dependencies never resolved (widget never loaded) - nothing to rank with.

        // Chart-level dedup: the best (highest-accuracy) occurrence represents a chart here, not the most
        // recent one (2026-08-22 fix) - this feeds Offer(), and Pool A is the "skill ceiling" pool, so a
        // worse-but-more-recent replay should never crowd out a personal best from ever being considered.
        // Same SR for every occurrence of a given key (fixed by map+rate+mod), so comparing by raw accuracy
        // is equivalent to comparing by Performance here, without needing SR resolved yet.
        var perChart = new Dictionary<PersonalSunnyJacKey, PersonalSunnyQueueEntry>();

        foreach (var entry in qualifying)
        {
            var key = PersonalSunnyJacKey.From(entry);
            if (!perChart.TryGetValue(key, out var existing) || entry.Accuracy > existing.Accuracy)
                perChart[key] = entry;
        }

        var nm = new List<(PersonalSunnyJacKey Key, PersonalSunnyQueueEntry Entry, double Proxy)>();
        var dt = new List<(PersonalSunnyJacKey Key, PersonalSunnyQueueEntry Entry, double Proxy)>();
        var ht = new List<(PersonalSunnyJacKey Key, PersonalSunnyQueueEntry Entry, double Proxy)>();

        foreach (var (key, entry) in perChart)
        {
            var local = beatmapManager.QueryBeatmap(b => b.MD5Hash == key.BeatmapMd5);
            if (local == null)
                continue; // Not downloaded locally any more - same handling as bakeOne.

            var bucket = key.Rate > 1.0 ? dt : key.Rate < 1.0 ? ht : nm;
            bucket.Add((key, entry, local.StarRating));
        }

        var survivors = new List<(PersonalSunnyJacKey Key, PersonalSunnyQueueEntry Entry)>();

        foreach (var bucket in new[] { nm, dt, ht })
        {
            // StarRating is -1 when osu! hasn't computed it for this beatmap yet - can't rank that, so
            // it passes the free filter unconditionally rather than being silently dropped.
            survivors.AddRange(bucket.Where(c => c.Proxy < 0).Select(c => (c.Key, c.Entry)));
            survivors.AddRange(bucket.Where(c => c.Proxy >= 0)
                                      .OrderByDescending(c => c.Proxy)
                                      .Take(free_filter_margin)
                                      .Select(c => (c.Key, c.Entry)));
        }

        // BeatmapManager.QueryBeatmap/GetWorkingBeatmap both go through Realm.Run/WorkingBeatmapCache's own
        // lock internally, so they're safe to call concurrently from here (verified against osu! source).
        //
        // save: false on both cache writes below - saving on every one of potentially thousands of
        // survivors would be an O(n) full-cache rewrite done n times, serialised through each store's own
        // lock across every parallel worker. Flush() once after the loop instead (2026-08-22 fix).
        Parallel.ForEach(survivors, new ParallelOptions { MaxDegreeOfParallelism = currentParallelism() }, candidate =>
        {
            try
            {
                double sr = resolveUniversalSr(candidate.Key, save: false);
                if (double.IsNaN(sr))
                    return;

                PersonalSunnyTopPoolStore.Offer(new PersonalSunnyTopPoolEntry(
                    candidate.Key.BeatmapMd5, candidate.Key.Rate, candidate.Key.ChartMod, sr,
                    candidate.Entry.Accuracy, candidate.Entry.EndedAt), save: false);
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] PersonalSunnyService broad-phase failed for {candidate.Key}: {e}");
            }
        });

        PersonalSunnyChartSrStore.Flush();
        PersonalSunnyTopPoolStore.Flush();
    }

    /// <summary>Universal-point sunny SR for one chart, via <see cref="PersonalSunnyChartSrStore"/> so it's computed at most once, ever.</summary>
    /// <param name="save">Forwarded to <see cref="PersonalSunnyChartSrStore.Put"/> - see its doc for why a bulk caller passes <see langword="false"/>.</param>
    private static double resolveUniversalSr(PersonalSunnyJacKey key, bool save = true)
    {
        if (PersonalSunnyChartSrStore.TryGet(key, out double cached))
            return cached;

        var local = beatmapManager!.QueryBeatmap(b => b.MD5Hash == key.BeatmapMd5);
        if (local == null)
            return double.NaN;

        var working = beatmapManager.GetWorkingBeatmap(local);
        var mods = PersonalSunnyModWhitelist.Reconstruct(key.Rate, key.ChartMod);
        var playable = working.GetPlayableBeatmap(maniaRuleset!, mods, CancellationToken.None);

        double sr = PersonalJacobianBaker.CalculateUniversalSr(playable, mods);
        PersonalSunnyChartSrStore.Put(key, sr, save);
        return sr;
    }

    /// <summary>
    /// Replaces the queue with <paramref name="entries"/> and runs bake+refit. Shared tail for every
    /// queue-population path - <see cref="CollectFromRealmAsync"/> is the only caller today, but this
    /// stays a separate entry point so an alternate source (e.g. a dev fixture) can reuse the same
    /// bake/refit pipeline without duplicating it. Call on a background thread.
    /// </summary>
    public static void ReplaceQueueAndRun(IReadOnlyList<PersonalSunnyQueueEntry> entries)
    {
        PersonalSunnyQueueStore.ReplaceAll(entries);
        runPipeline();
    }

    private static void runPipeline()
    {
        lock (pipeline_lock)
        {
            try
            {
                bakeMissing();
                refit();
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] PersonalSunnyService pipeline failed: {e}");
            }
            finally
            {
                IsBaking = false;
                Version++;
            }
        }
    }

    /// <summary>
    /// Pool A (top-Performance ceiling) union Pool B's <see cref="RecentPoolEffectiveCount"/>-best
    /// reduction, concatenated - NOT deduped against each other. A chart that lands in both pools
    /// deliberately counts twice in the fit (same precedent as Arcaea's b30+r10: overlap between "best"
    /// and "recent" isn't collapsed there either). The pools' own stores are untouched here - eviction/FIFO
    /// semantics belong to them alone (<see cref="PersonalSunnyTopPoolStore"/>/<see cref="PersonalSunnyQueueStore"/>).
    /// <c>IsRecentPool</c> tags which side each entry came from, for the widget's count breakdown.
    /// </summary>
    private static List<(PersonalSunnyJacKey Key, double Accuracy, DateTimeOffset EndedAt, bool IsRecentPool)> combinedEntries()
    {
        var combined = new List<(PersonalSunnyJacKey Key, double Accuracy, DateTimeOffset EndedAt, bool IsRecentPool)>();

        foreach (var entry in PersonalSunnyTopPoolStore.Entries)
            combined.Add((entry.Key, entry.Accuracy, entry.EndedAt, false));

        // Recent pool -> best RecentPoolEffectiveCount by Performance, one occurrence per chart (the
        // best one) within the MaxEntries-most-recent window - mirrors Arcaea's Recent10 (see
        // RecentPoolEffectiveCount's doc). PersonalSunnyQueueStore.Entries is already that raw window.
        var recentBest = PersonalSunnyQueueStore.Entries
                                                  .GroupBy(PersonalSunnyJacKey.From)
                                                  .Select(g => g.OrderByDescending(e => e.Accuracy).First())
                                                  .Select(e =>
                                                  {
                                                      var key = PersonalSunnyJacKey.From(e);
                                                      PersonalSunnyChartSrStore.TryGet(key, out double sr);
                                                      double performance = PersonalSunnyTopPoolEntry.ComputePerformance(sr, e.Accuracy);
                                                      return (Key: key, Entry: e, Performance: performance);
                                                  })
                                                  .OrderByDescending(x => x.Performance)
                                                  .Take(RecentPoolEffectiveCount);

        foreach (var (key, entry, _) in recentBest)
            combined.Add((key, entry.Accuracy, entry.EndedAt, true));

        return combined;
    }

    private static void bakeMissing()
    {
        var keys = combinedEntries().Select(c => c.Key).Distinct().ToList();

        var missing = keys.Where(k => !PersonalSunnyJacStore.TryGet(k, out _)).ToList();

        BakeTotal = missing.Count;
        BakeDone = 0;
        IsBaking = missing.Count > 0;
        Version++;

        if (beatmapManager == null || maniaRuleset == null)
        {
            // Dependencies never resolved (widget never loaded) - nothing to bake with. The fit just
            // runs on whatever was already cached.
            IsBaking = false;
            return;
        }

        // Independent per-chart work (own beatmap conversion, own PersonalJacobianBaker.Bake call) -
        // safe to run in parallel the same way runBroadPhase does. BakeDone/Version updates are the only
        // shared mutable state touched directly here, so those alone need the lock.
        //
        // save: false in bakeOne, Flush() once here after the loop - same O(n^2)-rewrite concern as
        // runBroadPhase's cache writes (2026-08-22 fix).
        Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = currentParallelism() }, key =>
        {
            bakeOne(key);

            lock (progress_lock)
            {
                BakeDone++;
                Version++;
            }
        });

        PersonalSunnyJacStore.Flush();
    }

    private static void bakeOne(PersonalSunnyJacKey key)
    {
        try
        {
            var local = beatmapManager!.QueryBeatmap(b => b.MD5Hash == key.BeatmapMd5);
            if (local == null)
                return; // Not downloaded locally any more - skip, the fit just won't use this item.

            var working = beatmapManager.GetWorkingBeatmap(local);
            var mods = PersonalSunnyModWhitelist.Reconstruct(key.Rate, key.ChartMod);
            var playable = working.GetPlayableBeatmap(maniaRuleset!, mods, CancellationToken.None);

            var result = PersonalJacobianBaker.Bake(playable, mods);

            PersonalSunnyJacStore.Put(new PersonalSunnyJacEntry(key.BeatmapMd5, key.Rate, key.ChartMod, result.Sr0, result.Jacobian), save: false);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyService bake failed for {key}: {e}");
        }
    }

    private static void refit()
    {
        var combined = combinedEntries();

        // PersonalSunnyJacStore only ever needs to hold what's actually in the fit, so it's pruned to
        // this set. PersonalSunnyChartSrStore is deliberately NOT pruned here - it's the broad-phase
        // ranking cache and legitimately holds far more charts than ever make the top/recent pools.
        PersonalSunnyJacStore.PruneTo(combined.Select(c => c.Key).Distinct());

        var y = new List<double>();
        var sr0 = new List<double>();
        var jac = new List<double[]>();
        int topCount = 0, recentCount = 0;

        foreach (var (key, accuracy, _, isRecentPool) in combined)
        {
            if (!PersonalSunnyJacStore.TryGet(key, out var baked))
                continue;

            double clampedAccuracy = Math.Min(accuracy, 1.0 - 1e-6);
            y.Add(-Math.Log(1.0 - clampedAccuracy));
            sr0.Add(baked.Sr0);
            jac.Add(baked.Jacobian);

            if (isRecentPool)
                recentCount++;
            else
                topCount++;
        }

        var fit = PersonalFitSolver.Solve(y.ToArray(), sr0.ToArray(), jac.ToArray());

        PersonalDiff.Update(PersonalFitSolver.ToRealDeltas(fit.UnitStep));

        Alpha = fit.Alpha;
        Beta = fit.Beta;
        FitRecordCount = y.Count;
        TopPoolRecordCount = topCount;
        RecentPoolRecordCount = recentCount;

        PersonalSunnyFitStore.Save(new PersonalSunnyFitStore.FitResult(fit.UnitStep, fit.Alpha, fit.Beta, y.Count, DateTimeOffset.Now));
    }
}
