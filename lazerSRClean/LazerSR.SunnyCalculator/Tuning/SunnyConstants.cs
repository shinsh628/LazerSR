using System;
using System.Threading;

namespace LazerSR.SunnyCalculator.Tuning;

/// <summary>
/// The 39 sunnySR constants, lifted out of the calculation so they can be shifted without editing
/// the pipeline. Every value is <c>stock + delta</c>.
///
/// Two delta sources exist, and they are kept strictly apart:
///
/// <list type="bullet">
/// <item><b>Process-wide default</b> — <see cref="DiffCombiner"/>'s result (today: the everyone-diff
/// alone). This is what every existing sunny consumer reads (song select pills, tooltips, results
/// screen, ...) via the plain properties below. <see cref="Reload"/> recomputes it.</item>
/// <item><b>Isolated diff</b> — set with <see cref="WithIsolatedDiff{T}"/> for one call context only
/// (an <see cref="AsyncLocal{T}"/>, so it flows through <c>Task.Run</c> but never leaks to a sibling
/// task or thread). This is for personal-diff consumers and Jacobian baking, which both need to swap
/// constants transiently and must not perturb the value every other consumer reads while they do it.</item>
/// </list>
///
/// A read inside an isolated scope sees the isolated values; a read anywhere else sees the process-wide
/// default, regardless of what any isolated scope elsewhere is doing concurrently.
/// </summary>
public static class SunnyConstants
{
    // ---- final aggregation (Difficulty/Skills/Strain.cs) ----
    public static double HighPercentileWeight => Value(Index.HighPercentileWeight);
    public static double MidPercentileWeight => Value(Index.MidPercentileWeight);
    public static double PowerMeanWeight => Value(Index.PowerMeanWeight);
    public static double PowerMeanLambda => Value(Index.PowerMeanLambda);
    public static double HighPercentileCentre => Value(Index.HighPercentileCentre);
    public static double MidPercentileCentre => Value(Index.MidPercentileCentre);

    // ---- per-note combination (Difficulty/Evaluators/StrainEvaluator.cs) ----
    /// <summary>Weight of the jack component; the pressing component gets 1 - this.</summary>
    public static double MixFirst => Value(Index.MixFirst);
    /// <summary>Inner exponent; the outer exponent is its reciprocal.</summary>
    public static double MixExponent => Value(Index.MixExponent);
    /// <summary>2.7 / 0.27 in stock sunny. The numerator stays 2.7; only the ratio carries information.</summary>
    public static double TwistRatio => Value(Index.TwistRatio);
    public static double PressingWeight => Value(Index.PressingWeight);
    public static double ReleaseDensityNorm => Value(Index.ReleaseDensityNorm);
    public static double JackKnee => Value(Index.JackKnee);

    // ---- 4K cross-hand coefficients (CrossColumnPreprocessor) ----
    public static double CrossOuter => Value(Index.CrossOuter);
    public static double CrossInner => Value(Index.CrossInner);
    public static double CrossCentre => Value(Index.CrossCentre);

    // ---- stream boost (PressingIntensityPreprocessor) ----
    public static double StreamBoostMinRatio => Value(Index.StreamBoostMinRatio);
    public static double StreamBoostMaxRatio => Value(Index.StreamBoostMaxRatio);

    // ---- jack nerf (SameColumnPreprocessor) ----
    public static double JackOptimalInterval => Value(Index.JackOptimalInterval);
    public static double JackNerfCoefficient => Value(Index.JackNerfCoefficient);

    // ---- unevenness gate (UnevennessPreprocessor) ----
    public static double UnevennessFloor => Value(Index.UnevennessFloor);
    public static double UnevennessLowThreshold => Value(Index.UnevennessLowThreshold);
    public static double UnevennessHighThreshold => Value(Index.UnevennessHighThreshold);

