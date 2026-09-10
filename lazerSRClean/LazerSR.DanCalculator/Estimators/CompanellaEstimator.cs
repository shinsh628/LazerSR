// Port of mania-hub live-backend/vendor/leoblack/estimator/companellaEstimator.js
// (see companellaEstimator.d.ts for the consumed shape).
//
// The JS lazily imports onnxruntime-web and picks a namespace (getOrtNamespace /
// getModelSession). That is all replaced by a single process-lifetime
// InferenceSession, created lazily under a lock, disposed never.
//
// PORT NOTE (ONNX I/O): the bundled Assets/dan_model.onnx was inspected offline:
//   input  "X"        float32  [batch, 10]   (FEATURE_COUNT = 10)
//   output "variable" float32  [batch, 1]
//   (an sklearn-onnx MLPRegressor export). We still resolve the names from
//   session.InputNames/OutputNames at runtime like the JS
//   (session.inputNames?.[0]), falling back to "X" / "variable".
// PORT NOTE (features): msdValues here is the lowercase-keyed dictionary produced
//   by Msd.ComputeMsd (PORTING.md §"MSD substitution"), not the JS PascalCase
//   object. normalizeMsdInput maps overall/stream/.../jackspeed/chordjack/technical.
// PORT NOTE (async -> sync): classifyCompanellaDifficulty is async in JS purely for
//   the dynamic import + session.run promise; the C# port is synchronous.
// PORT NOTE (Number(x.toFixed(2))): implemented as Math.Round(x, 2,
//   MidpointRounding.AwayFromZero) — JS toFixed is round-half-up modulo binary
//   float representation; away-from-zero at 2dp matches in every realistic case.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LazerSR.DanCalculator.Estimators;

public sealed class CompanellaEstimate
{
    /// <summary>Display difficulty, e.g. "Reform 8 mid/low" or "Epsilon low".</summary>
    public string EstDiff = "";
    /// <summary>Model output clamped to [1, 20] and shifted +1, rounded to 2dp.</summary>
    public double? NumericDifficulty;
    public string? NumericDifficultyHint;
    public string DanLabel = "";
    public string Variant = "";
    /// <summary>1.0 at a tier centre, falling to 0 at the boundary between tiers.</summary>
    public double Confidence;
    public double RawModelOutput;
}

public static class CompanellaEstimator
{
    private static readonly string[] DAN_LABELS =
    {
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
        "alpha", "beta", "gamma", "delta", "epsilon",
        "Emik Zeta", "Thaumiel Eta", "CloverWisp Theta", "iota", "kappa",
    };

    private const int FEATURE_COUNT = 10;
    private const double MIN_DAN = 1.0;
    private const double MAX_DAN = 20.0;

    private static readonly Dictionary<string, string> VARIANT_TEXT = new()
    {
        ["--"] = "low",
        ["-"] = "mid/low",
        [""] = "mid",
        ["+"] = "mid/high",
        ["++"] = "high",
    };

    private static readonly Regex DigitsOnly = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly object _sessionLock = new();
    private static InferenceSession? _session;

    private static InferenceSession Session()
    {
        var s = _session;
        if (s != null) return s;
        lock (_sessionLock)
        {
            return _session ??= new InferenceSession(ResolveModelPath());
        }
    }

