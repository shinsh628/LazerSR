// Port of mania-hub live-backend/vendor/leoblack/estimator/mixedEstimator.js
//
// "Mixed" estimator: runs the Sunny baseline, then (4K only) routes the RC half
// through Roxy → Azusa → Daniel and, for the low band, stages an Azusa⊕Companella
// fusion plan that the async apply step resolves. Non-4K, and RC-tagged non-4K,
// return the Sunny baseline unchanged.
//
// 1:1 mechanical port. Calibration rationale comments (originally Chinese)
// translated verbatim alongside the original.
//
// Also hosts the thin mania-hub wrappers from
// live-backend/src/dan/leoblack-estimator.ts that the Mixed callers need:
// RunLeoBlackMixed / RunLeoBlackSunny (see bottom of file).

using System.Globalization;
using System.Text.RegularExpressions;
using LazerSR.DanCalculator.Parser;
using LazerSR.DanCalculator.Patterns;

namespace LazerSR.DanCalculator.Estimators;

public static class MixedEstimator
{
    private static readonly HashSet<double> MixedSupportedKeys = new() { 4, 6, 7 };

    // 低难段（Roxy scope-out 或 Sunny star < 9）RC 部分的 Azusa⊕Companella 融合权重。
    // 两个近似独立的低难估计器取平均以降低方差（与 Roxy 内部 Azusa 融合同一机制族）；
    // 离线探针显示 w∈[0.4,0.7] 结果平坦，取对称 0.5，避免拟合基准分布。
    // (Fusion weight for the Azusa⊕Companella blend of the RC part in the low band
    // (Roxy scope-out or Sunny star < 9). Two near-independent low-difficulty
    // estimators are averaged to cut variance (same mechanism family as Roxy's
    // internal Azusa fusion); offline probes show w∈[0.4,0.7] is flat, so the
    // symmetric 0.5 is taken to avoid fitting the benchmark distribution.)
    private const double RcAzusaCompanellaFusionWeight = 0.5;

    // Companella 仅覆盖低难段：Sunny 星数达到该值以上不再参与 RC 融合。
    // (Companella only covers the low band: at/above this Sunny star it no longer
    // participates in RC fusion.)
    private const double LowBandCompanellaStarMax = 9;

    // 融合作用域：仅当 Azusa 的 RC 数值自身低于 Alpha 边界（低难主张成立）时融合。
    // 一致性门控（|Azusa−Companella| ≤ 1.0）经真实运行验证会误杀大量有益融合
    // （净收益 -4.89 → -2.87 MAE 点），已移除：分歧大小无法区分方向对错，
    // 净效应由权重平坦性保证。
    // (Fusion scope: fuse only when Azusa's RC value is itself below the Alpha
    // boundary (the low-difficulty claim holds). The agreement gate
    // (|Azusa−Companella| ≤ 1.0) was verified on real runs to kill a lot of
    // beneficial fusions (net -4.89 → -2.87 MAE points) and has been removed:
    // disagreement magnitude can't tell right direction from wrong; the net
    // effect is guaranteed by weight flatness.)
    private const double RcFusionLowBandMax = 11;

    private const double AzusaPrefBalancedHandScreenMaxBias = 0.006;
    private const double AzusaPrefBalancedHandMaxBias = 0.003;
    private const double AzusaPrefAzusaHigherScreenMinDelta = 0.25;
    private const double AzusaPrefAzusaHigherMinDelta = 0.4;
    private const double AzusaPrefAnchorHeavyScreenMinRate = 0.72;
    private const double AzusaPrefAnchorHeavyMinRate = 0.78;
    private const double AzusaPrefAzusaLowerScreenMaxDelta = -0.55;
    private const double AzusaPrefAzusaLowerMaxDelta = -0.7;