    // ---- conditional ----
    public static double FastPressureCoefficient => Value(Index.FastPressureCoefficient);
    public static double FastPressureOffset => Value(Index.FastPressureOffset);
    public static double ChordScale => Value(Index.ChordScale);
    public static double AnchorCubicOffset => Value(Index.AnchorCubicOffset);
    public static double AnchorLinearOffset => Value(Index.AnchorLinearOffset);
    public static double AnchorCubicMultiplier => Value(Index.AnchorCubicMultiplier);
    public static double LongNoteBoost => Value(Index.LongNoteBoost);
    public static double ReleaseIdealSlack => Value(Index.ReleaseIdealSlack);
    public static double ReleaseMultiplier => Value(Index.ReleaseMultiplier);
    public static double HitLeniencyKneeSlope => Value(Index.HitLeniencyKneeSlope);
    public static double HitLeniencyKneePoint => Value(Index.HitLeniencyKneePoint);
    public static double LnDensityEarlyMs => Value(Index.LnDensityEarlyMs);
    public static double LnDensityMidMs => Value(Index.LnDensityMidMs);
    public static double LnDensityUp => Value(Index.LnDensityUp);
    public static double LnDensityMid => Value(Index.LnDensityMid);
    public static double LnDensityClampBase => Value(Index.LnDensityClampBase);
    public static double LnDensityClampSlope => Value(Index.LnDensityClampSlope);

    // ---- derived: these never carry a delta of their own ----
    //
    // Constraints stock sunny already satisfies, kept here so a shifted constant cannot tear the
    // curve it belongs to (a discontinuous unevenness gate, a long note whose density never returns
    // to zero, and so on). Each reads the properties above, so it automatically follows whichever
    // scope (default or isolated) is active for the caller.

    public static double MixSecond => 1.0 - MixFirst;
    public static double MixOuterExponent => 1.0 / MixExponent;
    public static double TwistFirst => 2.7;
    public static double TwistSecond => 2.7 / TwistRatio;

    public static double UnevennessSlope =>
        (1.0 - UnevennessFloor) / (UnevennessHighThreshold - UnevennessLowThreshold);

    public static double UnevennessIntercept =>
        UnevennessFloor - UnevennessSlope * UnevennessLowThreshold;

    /// <summary>Density must return to exactly zero when a long note ends.</summary>
    public static double LnDensityEnd => -(LnDensityUp + LnDensityMid);

    public static double[] HighPercentiles => centredPercentiles(HighPercentileCentre);
    public static double[] MidPercentiles => centredPercentiles(MidPercentileCentre);

    private static double[] centredPercentiles(double centre) =>
        new[] { centre + 0.015, centre + 0.005, centre - 0.005, centre - 0.015 };

    // ---- stock values and delta application ----

    /// <summary>
    /// Stock sunny, in the order every diff vector uses: a diff's index i shifts <see cref="Names"/>[i].
    /// </summary>
    public static readonly double[] Stock =
    {
        0.22,   // HighPercentileWeight
        0.188,  // MidPercentileWeight
        0.55,   // PowerMeanWeight
        5.0,    // PowerMeanLambda
        0.930,  // HighPercentileCentre
        0.830,  // MidPercentileCentre
        0.4,    // MixFirst
        1.5,    // MixExponent
        10.0,   // TwistRatio
        0.8,    // PressingWeight
        35.0,   // ReleaseDensityNorm
        8.0,    // JackKnee
        0.175,  // CrossOuter
        0.25,   // CrossInner
        0.05,   // CrossCentre
        160.0,  // StreamBoostMinRatio
        360.0,  // StreamBoostMaxRatio
        0.08,   // JackOptimalInterval
        7e-5,   // JackNerfCoefficient
        0.75,   // UnevennessFloor
        0.02,   // UnevennessLowThreshold
        0.07,   // UnevennessHighThreshold
        0.4,    // FastPressureCoefficient
        80.0,   // FastPressureOffset
        1000.0, // ChordScale
        0.22,   // AnchorCubicOffset
        0.18,   // AnchorLinearOffset
        5.0,    // AnchorCubicMultiplier
        6.0,    // LongNoteBoost
        80.0,   // ReleaseIdealSlack
        0.8,    // ReleaseMultiplier
        0.6,    // HitLeniencyKneeSlope
        0.09,   // HitLeniencyKneePoint
        60.0,   // LnDensityEarlyMs
        120.0,  // LnDensityMidMs
        1.3,    // LnDensityUp
        -0.3,   // LnDensityMid
        2.5,    // LnDensityClampBase
        0.5,    // LnDensityClampSlope
    };

