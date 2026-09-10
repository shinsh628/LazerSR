// Port of mania-hub live-backend/vendor/leoblack/estimator/marathonCorrection.js
//
// 马拉松时长修正（Marathon Duration Correction）—— 共享 DOM-free 纯函数模块。
//
// 灵感来源：Dan-Overlay pipeline.py `_merge_primary_and_mina` 的马拉松时长修正
// （>300s + 技能均衡 → 时长修正、高难度 taper 渐减、只降不升）。
// 本模块将修正对象从 SR/DP 改为本项目的 `numericDifficulty`（段位数值，
// Roxy/Azusa 的语义输出），taper 从 SR 域改为 numeric 域，
// 修正式采用对数饱和（次线性）以保持相邻段位课程的相对顺序（reform 8th/9th 验收项）。
//
// 设计决策：
// - 修正对象：numericDifficulty。estDiff 由调用方同步重派生
//   （estDiff = numericToRcLabel(newNumeric)）；star 在本插件展示统一为 Sunny raw，
//   归一化会覆盖，无需在这里重算。
// - 时长惩罚对数饱和：corr = scale * ln(1 + excessMin)。线性随时长增长的惩罚
//   会翻转相邻段位课程的相对顺序（修正差 > base 差距）；对数在长端收敛修正差，
//   让排序由估算器 base 差支配。参数经 course 样本网格校准（见
//   docs/features/marathon-correction.md §8）。
// - taper：numeric <= 10 全量修正；10 ~ 16 线性降至 0；>= 16 不修正。
//   理由：低段位（Reform 1~10 马拉松课程包）时长虚高最严重，高段位校准稳定
//   （与 Dan-Overlay "SR>=7 不修"的机制意图一致，只是换到 numeric 域）。
// - 均衡条件：MSD skillsets 聚合出 4 个技能（jack/stream/stamina/tech），
//   max/total < 0.45 才触发——防止"真 marathon-jack 图"被时长误伤。
//   无 MSD 输入（ett 不可用）→ 返回 0，缺信号不动作。
// - 只降不升：应用后数值严格不高于原值。
//
// 两端（浏览器 worker / Node benchmark）同一实现，禁止 DOM/state 依赖。

namespace LazerSR.DanCalculator.Estimators;

public sealed class MarathonSkillAggregate
{
    public double Jk;
    public double St;
    public double Te;
    public double En;
    public double Total;
}

/// <summary>Optional parameter overrides for <see cref="MarathonCorrection.ComputeMarathonCorrection"/>.</summary>
public sealed class MarathonCorrectionParams
{
    public double ThresholdS = MarathonCorrection.MARATHON_DURATION_THRESHOLD_S;
    public double Scale = MarathonCorrection.MARATHON_CORRECTION_SCALE;
    public double Cap = MarathonCorrection.MARATHON_CORRECTION_CAP;
    public double BalanceRatio = MarathonCorrection.MARATHON_BALANCE_RATIO;
    public double TaperLo = MarathonCorrection.MARATHON_TAPER_LO;
    public double TaperHi = MarathonCorrection.MARATHON_TAPER_HI;
}

public static class MarathonCorrection
{
    // ── 参数 ────────────────────────────────────────────────────────────────

    public const double MARATHON_DURATION_THRESHOLD_S = 300;
    public const double MARATHON_CORRECTION_SCALE = 0.40;   // 对数域系数（v2.0.2 排序约束校准，见 docs/features/marathon-correction.md §8）
    public const double MARATHON_CORRECTION_CAP = 0.50;      // numeric 上限（排序约束校准）
    public const double MARATHON_BALANCE_RATIO = 0.45;       // max/total 均衡阈值
    public const double MARATHON_TAPER_LO = 10;
    public const double MARATHON_TAPER_HI = 16;

    // JS `Number(x) || 0` on a dictionary lookup (undefined -> NaN -> 0; 0 -> 0).
    private static double Val(Dictionary<string, double>? values, string key)
    {
        if (values != null && values.TryGetValue(key, out double v) && !double.IsNaN(v) && v != 0)
        {
            return v;
        }
        return 0;
    }

    // ── 技能聚合（ett values 键名：首字母大写，见 js/ett/calc.js）─