    private static readonly Regex InvalidRe = new(@"^Invalid\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DanielTooLowRe = new(@"^<\s*alpha\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── JS-number helpers ───────────────────────────────────────────────────
    // JS `Number(x)`: null -> 0, undefined/unparseable -> NaN.
    private static double NumberJs(double? x) => x ?? 0.0;

    private static double? ToNum(object? raw)
    {
        switch (raw)
        {
            case null: return null;
            case double d: return double.IsFinite(d) ? d : null;
            case float f: return double.IsFinite(f) ? f : null;
            case int i: return i;
            case long l: return l;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                    && double.IsFinite(p)
                    ? p
                    : null;
            default: return null;
        }
    }

    // JS threads options.odFlag straight into runSunnyEstimatorFromText, which
    // accepts number | "HR" | "EZ". Our SunnyShim.Run takes double?; a non-numeric
    // flag is dropped. (Same treatment as DanielEstimator.)
    private static double? AsOdDouble(object? odFlag)
    {
        if (odFlag is double d) return d;
        if (odFlag is int i) return i;
        if (odFlag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
        {
            return p;
        }
        return null;
    }

    private static Dictionary<string, object?>? NestedDict(Dictionary<string, object?>? d, string key)
        => d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;

    // ── result-literal conversions (JS `{ ...x }` spread targets) ────────────
    private static LeoBlackReworkResult FromSunny(SunnyResult s) => new()
    {
        Star = s.Star,
        LnRatio = s.LnRatio,
        ColumnCount = s.ColumnCount,
        EstDiff = s.EstDiff,
        NumericDifficulty = s.NumericDifficulty,
        NumericDifficultyHint = s.NumericDifficultyHint,
        RawNumericDifficulty = null,
        Graph = s.Graph,
        Debug = new(),
    };

    private static LeoBlackReworkResult FromRc(RcEstimatorResult r) => new()
    {
        Star = r.Star,
        LnRatio = r.LnRatio,
        ColumnCount = r.ColumnCount,
        EstDiff = r.EstDiff,
        NumericDifficulty = r.NumericDifficulty,
        NumericDifficultyHint = r.NumericDifficultyHint,
        RawNumericDifficulty = r.RawNumericDifficulty,
        Graph = r.Graph,
        Debug = r.Debug,
    };

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

    // ── ported free functions ──────────────────────────────────────────────
    private static (bool InEnabled, bool HoEnabled) ParseCvtFlags(string? value)
    {
        string normalized = (value ?? "").ToUpperInvariant();
        return (normalized.Contains("IN"), normalized.Contains("HO"));
    }

    private static (string Rc, string Ln) SplitDifficultyParts(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return ("-", "-");
        }

        var parts = text
            .Split("||")
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

        if (parts.Count >= 2)
        {
            return (parts[0], parts[1]);
        }

        string first = parts.Count > 0 && parts[0].Length > 0 ? parts[0] : text;
        return (first, first);
    }

    public static string ComposeDifficultyFromRcLn(string? rcLabel, string? lnLabel, double lnRatio)
    {
        string rc = (rcLabel ?? "").Trim();
        string ln = (lnLabel ?? "").Trim();
        double ratio = lnRatio;

        if (!double.IsFinite(ratio) || ratio < 0.15)
        {
            return rc.Length > 0 ? rc : (ln.Length > 0 ? ln : "-");
        }

        if (rc.Length == 0)
        {
            return ln.Length > 0 ? ln : "-";
        }
        if (ln.Length == 0)
        {
            return rc;
        }
        return $"{rc} || {ln}";
    }

    public static bool IsDanielTooLowDifficulty(string? value)
    {
        string text = (value ?? "").Trim();
        return DanielTooLowRe.IsMatch(text);
    }

    private static RcEstimatorResult? TryRunDanielFallback(string osuText, EstimatorOptions options, OsuFileParser? parsed)
    {
        try
        {
            // JS runDanielEstimatorFromText returns a Sunny-shaped literal; adapt
            // to the RC literal used by the routing checks below.
            SunnyResult daniel = DanielEstimator.RunDanielEstimatorFromText(osuText, options, parsed);
            return DanielToRc(daniel);
        }
        catch
        {
            return null;
        }
    }

    private static RcEstimatorResult DanielToRc(SunnyResult s) => new()
    {
        Star = s.Star,
        LnRatio = s.LnRatio,
        ColumnCount = s.ColumnCount,
        EstDiff = s.EstDiff,
        NumericDifficulty = s.NumericDifficulty,
        NumericDifficultyHint = s.NumericDifficultyHint,
        Graph = s.Graph,
        RawNumericDifficulty = null,
        Debug = new(),
    };

    private static RcEstimatorResult? TryRunAzusaFallback(string osuText, EstimatorOptions options, OsuFileParser? parsed)
    {
        try
        {
            return AzusaEstimator.RunAzusaEstimatorFromText(osuText, options, parsed);
        }
        catch
        {
            return null;
        }
    }

    private static RcEstimatorResult? TryRunRoxyFallback(string osuText, EstimatorOptions options, OsuFileParser? parsed)
    {
        try
        {
            return RoxyEstimator.RunRoxyEstimatorFromText(osuText, options, parsed);
        }
        catch
        {
            return null;
        }
    }

    private static bool CanUseRcResult(RcEstimatorResult? result)
    {
        if (result == null || result.ColumnCount != 4)
        {
            return false;
        }

        string estDiff = (result.EstDiff ?? "").Trim();
        if (estDiff.Length == 0 || InvalidRe.IsMatch(estDiff))
        {
            return false;
        }

        // Roxy 高难聚焦的 scope 边界（"< Alpha Low" / "> Emik Zeta high"）返回
        // numericDifficulty null，视为不可用，路由到 Azusa（低难）。
        // (Roxy's high-difficulty-focus scope boundaries ("< Alpha Low" /
        // "> Emik Zeta high") return numericDifficulty null — treated as unusable,
        // routed to Azusa (low difficulty).)
        double? numeric = result.NumericDifficulty;
        if (numeric == null)
        {
            return false;
        }

        return true;
    }

    private static double? ResultNumericValue(RcEstimatorResult? result)
    {
        double? raw = result?.NumericDifficulty;
        if (raw == null) return null;
        double value = raw.Value;
        return double.IsFinite(value) ? value : null;
    }

    // 低难段融合计划：plan 携带 Azusa 的 RC 基准值（fuseRc），
    // applyCompanellaToMixedResult 在 Companella 结果到达后做 0.5/0.5 融合。
    // onDisagree 指定门控未通过时保留哪一侧（该分支改动前的原赢家），
    // 保证融合只在两参考一致且都主张低难时生效，其余行为与改动前一致。
    private static MixedCompanellaPlan BuildLowBandCompanellaPlan(
        RcEstimatorResult rcResult, double lnRatio, string lnDifficulty, string onDisagree)
        => new()
        {
            LnRatio = lnRatio,
            LnDifficulty = lnDifficulty,
            FuseRc = true,
            OnDisagree = onDisagree,
            RcEstDiff = rcResult.EstDiff,
            RcNumeric = ResultNumericValue(rcResult),
            RcNumericHint = rcResult.NumericDifficultyHint,
        };

    // Roxy 的 debug.finalNumeric 是全部后处理（OD 校正、结构下限、参考间隙、
    // Azusa 融合）之后的连续值，比 numericDifficulty（保留 2 位小数）更精确，
    // 换路判定基于它可避免舍入导致的 delta 抖动。
    // (Roxy's debug.finalNumeric is the continuous value after all post-processing
    // (OD correction, structural floor, reference gap, Azusa fusion) — more precise
    // than numericDifficulty (kept to 2dp) — so basing the reroute decision on it
    // avoids rounding-induced delta jitter.)
    private static double? RoxyUnquantizedNumeric(RcEstimatorResult? result)
    {
        object? raw = result?.Debug != null && result.Debug.TryGetValue("finalNumeric", out var fv) ? fv : null;
        if (raw != null && !(raw is string es && es.Length == 0))
        {
            double? value = ToNum(raw);
            if (value != null) return value;
        }
        return ResultNumericValue(result);
    }

    private static double? DebugStatValue(RcEstimatorResult? result, string name)
    {
        var stats = NestedDict(result?.Debug, "stats");
        if (stats == null || !stats.TryGetValue(name, out var raw) || raw == null) return null;
        return ToNum(raw);
    }

    private static double? DebugReferenceValue(RcEstimatorResult? result, string name)
    {
        var meta = NestedDict(result?.Debug, "meta");
        var references = NestedDict(meta, "references");
        if (references == null || !references.TryGetValue(name, out var raw) || raw == null) return null;
        return ToNum(raw);
    }

    private static bool ShouldEvaluateAzusaRcPreference(RcEstimatorResult? roxyResult)
    {
        if (!CanUseRcResult(roxyResult))
        {
            return false;
        }

        double? roxyNumeric = RoxyUnquantizedNumeric(roxyResult);
        double? azusaReference = DebugReferenceValue(roxyResult, "Azusa");
        double? handBias = DebugStatValue(roxyResult, "handBias");
        double? anchorRate = DebugStatValue(roxyResult, "anchorRate");
        if (roxyNumeric == null || azusaReference == null)
        {
            return false;
        }

        double delta = azusaReference.Value - roxyNumeric.Value;
        bool balancedHandCandidate = handBias != null
            && handBias.Value <= AzusaPrefBalancedHandScreenMaxBias
            && delta >= AzusaPrefAzusaHigherScreenMinDelta;
        bool anchorHeavyCandidate = anchorRate != null
            && anchorRate.Value >= AzusaPrefAnchorHeavyScreenMinRate
            && delta <= AzusaPrefAzusaLowerScreenMaxDelta;

        // 跨界规则：Roxy 输出已到 11+ 而 Azusa 参考低于 11（见 shouldPreferAzusaRcResult）。
        // (Crossing rule: Roxy output has reached 11+ while the Azusa reference is
        // below 11 (see shouldPreferAzusaRcResult).)
        bool crossingCandidate = roxyNumeric.Value >= 11 && azusaReference.Value < 11;

        return balancedHandCandidate || anchorHeavyCandidate || crossingCandidate;
    }

    public static bool ShouldPreferAzusaRcResult(RcEstimatorResult? roxyResult, RcEstimatorResult? azusaResult)
    {
        if (!CanUseRcResult(roxyResult) || !CanUseRcResult(azusaResult))
        {
            return false;
        }

        double? roxyNumeric = RoxyUnquantizedNumeric(roxyResult);
        double? azusaNumeric = ResultNumericValue(azusaResult);
        double? handBias = DebugStatValue(roxyResult, "handBias");
        double? anchorRate = DebugStatValue(roxyResult, "anchorRate");
        if (roxyNumeric == null || azusaNumeric == null)
        {
            return false;
        }

        double delta = azusaNumeric.Value - roxyNumeric.Value;
        bool balancedHandAzusaLift = handBias != null
            && handBias.Value <= AzusaPrefBalancedHandMaxBias
            && delta >= AzusaPrefAzusaHigherMinDelta;
        bool anchorHeavyRoxyDamp = anchorRate != null
            && anchorRate.Value >= AzusaPrefAnchorHeavyMinRate
            && delta <= AzusaPrefAzusaLowerMaxDelta;

        // 跨界规则：Roxy 输出已到 11+（Alpha 上界）而 Azusa 仍低于 11——这些图
        // 的 expected 段位接近 Alpha 边界，Azusa 的收敛输出（<11）通常更贴近真实
        // 段位（Roxy 的结构模型对"结构难但段位低"的图系统性高估）。仅当两个条件
        // 同时满足时触发，11~17 段正常图（Azusa 参考也在 11+）不受影响。
        // (Crossing rule: Roxy output has reached 11+ (Alpha upper bound) while Azusa
        // is still below 11 — these charts' expected dan is near the Alpha boundary,
        // and Azusa's converged output (<11) is usually closer to the true dan
        // (Roxy's structural model systematically overestimates "structurally hard
        // but low-dan" charts). Triggers only when both conditions hold; normal
        // 11~17 charts (Azusa reference also 11+) are unaffected.)
        bool crossingLift = roxyNumeric.Value >= 11 && azusaNumeric.Value < 11;

        return balancedHandAzusaLift || anchorHeavyRoxyDamp || crossingLift;
    }

    public static LeoBlackReworkResult RunMixedEstimatorFromText(
        string osuText, EstimatorOptions? options = null, OsuFileParser? parsed = null)
    {
        options ??= new EstimatorOptions();

        SunnyResult sunnyBaseline = options.PrecomputedSunnyResult ?? RunSunnyBaseline(osuText, options);
        // Track which sub-algorithm actually won the routing chain below so callers
        // (analysis pipeline → telemetry) can report the real algorithm, not "Mixed".
        string actualAlgorithm = "Sunny";
        double columnCount = sunnyBaseline.ColumnCount;
        if (!double.IsFinite(columnCount) || !MixedSupportedKeys.Contains(columnCount))
        {
            var passthrough = FromSunny(sunnyBaseline);
            passthrough.MixedCompanellaPlan = null;
            passthrough.ActualEstimatorAlgorithm = actualAlgorithm;
            return passthrough;
        }

        var (inEnabled, hoEnabled) = ParseCvtFlags(options.CvtFlag);
        bool hasExplicitOd = options.OdFlag != null;
        string mixedModeTag = hoEnabled ? "RC" : PatternsConfig.ModeTagFromLnRatio(sunnyBaseline.LnRatio);
        var sunnyParts = SplitDifficultyParts(sunnyBaseline.EstDiff);
        double lnRatio = sunnyBaseline.LnRatio;
        string lnDifficulty = sunnyParts.Ln;

        if (mixedModeTag == "RC" && columnCount != 4)
        {
            var passthrough = FromSunny(sunnyBaseline);
            passthrough.MixedCompanellaPlan = null;
            passthrough.ActualEstimatorAlgorithm = actualAlgorithm;
            return passthrough;
        }

        LeoBlackReworkResult selectedRework = FromSunny(sunnyBaseline);
        string estDiff = sunnyBaseline.EstDiff;
        double? numericDifficulty = sunnyBaseline.NumericDifficulty;
        string? numericDifficultyHint = sunnyBaseline.NumericDifficultyHint;
        MixedCompanellaPlan? companellaPlan = null;

        if (mixedModeTag == "RC")
        {
            var roxyResult = TryRunRoxyFallback(osuText, options.With(precomputedSunnyResult: sunnyBaseline), parsed);
            if (CanUseRcResult(roxyResult))
            {
                selectedRework = FromRc(roxyResult!);
                actualAlgorithm = "Roxy";
                estDiff = roxyResult!.EstDiff;
                numericDifficulty = roxyResult.NumericDifficulty;
                numericDifficultyHint = roxyResult.NumericDifficultyHint;
                if (!inEnabled && !hasExplicitOd && ShouldEvaluateAzusaRcPreference(roxyResult))
                {
                    var azusaOpts = options.With(precomputedSunnyResult: sunnyBaseline);
                    azusaOpts.ForceSunnyReferenceHo = false;
                    var azusaResult = TryRunAzusaFallback(osuText, azusaOpts, parsed);
                    if (ShouldPreferAzusaRcResult(roxyResult, azusaResult))
                    {
                        selectedRework = FromRc(azusaResult!);
                        actualAlgorithm = "Azusa";
                        estDiff = azusaResult!.EstDiff;
                        numericDifficulty = azusaResult.NumericDifficulty;
                        numericDifficultyHint = azusaResult.NumericDifficultyHint;
                    }
                }
            }
            else if (!inEnabled)
            {
                var azusaOpts = options.With(precomputedSunnyResult: sunnyBaseline);
                azusaOpts.ForceSunnyReferenceHo = false;
                var azusaResult = TryRunAzusaFallback(osuText, azusaOpts, parsed);
                if (CanUseRcResult(azusaResult))
                {
                    selectedRework = FromRc(azusaResult!);
                    actualAlgorithm = "Azusa";
                    estDiff = azusaResult!.EstDiff;
                    numericDifficulty = azusaResult.NumericDifficulty;
                    numericDifficultyHint = azusaResult.NumericDifficultyHint;
                    // 低难段（Roxy scope-out 且 Sunny star < 9）：RC 数值升级为
                    // Azusa⊕Companella 融合；门控未通过或 Companella 失败时，
                    // 本结果（纯 Azusa）兜底——与改动前行为一致。
                    // (Low band (Roxy scope-out and Sunny star < 9): the RC value is
                    // upgraded to an Azusa⊕Companella fusion; if the gate fails or
                    // Companella fails, this result (pure Azusa) is the fallback —
                    // consistent with the pre-change behaviour.)
                    if (sunnyBaseline.Star < LowBandCompanellaStarMax)
                    {
                        companellaPlan = BuildLowBandCompanellaPlan(azusaResult, lnRatio, lnDifficulty, "azusa");
                    }
                }
                else
                {
                    var danielResult = TryRunDanielFallback(osuText, options, parsed);
                    bool canUseDaniel = danielResult != null
                        && danielResult.ColumnCount == 4
                        && !IsDanielTooLowDifficulty(danielResult.EstDiff);

                    if (canUseDaniel)
                    {
                        selectedRework = FromRc(danielResult!);
                        actualAlgorithm = "Daniel";
                        estDiff = danielResult!.EstDiff;
                        numericDifficulty = danielResult.NumericDifficulty;
                        numericDifficultyHint = danielResult.NumericDifficultyHint;
                    }
                }
            }
        }
        else
        {
            string rcDifficulty = sunnyParts.Rc;
            double? rcNumericDifficulty = sunnyBaseline.NumericDifficulty;
            string? rcNumericDifficultyHint = sunnyBaseline.NumericDifficultyHint;

            if (columnCount == 4)
            {
                if (sunnyBaseline.Star < LowBandCompanellaStarMax)
                {
                    // 低难 4K：RC 部分以 Azusa 为基准与 Companella 融合（0.5/0.5）。
                    // 旧实现把 RC 段完全交给 Companella，对以 RC 为主的 Mix 图
                    // （如低段位 RC course）偏差更大；Azusa 无效时保留纯 Companella 行为。
                    // (Low-difficulty 4K: the RC part is fused with Companella using
                    // Azusa as the baseline (0.5/0.5). The old implementation handed
                    // the RC part entirely to Companella, which biased RC-dominant
                    // Mix charts (e.g. low-dan RC courses) more; when Azusa is invalid
                    // the pure-Companella behaviour is kept.)
                    var azusaOpts = options.With(precomputedSunnyResult: sunnyBaseline);
                    azusaOpts.ForceSunnyReferenceHo = false;
                    var azusaResult = TryRunAzusaFallback(osuText, azusaOpts, parsed);
                    if (CanUseRcResult(azusaResult))
                    {
                        rcDifficulty = azusaResult!.EstDiff;
                        rcNumericDifficulty = azusaResult.NumericDifficulty;
                        rcNumericDifficultyHint = azusaResult.NumericDifficultyHint;
                        actualAlgorithm = "Azusa";
                        companellaPlan = BuildLowBandCompanellaPlan(azusaResult, lnRatio, lnDifficulty, "companella");
                    }
                    else
                    {
                        companellaPlan = new MixedCompanellaPlan
                        {
                            LnRatio = lnRatio,
                            LnDifficulty = lnDifficulty,
                        };
                        actualAlgorithm = "Companella";
                    }
                }
                else
                {
                    var danielResult = TryRunDanielFallback(osuText, options, parsed);
                    bool canUseDaniel = danielResult != null
                        && danielResult.ColumnCount == 4
                        && !IsDanielTooLowDifficulty(danielResult.EstDiff);

                    if (canUseDaniel)
                    {
                        rcDifficulty = danielResult!.EstDiff;
                        rcNumericDifficulty = danielResult.NumericDifficulty;
                        rcNumericDifficultyHint = danielResult.NumericDifficultyHint;
                        actualAlgorithm = "Daniel";
                    }
                }
            }

            estDiff = ComposeDifficultyFromRcLn(rcDifficulty, lnDifficulty, lnRatio);
            numericDifficulty = rcNumericDifficulty;
            numericDifficultyHint = rcNumericDifficultyHint;
        }

        double normalizedLnRatio = selectedRework.LnRatio;
        double forcedLnRatio = hoEnabled ? 0 : normalizedLnRatio;

        var result = Clone(selectedRework);
        result.LnRatio = double.IsFinite(forcedLnRatio) ? forcedLnRatio : 0;
        result.EstDiff = estDiff;
        result.NumericDifficulty = numericDifficulty;
        result.NumericDifficultyHint = numericDifficultyHint;
        result.MixedCompanellaPlan = companellaPlan;
        result.ActualEstimatorAlgorithm = actualAlgorithm;
        return result;
    }

    public static LeoBlackReworkResult ApplyCompanellaToMixedResult(
        LeoBlackReworkResult mixedResult, CompanellaEstimate companellaResult)
    {
        var plan = mixedResult?.MixedCompanellaPlan;
        if (mixedResult == null || plan == null)
        {
            return mixedResult!;
        }

        // 低难融合路径：Companella 与计划携带的 Azusa RC 基准做固定权重平均，
        // estDiff 由融合数值重新派生（numeric 与 estDiff 保持同源）。
        // 门控：仅当 Azusa 数值低于 Alpha（低难主张成立）时融合；未通过时回落
        // onDisagree 指定的原赢家（RC 分支为 Azusa，Mix 分支为 Companella），
        // 行为与改动前一致。
        // (Low-band fusion path: Companella and the plan-carried Azusa RC baseline
        // are averaged with a fixed weight; estDiff is re-derived from the fused
        // numeric (numeric and estDiff stay same-sourced). Gate: fuse only when the
        // Azusa value is below Alpha (the low-difficulty claim holds); if it fails,
        // fall back to the original winner named by onDisagree (Azusa for the RC
        // branch, Companella for the Mix branch) — behaviour unchanged.)
        if (plan.FuseRc)
        {
            double rcNumeric = NumberJs(plan.RcNumeric);
            double companellaNumeric = NumberJs(companellaResult?.NumericDifficulty);
            if (!double.IsFinite(rcNumeric) || !double.IsFinite(companellaNumeric))
            {
                return mixedResult;
            }
            if (rcNumeric >= RcFusionLowBandMax)
            {
                if (plan.OnDisagree == "companella")
                {
                    var kept = Clone(mixedResult);
                    kept.EstDiff = ComposeDifficultyFromRcLn(
                        companellaResult!.EstDiff,
                        plan.LnDifficulty,
                        plan.LnRatio);
                    kept.NumericDifficulty = companellaResult.NumericDifficulty;
                    kept.NumericDifficultyHint = companellaResult.NumericDifficultyHint;
                    kept.MixedCompanellaPlan = null;
                    return kept;
                }
                return mixedResult;
            }
            double weight = RcAzusaCompanellaFusionWeight;
            double fused = Math.Round(
                rcNumeric * weight + companellaNumeric * (1 - weight),
                2, MidpointRounding.AwayFromZero);
            var fusedResult = Clone(mixedResult);
            fusedResult.EstDiff = ComposeDifficultyFromRcLn(
                RcDifficultyFormat.NumericToRcLabel(fused),
                plan.LnDifficulty,
                plan.LnRatio);
            fusedResult.NumericDifficulty = fused;
            fusedResult.NumericDifficultyHint = null;
            fusedResult.MixedCompanellaPlan = null;
            return fusedResult;
        }

        var applied = Clone(mixedResult);
        applied.EstDiff = ComposeDifficultyFromRcLn(
            companellaResult!.EstDiff,
            plan.LnDifficulty,
            plan.LnRatio);
        applied.NumericDifficulty = companellaResult.NumericDifficulty;
        applied.NumericDifficultyHint = companellaResult.NumericDifficultyHint;
        applied.MixedCompanellaPlan = null;
        return applied;
    }

    // JS `runSunnyEstimatorFromText(osuText, options, parsed)`. Our SunnyShim.Run
    // has a fixed positional signature (owned by the integration agent); a
    // non-numeric od flag is dropped (same as DanielEstimator).
    // PORT NOTE: `parsed` is not threaded into the shim — the shim re-parses.
    private static SunnyResult RunSunnyBaseline(string osuText, EstimatorOptions options)
        => SunnyShim.Run(
            osuText,
            options.SpeedRate ?? 1.0,
            AsOdDouble(options.OdFlag),
            options.CvtFlag,
            options.WithGraph);

    // ── mania-hub wrappers (live-backend/src/dan/leoblack-estimator.ts) ─────

    // NOTE on dedupe: the vendored Mixed chain already shares its expensive work.
    // Mixed computes Sunny once and threads it to Roxy/Azusa via
    // precomputedSunnyResult, and Roxy computes its Daniel/Azusa references once
    // and shares them internally. Do NOT precompute Daniel here and pass it via
    // precomputedDanielResult: Roxy canonicalizes the beatmap timing before running
    // its references, so an externally computed Daniel sees subtly different input
    // and shifts the meta numerics on charts with unusual timing (verified on the
    // dan corpus). Leave the calculation chain intact; only resolve fusion plans
    // that upstream's own apply step would decline.
    public static LeoBlackReworkResult RunLeoBlackMixed(string osuText, EstimatorOptions? options = null)
    {
        var result = RunMixedEstimatorFromText(osuText, options ?? new EstimatorOptions());
        var plan = result.MixedCompanellaPlan;
        // Upstream creates a plan before checking the Azusa < Alpha fusion scope.
        // Its apply step keeps Azusa outside that scope, but leaves the plan pending.
        // Resolve that no-op here so we never load ONNX for a verdict it cannot move.
        if (plan != null && plan.FuseRc && plan.OnDisagree == "azusa"
            && (plan.RcNumeric == null || !double.IsFinite(plan.RcNumeric.Value) || plan.RcNumeric.Value >= 11))
        {
            var cleared = Clone(result);
            cleared.MixedCompanellaPlan = null;
            return cleared;
        }
        return result;
    }

    // Direct Sunny baseline (no Roxy/Azusa/Daniel routing). The classifier uses it
    // to re-verdict charts whose Roxy raw signal is pinned at the scale floor; the
    // mixed router itself already lands on this exact result for charts Roxy
    // rejects outright (e.g. under its minimum note count).
    // PORT NOTE: JS returns `Omit<LeoBlackReworkResult, "mixedCompanellaPlan">`
    // (the raw Sunny literal). We return a LeoBlackReworkResult with
    // MixedCompanellaPlan left null.
    public static LeoBlackReworkResult RunLeoBlackSunny(string osuText, EstimatorOptions? options = null)
    {
        options ??= new EstimatorOptions();
        return FromSunny(RunSunnyBaseline(osuText, options));
    }
}