    /// <summary>Index -> constant name, in the same order as <see cref="Stock"/>.</summary>
    public static readonly string[] Names =
    {
        "HighPercentileWeight", "MidPercentileWeight", "PowerMeanWeight", "PowerMeanLambda",
        "HighPercentileCentre", "MidPercentileCentre", "MixFirst", "MixExponent", "TwistRatio",
        "PressingWeight", "ReleaseDensityNorm", "JackKnee", "CrossOuter", "CrossInner",
        "CrossCentre", "StreamBoostMinRatio", "StreamBoostMaxRatio", "JackOptimalInterval",
        "JackNerfCoefficient", "UnevennessFloor", "UnevennessLowThreshold",
        "UnevennessHighThreshold", "FastPressureCoefficient", "FastPressureOffset", "ChordScale",
        "AnchorCubicOffset", "AnchorLinearOffset", "AnchorCubicMultiplier", "LongNoteBoost",
        "ReleaseIdealSlack", "ReleaseMultiplier", "HitLeniencyKneeSlope", "HitLeniencyKneePoint",
        "LnDensityEarlyMs", "LnDensityMidMs", "LnDensityUp", "LnDensityMid", "LnDensityClampBase",
        "LnDensityClampSlope",
    };

    public static int Count => Stock.Length;

    /// <summary>
    /// Positional indices into <see cref="Stock"/>/<see cref="Names"/>, named for readability at call
    /// sites below. Coupled to <see cref="Names"/> by position exactly like <see cref="Stock"/> already
    /// is — reorder one and the others must follow.
    /// </summary>
    private static class Index
    {
        public const int HighPercentileWeight = 0;
        public const int MidPercentileWeight = 1;
        public const int PowerMeanWeight = 2;
        public const int PowerMeanLambda = 3;
        public const int HighPercentileCentre = 4;
        public const int MidPercentileCentre = 5;
        public const int MixFirst = 6;
        public const int MixExponent = 7;
        public const int TwistRatio = 8;
        public const int PressingWeight = 9;
        public const int ReleaseDensityNorm = 10;
        public const int JackKnee = 11;
        public const int CrossOuter = 12;
        public const int CrossInner = 13;
        public const int CrossCentre = 14;
        public const int StreamBoostMinRatio = 15;
        public const int StreamBoostMaxRatio = 16;
        public const int JackOptimalInterval = 17;
        public const int JackNerfCoefficient = 18;
        public const int UnevennessFloor = 19;
        public const int UnevennessLowThreshold = 20;
        public const int UnevennessHighThreshold = 21;
        public const int FastPressureCoefficient = 22;
        public const int FastPressureOffset = 23;
        public const int ChordScale = 24;
        public const int AnchorCubicOffset = 25;
        public const int AnchorLinearOffset = 26;
        public const int AnchorCubicMultiplier = 27;
        public const int LongNoteBoost = 28;
        public const int ReleaseIdealSlack = 29;
        public const int ReleaseMultiplier = 30;
        public const int HitLeniencyKneeSlope = 31;
        public const int HitLeniencyKneePoint = 32;
        public const int LnDensityEarlyMs = 33;
        public const int LnDensityMidMs = 34;
        public const int LnDensityUp = 35;
        public const int LnDensityMid = 36;
        public const int LnDensityClampBase = 37;
        public const int LnDensityClampSlope = 38;
    }

    /// <summary>The process-wide default (stock + <see cref="DiffCombiner"/>'s result). Set by <see cref="Reload"/>.</summary>
    private static double[] _global = Array.Empty<double>();

    /// <summary>
    /// The isolated override for the current call context, or null if none is active. An
    /// <see cref="AsyncLocal{T}"/> flows through <c>Task.Run</c>/<c>await</c> but a write on one logical
    /// call context is never visible from a sibling one — this is what keeps baking from perturbing the
    /// value every other sunny consumer reads.
    /// </summary>
    private static readonly AsyncLocal<double[]?> isolatedOverride = new();

    static SunnyConstants() => Reload();

