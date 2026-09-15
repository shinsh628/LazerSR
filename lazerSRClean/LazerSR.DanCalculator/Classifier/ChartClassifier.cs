// Port of mania-hub live-backend/src/dan/chart-classifier.ts
//
// The single chart classifier. Routes each chart to the best-performing engine
// per the benchmark in live-backend/vendor/leoblack/PORT_NOTES.md:
//   4K RC        -> LeoBlack Mixed (Roxy/Azusa/Daniel/Sunny blend)
//   4K LN        -> LeoBlack's LN table (in-house LN kNN below its LN 5 floor)
//   6K / 7K      -> LeoBlack Sunny star rating mapped through the 6K/7K dan tables
//   other keys   -> patterns only, no dan verdict
// Callers should treat this as THE classifier; estimateDan/estimateDanielDan/
// estimateLeoBlackDan remain only as internals and benchmark baselines.

using System.Globalization;
using System.Text.RegularExpressions;
using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Estimators;
using LazerSR.DanCalculator.Features;
using LazerSR.DanCalculator.Interlude;
using LazerSR.DanCalculator.Intervals;
using LazerSR.DanCalculator.Patterns;
using LazerSR.DanCalculator.Types;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.Classifier;

public static class ChartClassifier
{
    // Upstream marathon correction runs inside Azusa and Roxy, before Mixed's
    // routing and Companella fusion. Duration is the original note-start span,
    // including breaks and without rate scaling; MSD belongs to the played rate.
    // Missing MSD leaves the verdict uncorrected. Final Mixed output can increase
    // when the correction changes its route or releases the Sunny low-end guard.
    public const double MARATHON_CORRECTION_MIN_DURATION_S = 300;