    // The model ships loose beside this assembly (like MinaCalc.dll). When the Hook
    // is injected into osu!, AppContext.BaseDirectory is osu!'s install dir, not
    // ours — so resolve from the assembly location first, mirroring MinaCalcNative.
    private static string ResolveModelPath()
    {
        const string fileName = "dan_model.onnx";
        var dirs = new[]
        {
            Path.GetDirectoryName(typeof(CompanellaEstimator).Assembly.Location),
            AppContext.BaseDirectory,
        };
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var candidate in new[] { Path.Combine(dir, fileName), Path.Combine(dir, "Assets", fileName) })
                if (File.Exists(candidate)) return candidate;
        }
        // Nothing found — hand the bare name to InferenceSession so its own
        // FileNotFoundException names the model, and Companella.cs turns it into null.
        return fileName;
    }

    private static double Clamp(double value, double min, double max)
        => Math.Max(min, Math.Min(max, value));

    private readonly record struct Prediction(int DanIndex, string Variant);

    private static Prediction ParsePrediction(double rawValue)
    {
        if (rawValue < MIN_DAN) return new Prediction(0, "--");
        if (rawValue >= MAX_DAN) return new Prediction(19, "++");

        double danLevel = Clamp(Math.Floor(rawValue + 0.5), 1, 20); // Math.round
        int danIndex = (int)danLevel - 1;
        double offset = rawValue - danLevel;

        string variant;
        if (offset <= -0.3) variant = "--";
        else if (offset <= -0.1) variant = "-";
        else if (offset < 0.1) variant = "";
        else if (offset < 0.3) variant = "+";
        else variant = "++";

        return new Prediction(danIndex, variant);
    }

    private static string CapitalizeLabel(string? label)
    {
        string text = (label ?? "?").Trim();
        if (text.Length == 0) return "?";
        if (DigitsOnly.IsMatch(text)) return text;

        var parts = Whitespace.Split(text).Where(p => p.Length > 0)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant());
        return string.Join(" ", parts);
    }

    private static (double Overall, double Stream, double Jumpstream, double Handstream,
        double Stamina, double Jackspeed, double Chordjack, double Technical)
        NormalizeMsdInput(IReadOnlyDictionary<string, double>? msdValues)
    {
        var input = msdValues ?? new Dictionary<string, double>();
        double Get(string k) => input.TryGetValue(k, out var v) ? v : double.NaN;
        return (Get("overall"), Get("stream"), Get("jumpstream"), Get("handstream"),
            Get("stamina"), Get("jackspeed"), Get("chordjack"), Get("technical"));
    }

    private static string BuildDisplayDifficulty(string label, string variant)
    {
        string variantText = VARIANT_TEXT.GetValueOrDefault(variant, VARIANT_TEXT[""]);
        string cappedLabel = CapitalizeLabel(label);

        if (DigitsOnly.IsMatch(cappedLabel)) return $"Reform {cappedLabel} {variantText}";
        return $"{cappedLabel} {variantText}";
    }

    /// <param name="msdValues">The lowercase-keyed skillset dictionary from Msd.ComputeMsd.</param>
    public static CompanellaEstimate ClassifyCompanellaDifficulty(
        IReadOnlyDictionary<string, double>? msdValues,
        double interludeStar,
        double sunnyStar)
    {
        var n = NormalizeMsdInput(msdValues);
        double interlude = interludeStar;
        double sunny = sunnyStar;

        var features = new[]
        {
            n.Overall, n.Stream, n.Jumpstream, n.Handstream,
            n.Stamina, n.Jackspeed, n.Chordjack, n.Technical,
            interlude, sunny,
        };

        bool hasInvalidFeature = features.Any(value => !double.IsFinite(value));
        if (hasInvalidFeature || features.Length != FEATURE_COUNT)
            throw new InvalidOperationException(
                "Companella requires valid MSD, InterludeSR, and Sunny SR values");

        var session = Session();

        string inputName = session.InputNames.Count > 0 ? session.InputNames[0] : "X";

        var floats = new float[FEATURE_COUNT];
        for (int i = 0; i < FEATURE_COUNT; i++) floats[i] = (float)features[i];
        var inputTensor = new DenseTensor<float>(floats, new[] { 1, FEATURE_COUNT });

        using var outputs = session.Run(
            new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) });

        string outputName = session.OutputNames.Count > 0 ? session.OutputNames[0] : "variable";
        var outputValue = outputs.FirstOrDefault(o => o.Name == outputName) ?? outputs.First();
        double rawModelValue = ExtractFirstNumericValue(outputValue);

        if (!double.IsFinite(rawModelValue))
            throw new InvalidOperationException("Companella model output is invalid");

        // Keep parity with Companella C# pipeline:
        // clamp -> +1 shift -> parse tier/variant.
        double shiftedRawValue = Clamp(rawModelValue, MIN_DAN, MAX_DAN) + 1;
        var (danIndex, variant) = ParsePrediction(shiftedRawValue);
        string label = danIndex >= 0 && danIndex < DAN_LABELS.Length ? DAN_LABELS[danIndex] : "?";

        double roundedRaw = Math.Round(shiftedRawValue, 2, MidpointRounding.AwayFromZero);
        double roundedCenter = Math.Floor(shiftedRawValue + 0.5); // Math.round
        double confidence = Math.Max(0, 1.0 - Math.Abs(shiftedRawValue - roundedCenter) * 2.0);

        return new CompanellaEstimate
        {
            EstDiff = BuildDisplayDifficulty(label, variant),
            NumericDifficulty = roundedRaw,
            NumericDifficultyHint = null,
            DanLabel = label,
            Variant = variant,
            Confidence = confidence,
            RawModelOutput = roundedRaw,
        };
    }

    private static double ExtractFirstNumericValue(DisposableNamedOnnxValue value)
    {
        try
        {
            var t = value.AsTensor<float>();
            foreach (var x in t) return x;
            return double.NaN;
        }
        catch
        {
            try
            {
                var td = value.AsTensor<double>();
                foreach (var x in td) return x;
            }
            catch { /* fall through */ }
            return double.NaN;
        }
    }
}
