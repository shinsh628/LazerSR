// Port of vendor/leoblack/patterns/config.js
// Exports PATTERNS_CONFIG as a static class of consts + ModeTagFromLnRatio.
// mania-hub local patch: CLUSTER_TIMED_MIN_MSPB (mixed-BPM sentinel handling).

namespace LazerSR.DanCalculator.Patterns;

public static class PatternsConfig
{
    private static readonly Dictionary<string, double> RC_SUBTYPE_BASE = new()
    {
        ["Rolls"] = 1.0 / 3.0,
        ["Trills"] = 1.0 / 3.0,
        ["Minitrills"] = 1.0 / 3.0,
        ["Handstream"] = 0.65,
        ["Split Trill"] = 0.65,
        ["Jumptrill"] = 0.65,
        ["Jumpstream"] = 0.65,
        ["Brackets"] = 0.65,
        ["Double Stream"] = 0.65,
        ["Dense Chordstream"] = 0.65,
        ["Light Chordstream"] = 0.65,
        ["Chord Rolls"] = 0.65,
        ["Longjacks"] = 0.9,
        ["Quadstream"] = 0.9,
        ["Gluts"] = 0.9,
        ["Chordjacks"] = 0.9,
        ["Minijacks"] = 0.9,
    };

    private static readonly Dictionary<string, double> LN_SUBTYPE_BASE = new()
    {
        ["Column Lock"] = 1.5,
        ["Release"] = 0.73,
        ["Shield"] = 0.8,
        ["JS Density"] = 1.0,
        ["HS Density"] = 1.0,
        ["DS Density"] = 1.0,
        ["LCS Density"] = 1.0,
        ["DCS Density"] = 1.0,
        ["Inverse"] = 1.5,
        ["Jacky WC"] = 0.55,
        ["Speedy WC"] = 0.8,
    };

    private static Dictionary<string, double> Merge(params Dictionary<string, double>[] maps)
    {
        var outMap = new Dictionary<string, double>();
        foreach (var m in maps)
            foreach (var kv in m)
                outMap[kv.Key] = kv.Value;
        return outMap;
    }

    public static readonly Dictionary<string, double> CORE_RATING_MULTIPLIER = new()
    {
        ["Stream"] = 1.0 / 3.0,
        ["Chordstream"] = 0.65,
        ["Jacks"] = 0.9,
        ["Coordination"] = 0.75,
        ["Density"] = 0.9,
        ["Wildcard"] = 1.0,
    };

    public static readonly Dictionary<string, Dictionary<string, double>> SUBTYPE_RATING_MULTIPLIER_BY_MODE = new()
    {
        ["RC"] = Merge(RC_SUBTYPE_BASE, LN_SUBTYPE_BASE),
        ["LN"] = Merge(RC_SUBTYPE_BASE, new Dictionary<string, double>
        {
            ["Column Lock"] = 1.5,
            ["Release"] = 1.0,
            ["Shield"] = 0.8,
            ["JS Density"] = 0.9,
            ["HS Density"] = 0.9,
            ["DS Density"] = 0.9,
            ["LCS Density"] = 0.9,
            ["DCS Density"] = 0.9,
            ["Inverse"] = 1.5,
            ["Jacky WC"] = 0.55,
            ["Speedy WC"] = 0.8,
        }),
        ["HB"] = Merge(RC_SUBTYPE_BASE, new Dictionary<string, double>
        {
            ["Column Lock"] = 1.5,
            ["Release"] = 0.3,
            ["Shield"] = 0.8,
            ["JS Density"] = 0.9,
            ["HS Density"] = 0.9,
            ["DS Density"] = 0.9,
            ["LCS Density"] = 0.9,
            ["DCS Density"] = 0.9,
            ["Inverse"] = 0.0,
            ["Jacky WC"] = 0.65,
            ["Speedy WC"] = 0.45,
        }),
        ["Mix"] = Merge(RC_SUBTYPE_BASE, new Dictionary<string, double>
        {
            ["Column Lock"] = 1.5,
            ["Release"] = 0.3,
            ["Shield"] = 0.8,
            ["JS Density"] = 0.9,
            ["HS Density"] = 0.9,
            ["DS Density"] = 0.9,
            ["LCS Density"] = 0.9,
            ["DCS Density"] = 0.9,
            ["Inverse"] = 0.0,
            ["Jacky WC"] = 0.45,
            ["Speedy WC"] = 0.45,
        }),
    };