    /// <summary>Re-reads <see cref="DiffCombiner"/>'s result and applies it on top of stock as the process-wide default.</summary>
    public static void Reload() => _global = computeValues(DiffCombiner.Combine());

    /// <summary>Index of a constant by name, or -1. Lets a diff be written without hard-coded offsets.</summary>
    public static int IndexOf(string name) => System.Array.IndexOf(Names, name);

    /// <summary>True while an isolated diff is active on the calling context — for the caller's own status
    /// display only. Reads outside a <see cref="WithIsolatedDiff{T}"/> scope are never affected regardless
    /// of this, so no other consumer needs to check it for correctness.</summary>
    public static bool IsIsolatedScopeActive => isolatedOverride.Value != null;

    /// <summary>
    /// AsyncLocal companion to <see cref="isolatedOverride"/>. Every existing isolated-diff consumer
    /// (personal-diff baking/display) is inherently non-stock, so <c>Strain.DifficultyValue()</c> treats
    /// "isolated scope active" alone as reason enough to take the shifted-constant-set tail. That is wrong
    /// for a caller that isolates specifically to force a truly vanilla (zero-delta) read regardless of the
    /// ambient sunny+ checkbox — this flag lets <see cref="WithIsolatedDiff{T}(double[], bool, Func{T})"/>
    /// tell <c>Strain</c> to use the stock high-SR rescale instead, exactly as if no diff (universal or
    /// isolated) were active at all.
    /// </summary>
    private static readonly AsyncLocal<bool> forceVanillaTailOverride = new();

    /// <summary>See <see cref="forceVanillaTailOverride"/>.</summary>
    public static bool ForceVanillaTail => forceVanillaTailOverride.Value;

    /// <summary>
    /// Runs <paramref name="action"/> with <paramref name="deltas"/> (a full stock-relative delta vector —
    /// e.g. everyone-diff + personal-diff, or that plus one perturbed dimension for Jacobian baking) active
    /// for every <see cref="SunnyConstants"/> read <paramref name="action"/> triggers, directly or through
    /// nested calls on the same logical call context. The process-wide default every other consumer reads
    /// is untouched, so this can run concurrently with normal song select / results screen sunny calculations
    /// without corrupting them.
    /// </summary>
    public static T WithIsolatedDiff<T>(double[] deltas, Func<T> action) => WithIsolatedDiff(deltas, forceVanillaTail: false, action);

    /// <summary>
    /// Overload of <see cref="WithIsolatedDiff{T}(double[], Func{T})"/> that also lets the caller force the
    /// stock high-SR rescale tail (see <see cref="ForceVanillaTail"/>) instead of the shifted-constant-set
    /// nerf that isolated scopes otherwise always take. Pass an all-zero <paramref name="deltas"/> together
    /// with <c>forceVanillaTail: true</c> to get a value that is bit-for-bit what stock sunny with sunny+
    /// off would produce, independent of the live <see cref="DiffCombiner.UniversalDiffEnabled"/> flag and
    /// safe to run concurrently with it (AsyncLocal, not the shared mutable flag).
    /// </summary>
    public static T WithIsolatedDiff<T>(double[] deltas, bool forceVanillaTail, Func<T> action)
    {
        double[] values = computeValues(deltas);
        double[]? previous = isolatedOverride.Value;
        bool previousVanilla = forceVanillaTailOverride.Value;
        isolatedOverride.Value = values;
        forceVanillaTailOverride.Value = forceVanillaTail;

        try
        {
            return action();
        }
        finally
        {
            isolatedOverride.Value = previous;
            forceVanillaTailOverride.Value = previousVanilla;
        }
    }

    private static double Value(int index)
    {
        double[]? overrideValues = isolatedOverride.Value;
        return (overrideValues ?? _global)[index];
    }

    private static double[] computeValues(double[] deltas)
    {
        if (deltas.Length != Stock.Length)
            throw new ArgumentException($"expected {Stock.Length} deltas, got {deltas.Length}");

        var values = new double[Stock.Length];

        for (int i = 0; i < Stock.Length; i++)
            values[i] = Stock[i] + deltas[i];

        return values;
    }
}