    private static readonly Regex InvalidRe = new(@"^Invalid\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnknownRe = new(@"^Unknown\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TABLE_TIER_PATTERN = new(@"^(.+?) (low|mid/low|mid/high|mid|high)$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string?> TIER_VARIANTS = new()
    {
        ["low"] = "--",
        ["mid/low"] = "-",
        ["mid"] = null,
        ["mid/high"] = "+",
        ["high"] = "++",
    };

    private static readonly Dictionary<string, double> TIER_OFFSETS = new()
    {
        ["low"] = -0.4,
        ["mid/low"] = -0.2,
        ["mid"] = 0,
        ["mid/high"] = 0.2,
        ["high"] = 0.4,
    };

    // JS Math.round: round half toward +Infinity.
    private static double JsRound(double v) => Math.Floor(v + 0.5);

    /// <summary>First-to-last note span in seconds, as upstream measures a marathon.</summary>
    public static double ChartNoteSpanSeconds(ManiaBeatmap map)
    {
        var notes = map.Notes;
        if (notes.Count < 2) return 0;
        double earliest = double.PositiveInfinity;
        double latest = double.NegativeInfinity;
        foreach (var note in notes)
        {
            if (note.Time < earliest) earliest = note.Time;
            if (note.Time > latest) latest = note.Time;
        }
        return latest > earliest ? (latest - earliest) / 1000 : 0;
    }

    /// <summary>
    /// Whether MSD values would change this chart's verdict through the marathon
    /// correction, so an async caller knows when computing them is worth a
    /// MinaCalc pass. The correction's other gates (skill balance, numeric taper)
    /// live in the vendored module and need the values themselves.
    /// </summary>
    public static bool IsMarathonCorrectionCandidate(ManiaBeatmap map)
        => map.KeyCount == 4 && ChartNoteSpanSeconds(map) > MARATHON_CORRECTION_MIN_DURATION_S;

    private static (double Lo, double Hi, string Name)[]? TableFor(string side, int keyCount)
    {
        var tables = DanIndex.For(keyCount);
        if (tables == null) return null;
        return side == "ln" ? tables.Ln?.Default : tables.Rc.Default;
    }

    private static ParsedDanPart? ParseTableHalf(string text, (double Lo, double Hi, string Name)[] table)
    {
        string? boundary = null;
        string body = text.Trim();
        if (body.StartsWith("< "))
        {
            boundary = "below";
            body = body[2..].Trim();
        }
        else if (body.StartsWith("> "))
        {
            boundary = "above";
            body = body[2..].Trim();
        }

        var match = TABLE_TIER_PATTERN.Match(body);
        if (!match.Success) return null;
        var levels = DanTables.TableLevels(table);
        int idx = levels.FindIndex(candidate => candidate.Base == match.Groups[1].Value);
        if (idx < 0) return null;
        var entry = levels[idx];

        string tier = match.Groups[2].Value;
        return new ParsedDanPart
        {
            Label = DanTables.TableLabelForBase(entry.Base),
            Variant = boundary == "below" ? "--" : boundary == "above" ? "++" : TIER_VARIANTS[tier],
            RawDan = boundary == "below" ? entry.Level - 0.5 : boundary == "above" ? entry.Level + 0.5 : entry.Level + TIER_OFFSETS[tier],
            Boundary = boundary,
        };
    }

    private static DanVerdictHalf ToHalf(
        ParsedDanPart parsed, string kind, string source, double estimatedSr, double confidence, string raw)
        => new()
        {
            Kind = kind,
            Source = source,
            Label = parsed.Label,
            Variant = parsed.Variant,
            DisplayName = $"{parsed.Label}{parsed.Variant ?? ""}",
            RawDan = JsRound(parsed.RawDan * 100) / 100,
            EstimatedSr = estimatedSr,
            Confidence = confidence,
            Boundary = parsed.Boundary,
            Raw = raw,
        };

    private static (string RcText, string? LnText) SplitVerdict(string verdict)
    {
        var parts = verdict.Split("||").Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
        return (parts.Count > 0 ? parts[0] : "", parts.Count >= 2 ? parts[^1] : null);
    }

    // Roxy wins the mixed routing for every 4K RC chart with enough taps, but its
    // calibration corpus bottoms out at the dan courses: the raw structural signal
    // clamps at -2.5 and the isotonic/meta layers can only extrapolate upward from
    // there, so a 0.9* ranked Easy with 80+ taps came back "Reform 4" (sub-1* maps
    // were landing in the 4-6 dan collections). Charts under Roxy's note gate fall
    // through to Sunny and read "< Intro 1" correctly.
    //
    // A pinned raw signal alone is NOT enough to distrust the verdict: Roxy's
    // structural curve also collapses on some genuinely hard charts (measured on
    // prod: an Alpha-level 6.5* file with raw -2.65), and rescuing those is exactly
    // why the meta model exists. The re-route therefore needs both signals to
    // agree: raw pinned at the clamp AND the independent Sunny baseline asserting
    // the chart sits below Reform 1 on its own scale. Trivial leakers measure
    // 0.22-0.95 Sunny-star vs 5.5+ for the collapsed-but-hard charts, so the two
    // populations are far apart.
    public const double ROXY_RAW_FLOOR_PIN = -2.45;
    // "Reform 1 low" starts at 3.037 Sunny-star in the 4K RC table
    // (vendor/leoblack/estimator/intervals/4k-rc.js): below 3.0 Sunny is
    // asserting sub-Reform-1 (Intro or off-scale) while the pinned meta claims
    // Reform 3+, a multi-dan disagreement only broken structural input produces.
    public const double SUNNY_LOW_END_MAX_STAR = 3.0;

    private static bool IsRoxyFloorPinned(LeoBlackReworkResult mixed)
    {
        if (mixed.NumericDifficultyHint != "roxy-meta-ridge-v3") return false;
        double raw = mixed.RawNumericDifficulty ?? 0.0;
        return double.IsFinite(raw) && raw <= ROXY_RAW_FLOOR_PIN;
    }

    // Since the 214aedd re-pin Roxy is high-difficulty-only (final numeric under
    // 11 routes to Azusa), so the trivial-chart population the floor-pin guard
    // was built for now reaches the verdict through Azusa instead - and repeats
    // the same overestimation (measured on the guard's own synthetic ranked-Easy
    // shape: 2 nps singles came back "Reform 3 low", numeric 2.6, while Azusa's
    // own Sunny reference read 3.17, i.e. 0.24 star). The candidate signature is
    // that internal disagreement: an Azusa verdict claiming Reform 2+ while the
    // Sunny reference it blended sits below Reform 1 on Sunny's own scale. The
    // reference rides the result's debug block (estimateSunnyNumeric output), so
    // screening costs no extra engine pass; the reroute's independent Sunny run
    // stays the final authority.
    public const double AZUSA_SUSPECT_MIN_NUMERIC = 2;
    // SUNNY_LOW_END_MAX_STAR mapped through estimateSunnyNumeric's linear scale
    // (2.85 + 1.33 * star at star 3.0). The scale saturates the low end (a 0-star
    // chart still reads 2.85), which is exactly why Azusa's blend cannot pull a
    // trivial chart's verdict down far enough on its own.
    public const double AZUSA_SUNNY_REFERENCE_MAX_NUMERIC = 6.84;

    private static bool IsAzusaLowEndSuspect(LeoBlackReworkResult mixed)
    {
        if (mixed.NumericDifficultyHint != "azusa-rc-v1") return false;
        double numeric = mixed.NumericDifficulty ?? double.NaN;
        if (!double.IsFinite(numeric) || numeric < AZUSA_SUSPECT_MIN_NUMERIC) return false;
        double sunnyReference = double.NaN;
        if (mixed.Debug.TryGetValue("sunnyNumeric", out var raw) && raw != null)
        {
            sunnyReference = raw switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
                _ => double.NaN,
            };
        }
        return double.IsFinite(sunnyReference) && sunnyReference < AZUSA_SUNNY_REFERENCE_MAX_NUMERIC;
    }

    /// <summary>
    /// The Sunny result to re-verdict <paramref name="mixed"/> with, or null when
    /// the guard should not apply. Exported for the one-shot floor-pin recompute
    /// sweep; keep both callers on this single predicate.
    /// </summary>
    public static LeoBlackReworkResult? SunnyLowEndReroute(LeoBlackReworkResult mixed, string osuText, double rate)
    {
        if (!IsRoxyFloorPinned(mixed) && !IsAzusaLowEndSuspect(mixed)) return null;
        var sunny = MixedEstimator.RunLeoBlackSunny(osuText, new EstimatorOptions { SpeedRate = rate });
        return sunny.Star < SUNNY_LOW_END_MAX_STAR ? sunny : null;
    }

    private static LeoBlackReworkResult Clone(LeoBlackReworkResult x) => new()
    {
        Star = x.Star,
        LnRatio = x.LnRatio,
        ColumnCount = x.ColumnCount,
        EstDiff = x.EstDiff,
        NumericDifficulty = x.NumericDifficulty,
        NumericDifficultyHint = x.NumericDifficultyHint,
        RawNumericDifficulty = x.RawNumericDifficulty,
        MixedCompanellaPlan = x.MixedCompanellaPlan,
        ActualEstimatorAlgorithm = x.ActualEstimatorAlgorithm,
        Graph = x.Graph,
        Debug = x.Debug,
    };

    public static ChartClassification ClassifyChart(ManiaBeatmap map, string osuText, ClassifyChartInput? input = null)
    {
        input ??= new ClassifyChartInput();
        double rate = Labels.GetInputRate(input);
        var warnings = new List<string>();
        var danEligibility = DanEligibility.InspectChartDanEligibility(map);
        bool sectionVibro = VibroSections.UsesSectionVibro(map);
        PrepareVibroChartResult? prepared = input.AdjustVibro ? VibroSections.PrepareVibroChart(osuText, rate, map) : null;
        VibroAnalysis? vibroAnalysis = sectionVibro
            ? (prepared?.Analysis ?? VibroSections.AnalyzeVibroSections(map, rate))
            : null;
        if (prepared?.Analysis.Status == VibroStatus.Adjusted)
        {
            osuText = prepared.Value.OsuText;
            var reparsed = ManiaBeatmapParser.Parse(osuText);
            reparsed.TotalLength = map.TotalLength;
            map = reparsed;
            warnings.Add($"Adjusted rating: {(prepared.Value.Analysis.ExcludedDurationMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)}s of vibro excluded; remaining patterns rated at their original timestamps.");
        }
        if (!danEligibility.Eligible)
        {
            warnings.Add(
                $"Player dan disabled: {danEligibility.MaxSameColumnHeadStack.ToString(CultureInfo.InvariantCulture)} objects share one column and head time.");
        }

        var features = DanFeatures.ExtractDanFeatures(map, input, rate);
        var patterns = DanPatterns.AnalyzeManiaPatterns(map, input, features);

        PatternAnalysisResult? clusters = null;
        try
        {
            clusters = PatternService.AnalyzePatternFromText(osuText);
        }
        catch (Exception error)
        {
            warnings.Add($"Pattern clustering failed: {error.Message}.");
        }

        bool longjackVibro = !sectionVibro && clusters != null
            && LongjackVibro.DetectVibroFromLongjackPattern(
                clusters.Report,
                PatternsConfig.LONGJACK_VIBRO_RATIO_THRESHOLD,
                PatternsConfig.LONGJACK_VIBRO_MIN_BPM / rate);
        bool lnVibro = VibroDetection.DetectLnVibro(map, rate);
        bool riceVibro = sectionVibro ? vibroAnalysis?.Status == VibroStatus.Excluded : VibroDetection.DetectRiceVibro(map, rate);
        bool vibro = longjackVibro || lnVibro || riceVibro;
        if (lnVibro)
        {
            warnings.Add("Staggered LN-spam (vibro) detected; LN difficulty is likely overestimated.");
        }

        LeoBlackReworkResult? mixed = null;
        bool companellaApplied = false;
        try
        {
            var rawMixed = MixedEstimator.RunLeoBlackMixed(osuText, new EstimatorOptions
            {
                SpeedRate = rate,
                MarathonCorrection = IsMarathonCorrectionCandidate(map) && input.MarathonMsdValues != null
                    ? new MarathonCorrectionInput
                    {
                        DurationS = ChartNoteSpanSeconds(map),
                        EttValues = input.MarathonMsdValues,
                    }
                    : null,
            });
            // The fusion clears Azusa's numeric hint. Check its original verdict too,
            // or a trivial chart can escape the low-end guard after the blend.
            bool validCompanella = input.Companella != null
                && input.Companella.NumericDifficulty is double cnd
                && double.IsFinite(cnd);
            companellaApplied = validCompanella && rawMixed.MixedCompanellaPlan != null;
            var candidate = validCompanella && input.Companella != null
                ? MixedEstimator.ApplyCompanellaToMixedResult(rawMixed, input.Companella)
                : rawMixed;
            // A Sunny failure inside the reroute check throws into the catch below,
            // leaving mixed null: better no verdict than the known-bad pinned one.
            var reroute = SunnyLowEndReroute(rawMixed, osuText, rate)
                ?? (!ReferenceEquals(candidate, rawMixed) ? SunnyLowEndReroute(candidate, osuText, rate) : null);
            if (reroute != null)
            {
                var routed = Clone(candidate);
                routed.Star = reroute.Star;
                routed.LnRatio = reroute.LnRatio;
                routed.EstDiff = reroute.EstDiff;
                routed.NumericDifficulty = null;
                routed.NumericDifficultyHint = null;
                routed.MixedCompanellaPlan = null;
                mixed = routed;
                companellaApplied = false;
                warnings.Add(IsRoxyFloorPinned(rawMixed)
                    ? "Roxy raw difficulty pinned at its scale floor; using the Sunny low-end verdict."
                    : "Azusa low-end verdict contradicts its own Sunny reference; using the Sunny low-end verdict.");
            }
            else
            {
                mixed = candidate;
            }
        }
        catch (Exception error)
        {
            warnings.Add($"LeoBlack estimator failed: {error.Message}.");
        }

        // Only a genuine (non-rerouted) Roxy verdict sets this hint — the Sunny
        // low-end reroute below clears it on its cloned result while keeping the
        // original Debug bag, so gating on the hint (not just Debug presence)
        // keeps a stale axis breakdown off a chart Roxy no longer actually rates.
        List<RoxyAxisContribution>? roxyAxes = null;
        if (mixed?.NumericDifficultyHint == "roxy-meta-ridge-v3"
            && mixed.Debug.TryGetValue("axisBreakdown", out var axisRaw)
            && axisRaw is Dictionary<string, object?> axisDict)
        {
            roxyAxes = new List<RoxyAxisContribution>();
            foreach (var (axisName, entryRaw) in axisDict)
            {
                if (entryRaw is not Dictionary<string, object?> entry) continue;
                double share = entry.TryGetValue("share", out var s) && s is double sd ? sd : 0;
                double localRawDan = entry.TryGetValue("localRawDan", out var l) && l is double ld ? ld : 0;
                roxyAxes.Add(new RoxyAxisContribution { Axis = axisName, Share = share, LocalRawDan = localRawDan });
            }
        }

        string? verdictText = mixed != null ? (mixed.EstDiff ?? "").Trim() : null;
        bool verdictUsable = verdictText != null && verdictText.Length > 0
            && !InvalidRe.IsMatch(verdictText) && !UnknownRe.IsMatch(verdictText);
        double lnRatio = mixed != null && double.IsFinite(mixed.LnRatio) ? mixed.LnRatio : features.Metrics.HoldRatio;
        double? sunnySr = mixed != null && double.IsFinite(mixed.Star) ? mixed.Star : (double?)null;

        double rcConfidence = vibro ? 0.35 : 0.72;
        if (vibro)
        {
            warnings.Add("Vibro detected; ordinary difficulty estimates are unreliable.");
        }

        DanVerdictHalf? rc = null;
        DanVerdictHalf? lnFromTables = null;

        if (verdictUsable && mixed != null)
        {
            var (rcText, lnText) = SplitVerdict(verdictText!);
            if (map.KeyCount == 4)
            {
                var parsedRc = LeoBlackEstimator.ParseLeoBlackRcHalf(rcText, mixed.NumericDifficulty);
                if (parsedRc != null)
                {
                    rc = ToHalf(parsedRc, "rc", companellaApplied ? DanVerdictSource.LeoBlackCompanella : DanVerdictSource.LeoBlackMixed, sunnySr ?? 0, parsedRc.Boundary != null ? 0.4 : rcConfidence, rcText);
                }
                if (lnText != null)
                {
                    var parsedLn = LeoBlackEstimator.ParseLeoBlackLnHalf(lnText);
                    if (parsedLn != null)
                    {
                        lnFromTables = ToHalf(parsedLn, "ln", DanVerdictSource.LeoBlackSunnyTable, sunnySr ?? 0, parsedLn.Boundary != null ? 0.4 : 0.6, lnText);
                    }
                }
                if (mixed.MixedCompanellaPlan != null)
                {
                    warnings.Add("RC estimate below 9 stars wants Companella, which was not supplied; showing the unrefined estimate.");
                }
            }
            else if (map.KeyCount == 6 || map.KeyCount == 7)
            {
                var tables = DanIndex.For(map.KeyCount);
                var parsedRc = tables != null ? ParseTableHalf(rcText, tables.Rc.Default) : null;
                if (parsedRc != null)
                {
                    rc = ToHalf(parsedRc, "rc", DanVerdictSource.LeoBlackSunnyTable, sunnySr ?? 0, parsedRc.Boundary != null ? 0.35 : 0.55, rcText);
                }
                if (lnText != null && tables?.Ln != null)
                {
                    var parsedLn = ParseTableHalf(lnText, tables.Ln.Default);
                    if (parsedLn != null)
                    {
                        lnFromTables = ToHalf(parsedLn, "ln", DanVerdictSource.LeoBlackSunnyTable, sunnySr ?? 0, parsedLn.Boundary != null ? 0.35 : 0.55, lnText);
                    }
                }
            }
        }

        // LeoBlack's LN table is the 4K LN verdict wherever it reads a real tier. Its
        // in-house predecessor drifted well above both LeoBlack and Dan-Overlay on
        // rated charts, because it falls through to an SR-linear regression whenever
        // no reference chart is within its distance gate - and that regression is fed
        // starRating * rate^0.7, so the drift widened with rate (chart 5327751 at
        // 1.5x: in-house LN 14+, LeoBlack LN 13, Dan-Overlay Yuugure/12).
        //
        // The table bottoms out at "< LN 5" (4.832 Sunny stars), where every easy LN
        // chart would otherwise collapse onto one reading, so the kNN still covers
        // that low end - the range its corpus does carry references for.
        DanVerdictHalf? ln = lnFromTables != null && lnFromTables.Boundary != "below" ? lnFromTables : null;
        if (ln == null && map.KeyCount == 4)
        {
            double baseStarRating = input.StarRating is double sr && double.IsFinite(sr) ? Math.Max(0, input.StarRating ?? 0) : 0;
            double starRating = baseStarRating > 0 ? baseStarRating * Math.Pow(rate, 0.7) : 0;
            var lnEstimate = LnDan.EstimateLnDan(map, input, features.Metrics, starRating, features.DurationMs, rate);
            if (lnEstimate != null)
            {
                ln = new DanVerdictHalf
                {
                    Kind = "ln",
                    Source = DanVerdictSource.InhouseLnKnn,
                    Label = lnEstimate.Label,
                    Variant = lnEstimate.Variant,
                    DisplayName = $"{lnEstimate.Label}{lnEstimate.Variant ?? ""}",
                    RawDan = lnEstimate.RawDan,
                    EstimatedSr = lnEstimate.EstimatedSr,
                    Confidence = lnEstimate.Confidence,
                    Boundary = null,
                    Raw = lnEstimate.DisplayName,
                };
            }
        }
        // Below the table floor with no kNN verdict either, the "< LN 5" boundary is
        // still the honest reading.
        if (ln == null) ln = lnFromTables;
        // Mirror the RC vibro damping: an LN dan computed off hold density means
        // little when the holds are vibro spam.
        if (ln != null && lnVibro)
        {
            var damped = new DanVerdictHalf
            {
                Kind = ln.Kind,
                Source = ln.Source,
                Label = ln.Label,
                Variant = ln.Variant,
                DisplayName = ln.DisplayName,
                RawDan = ln.RawDan,
                EstimatedSr = ln.EstimatedSr,
                Confidence = Math.Min(ln.Confidence, 0.35),
                Boundary = ln.Boundary,
                Raw = ln.Raw,
            };
            ln = damped;
        }

        string prefer = input.PreferFamily ?? "auto";
        DanVerdictHalf? primary = prefer == "ln"
            ? ln ?? rc
            : prefer == "rc"
                ? rc ?? ln
                : (lnRatio >= LnDan.LnPrimaryMinRatioFor(map.KeyCount) && ln != null ? ln : rc ?? ln);

        DanEstimate? estimate = primary != null
            ? new DanEstimate
            {
                Label = primary.Label,
                Variant = primary.Variant,
                DisplayName = primary.DisplayName,
                RawDan = primary.RawDan,
                EstimatedSr = primary.EstimatedSr,
                Family = primary.Kind == "ln" ? DanSkillFamily.Ln : DanSkillFamily.Dan,
                Confidence = primary.Confidence,
                Metrics = features.Metrics,
                SkillScores = BuildSkillScores(primary),
                Warnings = warnings,
            }
            : null;

        return new ChartClassification
        {
            KeyCount = map.KeyCount,
            Supported = primary != null,
            LnRatio = lnRatio,
            SunnySr = sunnySr,
            VerdictText = verdictText,
            Rc = rc,
            Ln = ln,
            Primary = primary,
            Estimate = estimate,
            Patterns = patterns,
            Clusters = clusters,
            Vibro = vibro,
            VibroAnalysis = vibroAnalysis,
            DanEligibility = danEligibility,
            CompanellaPending = mixed?.MixedCompanellaPlan != null,
            Warnings = warnings,
            RoxyAxes = roxyAxes,
        };
    }

    public static SkillScores BuildSkillScores(DanVerdictHalf primary)
    {
        var s = new SkillScores
        {
            [DanSkillFamily.Jack] = 0,
            [DanSkillFamily.Stream] = 0,
            [DanSkillFamily.Jumpstream] = 0,
            [DanSkillFamily.Handstream] = 0,
            [DanSkillFamily.Stamina] = 0,
            [DanSkillFamily.Chordjack] = 0,
            [DanSkillFamily.Tech] = 0,
            [DanSkillFamily.Ln] = primary.Kind == "ln" ? primary.EstimatedSr : 0,
            [DanSkillFamily.Dan] = primary.EstimatedSr,
        };
        return s;
    }
}