    public const double RC_CORE_LN_SCALE = 0.3;
    public const double RC_LN_CORE_SCALE = 0.0;
    public const double RELEASE_WITH_DW_MULTIPLIER = 0.8;
    public const double LN_MODE_LOW_THRESHOLD = 0.15;
    public const double LN_MODE_HIGH_THRESHOLD = 0.9;
    public const double HB_ROW_RATIO_THRESHOLD = 0.1;
    public const double BPM_CLUSTER_THRESHOLD = 5.0;

    // A window's MsPerBeat is its row gap x4 (primitives.js), so 40 here is a
    // 10ms row gap: LN tails landing just before the next head, grace notes,
    // stacked rows. Below it a window carries no tempo, only presence, and it
    // must not vote in a cluster's BPM (clustering.js) - the pools that let
    // them vote read "15000BPM Inverse" / "20000BPM Coordination" on /maps.
    // Also the ceiling of any computable cluster BPM: 60000 / 40 = 1500.
    public const double CLUSTER_TIMED_MIN_MSPB = 40.0;

    public const double PATTERN_STABILITY_THRESHOLD = 5.0;
    public const double IMPORTANT_CLUSTER_RATIO = 0.5;
    public const double CATEGORY_JS_HS_SECONDARY_RATIO = 0.4;
    public const double SV_AMOUNT_THRESHOLD = 2000.0;
    public const double SV_SPEED_EPS = 0.05;
    public const double SV_EXTREME_BPM_MIN = 20.0;
    public const double SV_EXTREME_BPM_MAX = 450.0;
    public const double SV_EXTREME_BPM_RATIO = 4.0;
    public const double LONGJACK_VIBRO_RATIO_THRESHOLD = 0.6;
    public const double LONGJACK_VIBRO_MIN_BPM = 180;
    public const double CLUSTER_SPECIFIC_NAME_MIN_RATIO = 0.0;
    public const bool ENABLE_MULTI_LABEL_SAME_WINDOW = true;
    public static readonly string[] COORDINATION_SPECIFIC_ORDER = { "Column Lock", "Shield", "Release" };
    public static readonly string[] DENSITY_SPECIFIC_ORDER = { "Inverse", "JS Density", "HS Density", "DS Density", "DCS Density", "LCS Density" };
    public static readonly string[] WILDCARD_SPECIFIC_ORDER = { "Speedy WC", "Jacky WC" };
    public const double JACKY_MIN_BPM = 90.0;
    public const double SHIELD_MAX_BEAT_RATIO = 0.25;
    public const double INVERSE_GAP_TOLERANCE_MS = 5.0;
    public const int INVERSE_MIN_FILLED_LANES = 3;
    public const int RELEASE_SCAN_ROWS = 4;
    public const int RELEASE_MIN_TAIL_ROWS = 4;
    public const int RELEASE_ROLL_POINTS = 2;
    public const int RELEASE_FULL_MATCH_ROWS = 5;
    public const int JACKY_CONTEXT_WINDOW = 6;
    public const double JACKY_FALLBACK_MAX_MSPB = 185.0;

    // Shared mode-tag threshold logic (moved out of mixedEstimator, same thresholds).
    public static string ModeTagFromLnRatio(double lnRatio)
    {
        if (!double.IsFinite(lnRatio))
        {
            return "Mix";
        }
        if (lnRatio <= LN_MODE_LOW_THRESHOLD)
        {
            return "RC";
        }
        if (lnRatio >= LN_MODE_HIGH_THRESHOLD)
        {
            return "LN";
        }
        return "Mix";
    }
}
