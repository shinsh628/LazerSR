// Shared options bag for the LeoBlack estimator blend components (Daniel / Azusa /
// Roxy). The JS estimators each take a loose `options = {}` object; this DTO
// collects every field any of them reads. Not a 1:1 port of a single JS file —
// PORT NOTE: extracted so the three estimator ports share one option surface.
//
// Fields mirror the JS option keys, PascalCased. `null` / unset means "JS
// `options.foo` was undefined".

namespace LazerSR.DanCalculator.Estimators;

/// <summary>JS `options.marathonCorrection` — `{ durationS, ettValues }`.</summary>
public sealed class MarathonCorrectionInput
{
    /// <summary>JS `durationS` — beatmap drain duration in seconds (NOT speed-scaled).</summary>
    public double? DurationS;

    /// <summary>
    /// JS `ettValues` — MSD skillset map (capitalised keys: Overall/Stream/Jumpstream/
    /// Handstream/Stamina/JackSpeed/Chordjack/Technical). Caller computes MSD; the
    /// correction just consumes it. <c>null</c> when MSD unavailable.
    /// </summary>
    public Dictionary<string, double>? EttValues;
}

public sealed class EstimatorOptions
{
    /// <summary>JS `options.speedRate` (default 1.0 at each call site).</summary>
    public double? SpeedRate;

    /// <summary>
    /// JS `options.odFlag` — a number, or the strings "HR"/"EZ" (Roxy also reads
    /// `OD`/`od`/`overallDifficulty` aliases via <see cref="ResolveOdFlagRaw"/>).
    /// </summary>
    public object? OdFlag;

    /// <summary>Roxy alias for <see cref="OdFlag"/> (JS `options.OD`).</summary>
    public object? OD;

    /// <summary>Roxy alias for <see cref="OdFlag"/> (JS `options.od`).</summary>
    public object? Od;

    /// <summary>Roxy alias for <see cref="OdFlag"/> (JS `options.overallDifficulty`).</summary>
    public object? OverallDifficulty;

    /// <summary>JS `options.cvtFlag` — "HO" / "IN" / null.</summary>
    public string? CvtFlag;

    /// <summary>JS `options.withGraph === true`.</summary>
    public bool WithGraph;

    /// <summary>JS `options.extendedEstimationRange === true`.</summary>
    public bool? ExtendedEstimationRange;

    /// <summary>JS `options.enableAlwaysShowLNDifficulty === true`.</summary>
    public bool? EnableAlwaysShowLNDifficulty;

    /// <summary>JS `options.forceSunnyReferenceHo !== false` (Azusa; default true).</summary>
    public bool ForceSunnyReferenceHo = true;

    /// <summary>JS `options.precomputedDanielResult` — a Daniel/Sunny estimator result.</summary>
    public SunnyResult? PrecomputedDanielResult;

    /// <summary>JS `options.precomputedSunnyResult` — a Sunny estimator result.</summary>
    public SunnyResult? PrecomputedSunnyResult;

    /// <summary>JS `options.marathonCorrection`.</summary>
    public MarathonCorrectionInput? MarathonCorrection;

    /// <summary>JS `options.odFlag ?? options.OD ?? options.od ?? options.overallDifficulty ?? null`.</summary>
    public object? ResolveOdFlagRaw()
        => OdFlag ?? OD ?? Od ?? OverallDifficulty;

    /// <summary>Shallow copy with `withGraph` overridden — JS `{ ...options, withGraph }`.</summary>
    public EstimatorOptions With(bool? withGraph = null, string? cvtFlag = null, object? odFlag = null,
        double? speedRate = null, SunnyResult? precomputedSunnyResult = null,
        SunnyResult? precomputedDanielResult = null)
        => new()
        {
            SpeedRate = speedRate ?? SpeedRate,
            OdFlag = odFlag ?? OdFlag,
            OD = OD,
            Od = Od,
            OverallDifficulty = OverallDifficulty,
            CvtFlag = cvtFlag ?? CvtFlag,
            WithGraph = withGraph ?? WithGraph,
            ExtendedEstimationRange = ExtendedEstimationRange,
            EnableAlwaysShowLNDifficulty = EnableAlwaysShowLNDifficulty,
            ForceSunnyReferenceHo = ForceSunnyReferenceHo,
            PrecomputedDanielResult = precomputedDanielResult ?? PrecomputedDanielResult,
            PrecomputedSunnyResult = precomputedSunnyResult ?? PrecomputedSunnyResult,
            MarathonCorrection = MarathonCorrection,
        };
}