    public static MarathonSkillAggregate? AggregateSkillsets(Dictionary<string, double>? ettValues)
    {
        if (ettValues == null)
        {
            return null;
        }
        double jk = Math.Max(Val(ettValues, "JackSpeed"), Val(ettValues, "Chordjack"));
        double st = Math.Max(Val(ettValues, "Stream"), Val(ettValues, "Jumpstream"));
        double te = Val(ettValues, "Technical");
        double en = 0.7 * Val(ettValues, "Stamina")
            + 0.3 * Val(ettValues, "Handstream");
        double total = jk + st + te + en;
        if (!(total > 1))
        {
            return null;
        }
        return new MarathonSkillAggregate { Jk = jk, St = st, Te = te, En = en, Total = total };
    }

    // ── 核心计算 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 计算修正量（numeric 单位）。返回 0 表示不修正。
    /// (时长不足/技能不均衡/无 MSD/数值无效/taper 归零)
    /// </summary>
    public static double ComputeMarathonCorrection(
        double? durationS, Dictionary<string, double>? ettValues = null, double? numeric = null,
        MarathonCorrectionParams? @params = null)
    {
        var p = @params ?? new MarathonCorrectionParams();

        // JS `!(Number(durationS) > p.thresholdS)` — null/NaN also fail the comparison.
        double dur = durationS ?? double.NaN;
        if (!(dur > p.ThresholdS))
        {
            return 0;
        }
        // 严格数值类型判断：null/undefined/NaN 一律跳过
        if (numeric == null || !double.IsFinite(numeric.Value))
        {
            return 0;
        }
        double num = numeric.Value;

        // 技能均衡检查（无 MSD → 缺信号不动作）
        var agg = AggregateSkillsets(ettValues);
        if (agg == null
            || Math.Max(Math.Max(agg.Jk, agg.St), Math.Max(agg.Te, agg.En)) / agg.Total >= p.BalanceRatio)
        {
            return 0;
        }

        // 超出分钟数 → 对数饱和修正（次线性）：corr = scale * ln(1 + excessMin)。封顶 cap。
        double excessMin = (dur - p.ThresholdS) / 60;
        double raw = Math.Min(p.Cap, p.Scale * Math.Log(1 + excessMin));
        if (raw <= 0)
        {
            return 0;
        }

        // numeric taper：<= taperLo 全量；>= taperHi 归零；中间线性渐减
        double taper = 1;
        if (num >= p.TaperHi)
        {
            taper = 0;
        }
        else if (num > p.TaperLo)
        {
            taper = (p.TaperHi - num) / (p.TaperHi - p.TaperLo);
        }
        if (!(taper > 0))
        {
            return 0;
        }

        return raw * taper;
    }

    /// <summary>只降不升地应用修正。</summary>
    public static double ApplyMarathonCorrectionToNumeric(double numeric, double corr)
    {
        if (!double.IsFinite(numeric) || !(corr > 0))
        {
            return numeric;
        }
        return numeric - corr; // corr>0 必定使结果降低；边界 clamp 由调用方维持
    }

    /// <summary>
    /// 对 RC 估算结果应用修正并同步重派生 estDiff（numericToRcLabel）。
    /// 只处理 numericDifficulty 与 estDiff 两个字段；star 不在此处理。
    /// 未修正时返回原引用。
    /// </summary>
    public static SunnyResult ApplyMarathonCorrectionToRcResult(SunnyResult result, MarathonCorrectionInput? input)
    {
        double? numeric = result.NumericDifficulty;
        // 同 computeMarathonCorrection：严格类型判断，null（scope 外/无效）不修正
        if (numeric == null || !double.IsFinite(numeric.Value))
        {
            return result;
        }
        double corr = ComputeMarathonCorrection(
            input?.DurationS,
            input?.EttValues,
            numeric);
        if (!(corr > 0))
        {
            return result;
        }

        double corrected = ApplyMarathonCorrectionToNumeric(numeric.Value, corr);
        return new SunnyResult
        {
            Star = result.Star,
            LnRatio = result.LnRatio,
            ColumnCount = result.ColumnCount,
            Graph = result.Graph,
            NumericDifficultyHint = result.NumericDifficultyHint,
            NumericDifficulty = Math.Round(corrected, 2, MidpointRounding.AwayFromZero),
            EstDiff = RcDifficultyFormat.NumericToRcLabel(corrected),
        };
    }
}
