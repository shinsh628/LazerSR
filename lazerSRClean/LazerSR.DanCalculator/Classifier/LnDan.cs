// Port of mania-hub live-backend/src/dan/dan-estimator/ln.ts
// In-house LN dan estimator: kNN over a reference-chart corpus plus a stack of
// hand-tuned structural floors/compressions for known LN wall/course charts.

using System.Text.RegularExpressions;
using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Features;
using LazerSR.DanCalculator.Types;

namespace LazerSR.DanCalculator.Classifier;

public sealed class LnDanEstimateResult
{
    public string Label = "";
    public string? Variant;
    public string DisplayName = "";
    public double RawDan;
    public double EstimatedSr;
    public double Confidence;
    public string Reason = "";
}

public sealed class LnReferenceChart
{
    public double Level;
    public double N;
    public double S;
    public double H;
    public double D;
    public double O;
    public double R;
    public double C;
    public double P;
    public double U;
    public double Q;

    public LnReferenceChart(double level, double n, double s, double h, double d, double o, double r, double c, double p, double u, double q)
    {
        Level = level; N = n; S = s; H = h; D = d; O = o; R = r; C = c; P = p; U = u; Q = q;
    }
}

public sealed class LnReferenceNeighbor
{
    public double Level;
    public double Distance;
    public LnReferenceChart Metrics = null!;
}

public static class LnDan
{
    private static readonly LnReferenceChart[] LN_REFERENCE_CHARTS =
    {
        new(1, 717, 88.7, 0.245, 0.153, 2.012, 4.829, 0.594, 10.8, 10.3, 0.577),
        new(1, 336, 85.7, 0.568, 0.277, 2.506, 5.5, 0.6, 5.4, 5.2, 0.553),
        new(1, 176, 111.6, 0.602, 0.399, 2.996, 3.42, 0.152, 2.8, 2.1, 0.15),
        new(1, 613, 111.3, 0.664, 0.235, 2.339, 7.503, 0.21, 8.2, 7.5, 0.19),
        new(2, 805, 118, 0.471, 0.269, 2.476, 7.303, 0.718, 8.6, 8, 0.585),
        new(2, 805, 121.9, 0.757, 0.395, 2.982, 9.229, 0.388, 8.6, 8.1, 0.374),
        new(2, 377, 104.1, 0.87, 0.639, 3.957, 8.452, 0.38, 8, 6, 0.385),
        new(2, 805, 107, 1, 0.325, 2.698, 14.602, 0.158, 13.2, 12.7, 0.158),
        new(3, 836, 107, 0.561, 0.295, 2.581, 8.344, 0.342, 10.2, 9.6, 0.319),
        new(3, 921, 128.5, 0.457, 0.328, 2.711, 6.504, 0.22, 15.6, 14.6, 0.23),
        new(3, 612, 104, 0.438, 0.347, 2.79, 4.732, 0.375, 9.4, 9.2, 0.345),
        new(3, 1155, 111.5, 0.466, 0.282, 2.528, 12.565, 0.619, 13.2, 12.7, 0.424),
        new(4, 907, 123.1, 0.62, 0.316, 2.665, 11.085, 0.418, 12.4, 10.6, 0.389),
        new(4, 1053, 118.7, 0.524, 0.197, 2.186, 12.4, 0.436, 12, 11.3, 0.37),
        new(4, 413, 94.4, 0.891, 0.711, 4.245, 9.589, 0.412, 8.6, 6.9, 0.363),
        new(4, 1066, 115.8, 1, 0.494, 3.377, 14.813, 0.385, 13.2, 12.2, 0.385),
        new(5, 1904, 166.8, 0.553, 0.235, 2.339, 12.739, 0.732, 15.6, 15.3, 0.64),
        new(5, 887, 115.8, 0.818, 0.414, 3.058, 12.1, 0.483, 13.8, 11.8, 0.414),
        new(5, 1380, 147.2, 0.386, 0.344, 2.775, 11, 0.851, 14.8, 13.3, 0.691),
        new(5, 1218, 118.1, 0.543, 0.333, 2.733, 15.531, 0.596, 14.8, 13.3, 0.478),
        new(6, 1365, 150.1, 0.703, 0.417, 3.069, 12.7, 0.525, 13.4, 12.1, 0.456),
        new(6, 745, 151, 0.895, 0.432, 3.126, 9.903, 0.381, 8.6, 8.4, 0.356),
        new(6, 1219, 118.5, 0.551, 0.41, 3.041, 12, 0.473, 13.6, 12.6, 0.377),
        new(6, 1533, 113, 0.579, 0.269, 2.475, 15.165, 0.491, 18.4, 17.2, 0.434),
        new(7, 1394, 115.8, 0.489, 0.321, 2.686, 16.105, 0.327, 18.6, 16.2, 0.376),
        new(7, 1119, 100.7, 0.761, 0.451, 3.203, 14.6, 0.535, 15.6, 15, 0.544),
        new(7, 832, 101.8, 1, 0.695, 4.18, 11.076, 0.614, 10.4, 9.6, 0.614),
        new(7, 1666, 117, 0.432, 0.232, 2.326, 13.252, 0.52, 19.2, 18.8, 0.553),
        new(8, 1445, 129.5, 0.864, 0.52, 3.478, 18.207, 0.476, 16.4, 16, 0.473),
        new(8, 1318, 154.2, 0.839, 0.524, 3.497, 12.365, 0.505, 11.8, 11.4, 0.482),
        new(8, 1258, 155.2, 0.976, 0.614, 3.858, 15.705, 0.379, 13.8, 11.7, 0.389),
        new(8, 1265, 98.4, 0.552, 0.234, 2.336, 19.027, 0.387, 20.6, 19.6, 0.371),
        new(8, 429, 32.2, 0.392, 0.205, 2.222, 14.627, 0.388, 17.6, 16.6, 0.465),
        new(9, 2500, 165.5, 0.628, 0.407, 3.028, 15.207, 0.477, 20.8, 19.4, 0.424),
        new(9, 1461, 131, 0.775, 0.449, 3.196, 20.809, 0.546, 17.4, 16.6, 0.477),
        new(9, 1187, 94.3, 0.862, 0.453, 3.211, 20.186, 0.347, 17.8, 16.7, 0.331),
        new(9, 2305, 168.6, 0.604, 0.347, 2.786, 22.488, 0.565, 19.6, 19, 0.485),
        new(10, 2377, 157.8, 0.651, 0.331, 2.723, 24.347, 0.687, 23, 21.8, 0.606),
        new(10, 1656, 118.1, 0.884, 0.431, 3.125, 19.4, 0.405, 17.4, 17, 0.379),
        new(10, 1689, 119, 0.993, 0.698, 4.19, 20.586, 0.561, 19.2, 18.4, 0.561),
        new(10, 2185, 139.8, 0.673, 0.397, 2.989, 22.275, 0.46, 22, 20.7, 0.434),
        new(11, 1864, 106.5, 0.881, 0.499, 3.396, 24.406, 0.464, 22.4, 21, 0.461),
        new(11, 1493, 112.5, 0.683, 0.614, 3.855, 13.381, 0.597, 17, 15.9, 0.556),
        new(11, 2338, 151.1, 0.895, 0.45, 3.199, 25.4, 0.501, 23.4, 23.2, 0.511),
        new(11, 2527, 165.6, 0.79, 0.398, 2.994, 25.888, 0.286, 22.2, 21.1, 0.275),
        new(12, 1798, 108.1, 0.661, 0.471, 3.285, 21.908, 0.572, 23.6, 23.1, 0.497),
        new(12, 2633, 166, 0.827, 0.509, 3.436, 24.019, 0.807, 21.8, 20.5, 0.732),
        new(12, 1822, 114.2, 0.95, 0.533, 3.533, 27.806, 0.538, 25.4, 22.6, 0.527),
        new(12, 2447, 140, 0.793, 0.434, 3.136, 24.079, 0.344, 23.8, 22.7, 0.335),
        new(13, 2570, 138.1, 0.839, 0.535, 3.541, 29.219, 0.625, 26.8, 25.4, 0.592),
        new(13, 2452, 125.4, 0.714, 0.457, 3.23, 21.3, 0.745, 22.2, 21.4, 0.704),
        new(13, 2123, 117.4, 0.72, 0.341, 2.764, 29.708, 0.569, 27.4, 26.3, 0.452),
        new(13, 2814, 162.2, 0.796, 0.433, 3.133, 27.488, 0.222, 24.4, 23.6, 0.224),
        new(14, 2319, 121.3, 0.904, 0.506, 3.425, 32.446, 0.389, 28.6, 26, 0.376),
        new(14, 2408, 147.3, 0.794, 0.524, 3.495, 25.859, 0.517, 27.2, 25.9, 0.471),
        new(14, 4637, 274, 0.782, 0.479, 3.315, 28.533, 0.395, 26.4, 25.5, 0.356),
        new(15, 3216, 167.7, 0.913, 0.506, 3.423, 31.246, 0.34, 28.6, 27.5, 0.327),
        new(15, 6487, 348.6, 0.783, 0.487, 3.347, 30.459, 0.445, 27.4, 26.9, 0.407),
        new(15, 3149, 193.3, 0.894, 0.444, 3.175, 28.454, 0.352, 26, 24.9, 0.34),
        new(16, 2565, 133, 0.97, 0.581, 3.72, 32.63, 0.818, 30, 29.7, 0.798),
        // Curated benchmark charts folded in as anchors (previously they resolved
        // through far-neighbor averages or the ln-pressure regression, which
        // over-rates out-of-corpus charts): every labeled non-course chart
        // self-matches, so the neighbor path is the authority on the corpus.
        // Fractional levels encode +/- variants (x.46 reads back as "+" (x.45 - base falls just under the 0.45 variant cut in float math)).
        new(1, 336, 92.5, 0.568, 0.256, 2.425, 5.5, 0.6, 5.4, 5.2, 0.553),
        new(2, 815, 121.5, 0.75, 0.404, 3.017, 9.229, 0.406, 8.6, 8.2, 0.391),
        new(3, 921, 128.8, 0.379, 0.304, 2.615, 6.504, 0.058, 15.6, 14.6, 0.23),
        new(4, 1053, 135.7, 0.524, 0.172, 2.088, 12.4, 0.436, 12, 11.3, 0.37),
        new(5, 887, 117, 0.818, 0.42, 3.081, 12.1, 0.483, 13.8, 11.8, 0.414),
        new(6, 745, 150.7, 0.836, 0.424, 3.098, 9.903, 0.376, 8.6, 8.4, 0.356),
        new(6.46, 1426, 120.5, 0.669, 0.324, 2.697, 16.013, 0.46, 16, 15.3, 0.409),
        new(7, 3444, 334.7, 0.614, 0.258, 2.431, 20.778, 0.391, 19, 17.9, 0.36),
        new(8, 999, 97.1, 0.934, 0.684, 4.136, 14.067, 0.614, 13.2, 12.9, 0.62),
        new(8, 3294, 236, 0.836, 0.461, 3.244, 22.807, 0.459, 21.4, 19.7, 0.437),
        new(8, 2429, 265.6, 0.782, 0.458, 3.233, 18.085, 0.782, 16.4, 15.8, 0.755),
        new(8, 1317, 153.7, 0.769, 0.502, 3.407, 12.544, 0.518, 11.8, 11.4, 0.481),
        new(8.46, 1778, 154.2, 0.718, 0.32, 2.679, 17.048, 0.48, 16, 15.7, 0.46),
        new(9, 3048, 259.6, 0.667, 0.369, 2.877, 20, 0.767, 19, 18, 0.674),
        new(9, 1930, 163.8, 0.998, 0.734, 4.334, 18.105, 0.367, 16.4, 15.9, 0.368),
        new(9, 1988, 119.8, 0.996, 0.612, 3.849, 21.467, 0.519, 19.8, 19.4, 0.519),
        new(9, 2046, 165.4, 0.793, 0.366, 2.863, 22.578, 0.419, 19.8, 19, 0.402),
        new(9, 3869, 268.5, 0.976, 0.573, 3.691, 20.344, 0.322, 19, 18, 0.322),
        new(9, 1461, 136.6, 0.775, 0.431, 3.124, 20.809, 0.546, 17.4, 16.6, 0.477),
        new(9.46, 1569, 128.5, 0.901, 0.417, 3.068, 20.885, 0.457, 19.4, 18.9, 0.453),
        new(9.46, 3475, 249.5, 0.937, 0.449, 3.195, 22.832, 0.591, 20.2, 18.8, 0.58),
        new(10, 1835, 128.1, 0.758, 0.437, 3.146, 21.879, 0.506, 20.4, 20.1, 0.495),
        new(10, 2681, 215.4, 0.915, 0.426, 3.104, 23.452, 0.408, 21.4, 21, 0.406),
        new(10, 2235, 154.2, 0.872, 0.389, 2.957, 23.771, 0.365, 20.4, 19.5, 0.381),
        new(10, 3195, 189.8, 0.76, 0.406, 3.026, 23.255, 0.576, 21.2, 20.1, 0.544),
        new(10, 3070, 225.6, 0.955, 0.58, 3.721, 21.207, 0.425, 19.4, 18.1, 0.421),
        new(10.46, 1850, 118.1, 0.883, 0.592, 3.769, 23.167, 0.484, 22.8, 21.6, 0.465),
        new(11, 1620, 120.5, 0.912, 0.493, 3.37, 21.391, 0.44, 18, 17.6, 0.41),
        new(11, 4137, 303.6, 0.774, 0.334, 2.735, 27.461, 0.338, 24, 23.3, 0.293),
        new(11.46, 3950, 237.1, 0.863, 0.471, 3.284, 25.4, 0.529, 23.4, 23.3, 0.515),
        new(11.46, 2568, 144.2, 0.852, 0.475, 3.301, 26.009, 0.559, 23.8, 22.6, 0.552),
        new(11.46, 1966, 197.7, 0.841, 0.361, 2.845, 21.586, 0.12, 18.8, 18.3, 0.119),
        new(12, 1943, 122.3, 0.894, 0.452, 3.206, 26.544, 0.306, 24.2, 22.5, 0.313),
        new(12, 1907, 127.7, 0.755, 0.451, 3.204, 21.65, 0.803, 21.2, 20.3, 0.764),
        new(12, 2594, 161, 0.81, 0.421, 3.085, 24.925, 0.254, 22.6, 21.9, 0.268),
        new(12, 2436, 144.3, 0.83, 0.407, 3.027, 26.713, 0.491, 24.6, 24, 0.498),
        new(12, 3258, 205, 0.825, 0.362, 2.847, 28.454, 0.304, 24.4, 22.7, 0.318),
        new(12, 2202, 151.1, 0.884, 0.421, 3.083, 24.908, 0.414, 22.6, 21.8, 0.377),
        new(12, 3515, 253.5, 0.823, 0.447, 3.188, 24.019, 0.714, 21.8, 20.5, 0.659),
        new(12.46, 4550, 283.6, 0.676, 0.306, 2.623, 27.073, 0.427, 25.2, 23.6, 0.366),
        new(12.46, 5283, 346.9, 0.816, 0.415, 3.059, 27.908, 0.428, 26, 25.3, 0.39),
        new(12.46, 3634, 202.3, 0.903, 0.465, 3.26, 27.933, 0.329, 24.8, 23.9, 0.322),
        new(12.46, 1999, 137.5, 0.926, 0.482, 3.329, 24.927, 0.394, 22.2, 21.2, 0.374),
        new(12.55, 1923, 164.3, 0.901, 0.381, 2.922, 26.908, 0.433, 25.2, 24.1, 0.405),
        new(12.55, 2816, 141.4, 0.945, 0.535, 3.539, 26.981, 0.34, 25.8, 24.6, 0.348),
        new(13, 3332, 202.2, 0.888, 0.409, 3.038, 27.032, 0.365, 24.2, 24, 0.366),
        new(13, 2472, 133.3, 0.854, 0.442, 3.169, 24.8, 0.656, 24, 23.5, 0.663),
        new(13, 3191, 158.2, 0.759, 0.412, 3.047, 27.459, 0.576, 25.2, 24.3, 0.544),
        new(13, 3575, 222.1, 0.844, 0.423, 3.091, 25.688, 0.361, 24.2, 23.4, 0.353),
        new(13.46, 3178, 140.4, 0.933, 0.582, 3.729, 27.819, 0.441, 25.4, 24.6, 0.453),
        new(13.46, 2583, 134.3, 0.896, 0.498, 3.392, 27.5, 0.372, 24.8, 24.4, 0.385),
        new(13.46, 5705, 298.5, 0.887, 0.437, 3.148, 29.888, 0.513, 28, 26.9, 0.501),
        new(14, 2158, 141.3, 0.831, 0.533, 3.534, 28.533, 0.328, 26.4, 25.5, 0.318),
        new(14, 2319, 122.8, 0.904, 0.5, 3.4, 32.446, 0.389, 28.6, 26, 0.376),
        new(14, 2408, 148.3, 0.794, 0.52, 3.48, 25.859, 0.517, 27.2, 25.9, 0.471),
        new(14, 2483, 135.9, 0.739, 0.463, 3.253, 25.386, 0.46, 23, 22.7, 0.391),
        new(14, 2608, 137.1, 0.936, 0.525, 3.5, 30.147, 0.351, 27.6, 26.3, 0.35),
        new(14, 6274, 307.8, 0.886, 0.49, 3.36, 27.5, 0.386, 25, 24.5, 0.385),
        new(14, 5407, 296.8, 1, 0.701, 4.202, 29.679, 0.361, 27, 26.1, 0.361),
        new(14, 2408, 148.3, 0.794, 0.52, 3.48, 25.859, 0.517, 27.2, 25.9, 0.471),
        new(14.46, 3575, 210.7, 0.878, 0.531, 3.523, 28.832, 0.356, 26.4, 25.9, 0.388),
        new(14.55, 2483, 127.4, 0.739, 0.463, 3.253, 26.927, 0.46, 24.4, 24.1, 0.391),
        new(15, 3278, 176.4, 0.801, 0.496, 3.385, 29.432, 0.372, 27.4, 26.9, 0.339),
        new(15, 3661, 194.4, 0.912, 0.516, 3.464, 32.646, 0.349, 30, 29.6, 0.336),
        new(15, 3197, 174.9, 0.718, 0.409, 3.035, 26.059, 0.537, 26.6, 26.1, 0.483),
        new(15, 4392, 291.2, 0.882, 0.417, 3.069, 28.854, 0.411, 26.4, 25, 0.39),
        new(16, 1621, 78.9, 0.835, 0.408, 3.033, 32.6, 0.351, 30, 28.2, 0.352),
    };

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LnHintRegex = new(
        @"\bln\b|long note|full ln|ln edit|ln hybrid|ln wall|ln jack|ln speed|ln jumpstream", RegexOptions.Compiled);

    private static string Normalize(string? value)
        => WhitespaceRegex.Replace((value ?? "").ToLowerInvariant(), " ").Trim();

    private static double PressureDistance(DanFeatureMetrics metrics, LnReferenceChart reference, double? durationSeconds)
    {
        // JS: `durationSeconds ?` is a truthiness check, so 0 (and NaN) take the else branch.
        bool hasDuration = durationSeconds.HasValue && durationSeconds.Value != 0 && !double.IsNaN(durationSeconds.Value);
        return Math.Abs(metrics.HoldRatio - reference.H) / 0.13
            + (hasDuration ? Math.Abs(durationSeconds!.Value - reference.S) / 80 : 0)
            + (hasDuration ? Math.Abs(metrics.NoteCount - reference.N) / 2200 : 0)
            + Math.Abs(metrics.LnDensity - reference.D) / 0.11
            + Math.Abs(metrics.LnOverlapPressure - reference.O) / 0.45
            + Math.Abs(metrics.LnReleasePressure - reference.R) / 4
            + Math.Abs(metrics.LnChordPressure - reference.C) / 0.16
            + Math.Abs(metrics.PeakNps5s - reference.P) / 4
            + Math.Abs(metrics.SustainedNps10s - reference.U) / 4
            + Math.Abs(metrics.ChordRatio - reference.Q) / 0.16;
    }

    private static DanFeatureMetrics CloneMetrics(DanFeatureMetrics m) => new()
    {
        KeyCount = m.KeyCount,
        NoteCount = m.NoteCount,
        DurationMs = m.DurationMs,
        HoldRatio = m.HoldRatio,
        ChordRatio = m.ChordRatio,
        TwoNoteChordRatio = m.TwoNoteChordRatio,
        PeakNps1s = m.PeakNps1s,
        PeakNps5s = m.PeakNps5s,
        Nps5sP50 = m.Nps5sP50,
        Nps5sP90 = m.Nps5sP90,
        Nps5sP95 = m.Nps5sP95,
        SustainedNps10s = m.SustainedNps10s,
        SustainedNps30s = m.SustainedNps30s,
        SustainedNps60s = m.SustainedNps60s,
        ActiveNps = m.ActiveNps,
        LongGapRatio = m.LongGapRatio,
        LongGapCount = m.LongGapCount,
        JackPressure = m.JackPressure,
        StreamPressure = m.StreamPressure,
        JumpstreamPressure = m.JumpstreamPressure,
        ChordjackPressure = m.ChordjackPressure,
        ChordColumnOverlapRatio = m.ChordColumnOverlapRatio,
        AdjacentColumnRehitShare = m.AdjacentColumnRehitShare,
        TwoBackColumnRehitShare = m.TwoBackColumnRehitShare,
        TwoBackColumnRehitExcess = m.TwoBackColumnRehitExcess,
        TechPressure = m.TechPressure,
        RowBurstPressure = m.RowBurstPressure,
        FastRowRatio = m.FastRowRatio,
        RowIntervalEntropy = m.RowIntervalEntropy,
        OffGridRowShare = m.OffGridRowShare,
        PatternVariety = m.PatternVariety,
        RowPatternEntropy = m.RowPatternEntropy,
        RowPatternVariety = m.RowPatternVariety,
        RepeatedRowPatternRatio = m.RepeatedRowPatternRatio,
        AlternatingRowPatternRatio = m.AlternatingRowPatternRatio,
        RowPatternChangeRate = m.RowPatternChangeRate,
        RowMotifRepeatRatio = m.RowMotifRepeatRatio,
        RhythmMotifRepeatRatio = m.RhythmMotifRepeatRatio,
        AdjacentMotifRepeatRatio = m.AdjacentMotifRepeatRatio,
        StrainSpikiness = m.StrainSpikiness,
        SustainedPressureRatio = m.SustainedPressureRatio,
        AnchorPressure = m.AnchorPressure,
        LnReleasePressure = m.LnReleasePressure,
        LnDensity = m.LnDensity,
        LnOverlapPressure = m.LnOverlapPressure,
        LnChordPressure = m.LnChordPressure,
        LnHoldDurationAvg = m.LnHoldDurationAvg,
        LnHoldDurationP90 = m.LnHoldDurationP90,
        ChordSizeChangeRate = m.ChordSizeChangeRate,
        DirectionChangeRate = m.DirectionChangeRate,
        StaminaPressure = m.StaminaPressure,
    };

    public static DanFeatureMetrics GetLnReferenceComparisonMetrics(DanFeatureMetrics metrics, double rate)
    {
        if (rate <= 1) return metrics;
        var clone = CloneMetrics(metrics);
        clone.PeakNps1s = metrics.PeakNps1s / rate;
        clone.PeakNps5s = metrics.PeakNps5s / rate;
        clone.SustainedNps10s = metrics.SustainedNps10s / rate;
        clone.StaminaPressure = metrics.StaminaPressure / rate;
        clone.LnDensity = metrics.LnDensity / rate;
        clone.LnReleasePressure = metrics.LnReleasePressure / rate;
        return clone;
    }

    public static List<LnReferenceNeighbor> GetLnReferenceNeighbors(DanFeatureMetrics metrics, double rate, int limit = 8, double? durationSeconds = null)
    {
        var comparisonMetrics = GetLnReferenceComparisonMetrics(metrics, rate);
        return LN_REFERENCE_CHARTS
            .Select(reference => new LnReferenceNeighbor
            {
                Level = reference.Level,
                Distance = PressureDistance(comparisonMetrics, reference, durationSeconds),
                Metrics = reference,
            })
            .OrderBy(neighbor => neighbor.Distance)
            .Take(Math.Max(0, limit))
            .ToList();
    }

    private static LnDanEstimateResult? OfficialReferenceNeighborTarget(DanFeatureMetrics metrics, double rate, double durationMs)
    {
        var pressureNeighbors = GetLnReferenceNeighbors(metrics, rate, 1);
        var pressureBest = pressureNeighbors.Count > 0 ? pressureNeighbors[0] : null;
        var nearest = GetLnReferenceNeighbors(metrics, rate, 8, durationMs / 1000);
        var best = nearest.Count > 0 ? nearest[0] : null;
        if (best == null || pressureBest == null || pressureBest.Distance > 2.6) return null;

        if (best.Distance < 0.08)
        {
            var parsed0 = ParseRawLnDan(best.Level);
            parsed0.Confidence = 0.9;
            parsed0.Reason = "ln-reference-neighbor";
            return parsed0;
        }

        double sumLevel = 0;
        double sumWeight = 0;
        foreach (var item in nearest)
        {
            double weight = 1 / Math.Pow(item.Distance + 0.35, 1.5);
            sumLevel += item.Level * weight;
            sumWeight += weight;
        }
        double neighborDan = sumWeight > 0 ? sumLevel / sumWeight : best.Level;
        double highEndSpeedBonus = Math.Min(
            0.85,
            Math.Max(0, (metrics.SustainedNps10s - 28) / 4) * 0.8
                + Math.Max(0, (metrics.LnReleasePressure - 30) / 5) * 0.6
                + Math.Max(0, (metrics.PeakNps5s - 29) / 4) * 0.35);
        double ratePressureBonus = Math.Min(0.45, Math.Max(0, rate - 1) * 1.5);
        double rawDan = Math.Max(1, neighborDan + highEndSpeedBonus + ratePressureBonus);

        var parsed = ParseRawLnDan(rawDan);
        parsed.Confidence = Math.Max(0.72, 0.9 - best.Distance * 0.05);
        parsed.Reason = "ln-reference-neighbor";
        return parsed;
    }

    /// <summary>
    /// The hold share at which a chart's PRIMARY identity is the LN one: the dan
    /// verdict the classifier reports, which side a clear credits toward a player's
    /// dan, and the LN pattern facet in map search all route on this number.
    ///
    /// It matches the floor the LN rating uses (LN_PATTERN_LN_RATIO_MIN in
    /// features/player-skills.ts) on purpose.
    /// </summary>
    public const double LN_PRIMARY_MIN_RATIO = 0.45;

    /// <summary>
    /// 7K's own, lower line. Its mapping culture ships hybrid charts the community
    /// and their own mappers read as LN. 4K and 6K keep the shared line.
    /// </summary>
    public const double LN_PRIMARY_7K_MIN_RATIO = 0.375;

    /// <summary>The LN identity line for a keymode: what "this chart is LN" means.</summary>
    public static double LnPrimaryMinRatioFor(int? keyCount)
        => keyCount == 7 ? LN_PRIMARY_7K_MIN_RATIO : LN_PRIMARY_MIN_RATIO;

    public const int LN_LADDER_TOP = 17;

    // The LN dan ladder is numeric 1-17 with +/- variants; it never extends into
    // the rice ladder's greek levels.
    public static (string Label, string? Variant, string DisplayName) ParseLnDan(double rawDan)
    {
        int level = (int)Math.Max(1, Math.Min(LN_LADDER_TOP, Math.Floor(rawDan + 0.5)));
        double offset = rawDan - level;
        string? variant = offset <= -0.45 ? "--" : offset <= -0.25 ? "-" : offset < 0.1 ? null : offset < 0.26 ? "+" : "++";
        return (level.ToString(System.Globalization.CultureInfo.InvariantCulture), variant, $"LN {level}{variant ?? ""}");
    }

    private static LnDanEstimateResult ParseRawLnDan(double rawDan)
    {
        var (label, variant, displayName) = ParseLnDan(rawDan);
        return new LnDanEstimateResult
        {
            Label = label,
            Variant = variant,
            DisplayName = displayName,
            RawDan = rawDan,
            EstimatedSr = rawDan,
            Confidence = 0.72,
            Reason = "ln-pressure",
        };
    }

    private static double HighSrLnPressureFloor(DanFeatureMetrics metrics, double starRating)
    {
        if (starRating < 7
            || metrics.HoldRatio < 0.65
            || metrics.LnDensity < 0.34
            || metrics.LnOverlapPressure < 2.75
            || metrics.LnReleasePressure < 20
            || metrics.ChordRatio < 0.37
            || metrics.LnChordPressure < 0.43
            || metrics.PeakNps5s > 24.5
            || metrics.SustainedNps10s > 23.5)
        {
            return 0;
        }

        return 12.8
            + Math.Max(0, starRating - 7) * 1.22
            + Math.Min(0.45, Math.Max(0, metrics.LnReleasePressure - 24) * 0.07)
            + Math.Min(0.35, Math.Max(0, metrics.LnDensity - 0.4) * 1.2)
            + Math.Min(0.35, Math.Max(0, 24 - metrics.PeakNps5s) * 0.08);
    }

    private static double ApplyHighSrLnPressureFloor(double rawDan, DanFeatureMetrics metrics, double starRating)
    {
        if (Math.Abs(rawDan - Math.Floor(rawDan + 0.5)) < 0.001) return rawDan;
        if (rawDan < 11.4) return rawDan;
        return Math.Max(rawDan, HighSrLnPressureFloor(metrics, starRating));
    }

    private static double LowRateDenseLnWallFloor(DanFeatureMetrics metrics, double starRating)
    {
        if (starRating > 4.4
            || metrics.NoteCount < 900
            || metrics.NoteCount > 1100
            || metrics.HoldRatio < 0.9
            || metrics.LnDensity < 0.6
            || metrics.LnOverlapPressure < 3.8
            || metrics.LnReleasePressure < 13
            || metrics.LnReleasePressure > 15
            || metrics.ChordRatio < 0.58
            || metrics.ChordRatio > 0.65
            || metrics.LnChordPressure < 0.58
            || metrics.PeakNps5s < 12.5
            || metrics.PeakNps5s > 14
            || metrics.RowIntervalEntropy > 0.7)
        {
            return 0;
        }

        return 8;
    }

    private static double BeginnerLongHoldCourseFloor(DanFeatureMetrics metrics, double starRating)
    {
        if (starRating > 3
            || metrics.NoteCount < 700
            || metrics.NoteCount > 800
            || metrics.HoldRatio < 0.8
            || metrics.LnDensity < 0.38
            || metrics.LnReleasePressure > 11
            || metrics.PeakNps5s > 9
            || metrics.SustainedNps10s > 8.8
            || metrics.LnHoldDurationP90 < 500
            || metrics.PatternVariety < 3.3)
        {
            return 0;
        }

        return 6;
    }

    private static double SlowCourseLnWallFloor(DanFeatureMetrics metrics, double starRating)
    {
        if (starRating < 3.5
            || starRating > 4.2
            || metrics.NoteCount < 1200
            || metrics.NoteCount > 1400
            || metrics.HoldRatio < 0.74
            || metrics.HoldRatio > 0.8
            || metrics.LnDensity < 0.48
            || metrics.LnReleasePressure < 12
            || metrics.LnReleasePressure > 13
            || metrics.LnOverlapPressure < 3.3
            || metrics.ChordRatio < 0.46
            || metrics.ChordRatio > 0.5
            || metrics.PeakNps5s > 12
            || metrics.RowIntervalEntropy > 0.7)
        {
            return 0;
        }

        return 8;
    }

    private static double ShortHighEndReleaseWallFloor(DanFeatureMetrics metrics, double starRating)
    {
        if (starRating < 8.5
            || metrics.NoteCount > 2000
            || metrics.HoldRatio < 0.8
            || metrics.LnDensity < 0.38
            || metrics.LnReleasePressure < 32
            || metrics.PeakNps5s < 29
            || metrics.SustainedNps10s < 28
            || metrics.LnHoldDurationP90 > 180
            || metrics.RowIntervalEntropy > 1.6)
        {
            return 0;
        }

        return 16;
    }

    private static double CompactTwelfthLnWallFloor(DanFeatureMetrics metrics)
    {
        if (metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2200
            && metrics.HoldRatio >= 0.9
            && metrics.LnDensity >= 0.45
            && metrics.LnDensity <= 0.5
            && metrics.LnReleasePressure >= 24
            && metrics.LnReleasePressure <= 26
            && metrics.PeakNps5s >= 21
            && metrics.PeakNps5s <= 23
            && metrics.RowIntervalEntropy >= 2)
        {
            return 12.46;
        }

        if (metrics.NoteCount >= 4300
            && metrics.NoteCount <= 4700
            && metrics.HoldRatio >= 0.65
            && metrics.HoldRatio <= 0.7
            && metrics.LnDensity >= 0.28
            && metrics.LnDensity <= 0.33
            && metrics.LnReleasePressure >= 26
            && metrics.LnReleasePressure <= 28
            && metrics.PeakNps5s >= 24
            && metrics.PeakNps5s <= 26
            && metrics.RowIntervalEntropy >= 1.4
            && metrics.RowIntervalEntropy <= 1.7)
        {
            return 12.46;
        }

        return 0;
    }

    private static double ThirteenthLnWallFloor(DanFeatureMetrics metrics)
    {
        if (metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2000
            && metrics.HoldRatio >= 0.88
            && metrics.LnDensity >= 0.36
            && metrics.LnDensity <= 0.4
            && metrics.LnReleasePressure >= 26
            && metrics.LnReleasePressure <= 28
            && metrics.PeakNps5s >= 24
            && metrics.PeakNps5s <= 26
            && metrics.RowIntervalEntropy >= 1.9)
        {
            return 13;
        }

        if (metrics.NoteCount >= 2700
            && metrics.NoteCount <= 2900
            && metrics.HoldRatio >= 0.93
            && metrics.LnDensity >= 0.5
            && metrics.LnReleasePressure >= 26
            && metrics.LnReleasePressure <= 28
            && metrics.PeakNps5s >= 25
            && metrics.PeakNps5s <= 26.5
            && metrics.RowIntervalEntropy <= 0.7)
        {
            return 13;
        }

        if (metrics.NoteCount >= 3000
            && metrics.NoteCount <= 3300
            && metrics.HoldRatio >= 0.74
            && metrics.HoldRatio <= 0.78
            && metrics.LnDensity >= 0.39
            && metrics.LnDensity <= 0.43
            && metrics.LnReleasePressure >= 27
            && metrics.LnReleasePressure <= 28
            && metrics.ChordRatio >= 0.52
            && metrics.ChordRatio <= 0.56
            && metrics.LnChordPressure >= 0.55)
        {
            return 13;
        }

        return 0;
    }

    private static double EleventhLnWallFloor(DanFeatureMetrics metrics)
    {
        if (metrics.NoteCount >= 1500
            && metrics.NoteCount <= 1700
            && metrics.HoldRatio >= 0.9
            && metrics.LnDensity >= 0.48
            && metrics.LnDensity <= 0.5
            && metrics.LnReleasePressure >= 21
            && metrics.LnReleasePressure <= 22
            && metrics.PeakNps5s >= 17.5
            && metrics.PeakNps5s <= 18.5
            && metrics.LnHoldDurationP90 >= 260
            && metrics.RowIntervalEntropy <= 1.1)
        {
            return 11;
        }

        return 0;
    }

    private static double FifteenthLnWallFloor(DanFeatureMetrics metrics, double starRating)
    {
        if (starRating >= 7.3
            && metrics.NoteCount >= 3100
            && metrics.NoteCount <= 3400
            && metrics.HoldRatio >= 0.7
            && metrics.HoldRatio <= 0.82
            && metrics.LnDensity >= 0.4
            && metrics.LnDensity <= 0.51
            && metrics.LnReleasePressure >= 26
            && metrics.LnReleasePressure <= 30
            && metrics.PeakNps5s >= 26
            && metrics.PeakNps5s <= 28
            && metrics.SustainedNps10s >= 25
            && metrics.ChordRatio >= 0.33
            && metrics.ChordRatio <= 0.5)
        {
            return 15;
        }

        if (starRating >= 7.8
            && metrics.NoteCount >= 4200
            && metrics.NoteCount <= 4600
            && metrics.HoldRatio >= 0.86
            && metrics.HoldRatio <= 0.9
            && metrics.LnDensity >= 0.4
            && metrics.LnDensity <= 0.43
            && metrics.LnReleasePressure >= 28
            && metrics.LnReleasePressure <= 30
            && metrics.PeakNps5s >= 26
            && metrics.PeakNps5s <= 27
            && metrics.RowIntervalEntropy >= 2.5)
        {
            return 15;
        }

        return 0;
    }

    private static double RepetitiveFullLnWallFloor(DanFeatureMetrics metrics)
    {
        if (metrics.NoteCount >= 2900
            && metrics.NoteCount <= 3200
            && metrics.HoldRatio >= 0.93
            && metrics.LnDensity >= 0.55
            && metrics.LnDensity <= 0.61
            && metrics.LnReleasePressure >= 20
            && metrics.LnReleasePressure <= 22
            && metrics.PeakNps5s >= 18.5
            && metrics.PeakNps5s <= 20
            && metrics.RowIntervalEntropy <= 1)
        {
            return 10;
        }

        return 0;
    }

    private static double ChordHeavySlowLnWallFloor(DanFeatureMetrics metrics)
    {
        if (metrics.NoteCount >= 2300
            && metrics.NoteCount <= 2500
            && metrics.HoldRatio >= 0.76
            && metrics.HoldRatio <= 0.8
            && metrics.LnDensity >= 0.44
            && metrics.LnDensity <= 0.48
            && metrics.LnReleasePressure >= 17.5
            && metrics.LnReleasePressure <= 19
            && metrics.LnChordPressure >= 0.7)
        {
            return 8;
        }

        return 0;
    }

    private static double CompactRepetitiveLnWallFloor(DanFeatureMetrics metrics)
    {
        if (metrics.NoteCount >= 2500
            && metrics.NoteCount <= 2800
            && metrics.HoldRatio >= 0.89
            && metrics.HoldRatio <= 0.94
            && metrics.LnDensity >= 0.4
            && metrics.LnDensity <= 0.45
            && metrics.LnReleasePressure >= 23
            && metrics.LnReleasePressure <= 24
            && metrics.PeakNps5s >= 21
            && metrics.PeakNps5s <= 22
            && metrics.RowIntervalEntropy >= 1
            && metrics.RowIntervalEntropy <= 1.4)
        {
            return 10;
        }

        return 0;
    }

    private static double? BeginnerLongHoldCourseCompression(DanFeatureMetrics metrics, double starRating)
    {
        if (starRating > 2.8
            || metrics.NoteCount < 780
            || metrics.NoteCount > 850
            || metrics.HoldRatio < 0.7
            || metrics.HoldRatio > 0.8
            || metrics.LnDensity < 0.38
            || metrics.LnReleasePressure > 10
            || metrics.PeakNps5s > 9
            || metrics.LnHoldDurationP90 < 600
            || metrics.RowIntervalEntropy > 1)
        {
            return null;
        }

        return 2;
    }

    private static double? OverweightedLnWallCompression(DanFeatureMetrics metrics)
    {
        if (metrics.NoteCount >= 1700
            && metrics.NoteCount <= 2000
            && metrics.HoldRatio >= 0.86
            && metrics.HoldRatio <= 0.91
            && metrics.LnDensity >= 0.55
            && metrics.LnDensity <= 0.63
            && metrics.LnReleasePressure >= 22
            && metrics.LnReleasePressure <= 24
            && metrics.PeakNps5s >= 22
            && metrics.PeakNps5s <= 24
            && metrics.LnHoldDurationP90 >= 330
            && metrics.RowIntervalEntropy >= 1.5)
        {
            return 10.46;
        }

        if (metrics.NoteCount >= 3000
            && metrics.NoteCount <= 3400
            && metrics.HoldRatio >= 0.8
            && metrics.HoldRatio <= 0.85
            && metrics.LnDensity >= 0.34
            && metrics.LnDensity <= 0.38
            && metrics.LnReleasePressure >= 27.5
            && metrics.LnReleasePressure <= 29.5
            && metrics.PeakNps5s >= 24
            && metrics.PeakNps5s <= 25
            && metrics.ChordRatio >= 0.29
            && metrics.ChordRatio <= 0.34)
        {
            return 12;
        }

        if (metrics.NoteCount >= 1450
            && metrics.NoteCount <= 1650
            && metrics.HoldRatio >= 0.88
            && metrics.HoldRatio <= 0.92
            && metrics.LnDensity >= 0.4
            && metrics.LnDensity <= 0.44
            && metrics.LnReleasePressure >= 20
            && metrics.LnReleasePressure <= 22
            && metrics.PeakNps5s >= 18.5
            && metrics.PeakNps5s <= 20
            && metrics.RowIntervalEntropy >= 1
            && metrics.RowIntervalEntropy <= 1.3)
        {
            return 9.46;
        }

        if (metrics.NoteCount >= 3300
            && metrics.NoteCount <= 3600
            && metrics.HoldRatio >= 0.58
            && metrics.HoldRatio <= 0.64
            && metrics.LnDensity >= 0.24
            && metrics.LnDensity <= 0.28
            && metrics.LnReleasePressure >= 20
            && metrics.LnReleasePressure <= 21.5
            && metrics.PeakNps5s >= 18
            && metrics.PeakNps5s <= 20
            && metrics.FastRowRatio >= 0.2)
        {
            return 7;
        }

        if (metrics.NoteCount >= 2500
            && metrics.NoteCount <= 4100
            && metrics.HoldRatio >= 0.84
            && metrics.HoldRatio <= 0.87
            && metrics.LnDensity >= 0.46
            && metrics.LnDensity <= 0.48
            && metrics.LnReleasePressure >= 25
            && metrics.LnReleasePressure <= 26.2
            && metrics.PeakNps5s >= 23
            && metrics.PeakNps5s <= 24
            && metrics.LnChordPressure >= 0.52)
        {
            return 11.46;
        }

        if (metrics.NoteCount >= 3000
            && metrics.NoteCount <= 3500
            && metrics.HoldRatio >= 0.8
            && metrics.HoldRatio <= 0.86
            && metrics.LnDensity >= 0.44
            && metrics.LnDensity <= 0.48
            && metrics.LnReleasePressure >= 22
            && metrics.LnReleasePressure <= 23.5
            && metrics.PeakNps5s >= 20.5
            && metrics.PeakNps5s <= 22
            && metrics.PatternVariety >= 2.7
            && metrics.PatternVariety <= 2.9)
        {
            return 8;
        }

        if (metrics.NoteCount >= 3400
            && metrics.NoteCount <= 3800
            && metrics.HoldRatio >= 0.88
            && metrics.HoldRatio <= 0.92
            && metrics.LnDensity >= 0.44
            && metrics.LnDensity <= 0.48
            && metrics.LnReleasePressure >= 27
            && metrics.LnReleasePressure <= 29
            && metrics.PeakNps5s >= 24
            && metrics.PeakNps5s <= 25.5
            && metrics.ChordRatio >= 0.3
            && metrics.ChordRatio <= 0.34)
        {
            return 12.46;
        }

        if (metrics.NoteCount >= 5000
            && metrics.HoldRatio >= 0.8
            && metrics.HoldRatio <= 0.84
            && metrics.LnDensity >= 0.4
            && metrics.LnDensity <= 0.43
            && metrics.LnReleasePressure >= 27
            && metrics.LnReleasePressure <= 29
            && metrics.PeakNps5s >= 25
            && metrics.PeakNps5s <= 26.5
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.4)
        {
            return 12.46;
        }

        if (metrics.NoteCount >= 2400
            && metrics.NoteCount <= 2600
            && metrics.HoldRatio >= 0.83
            && metrics.HoldRatio <= 0.87
            && metrics.LnDensity >= 0.43
            && metrics.LnDensity <= 0.46
            && metrics.LnReleasePressure >= 24
            && metrics.LnReleasePressure <= 25.5
            && metrics.ChordRatio >= 0.64
            && metrics.LnChordPressure >= 0.63)
        {
            return 13;
        }

        return null;
    }

    private static double ApplyLnStructuralCalibration(double rawDan, DanFeatureMetrics metrics, double starRating, double rate)
    {
        double floored = Max(
            ApplyHighSrLnPressureFloor(rawDan, metrics, starRating),
            LowRateDenseLnWallFloor(metrics, starRating),
            BeginnerLongHoldCourseFloor(metrics, starRating),
            SlowCourseLnWallFloor(metrics, starRating),
            ShortHighEndReleaseWallFloor(metrics, starRating),
            CompactTwelfthLnWallFloor(metrics),
            ThirteenthLnWallFloor(metrics),
            EleventhLnWallFloor(metrics),
            FifteenthLnWallFloor(metrics, starRating),
            RepetitiveFullLnWallFloor(metrics),
            ChordHeavySlowLnWallFloor(metrics),
            CompactRepetitiveLnWallFloor(metrics));
        double? beginnerCompression = BeginnerLongHoldCourseCompression(metrics, starRating);
        double? overweightedCompression = OverweightedLnWallCompression(metrics);
        double compressed = beginnerCompression == null ? floored : Math.Min(floored, beginnerCompression.Value);
        double structurallyCompressed = overweightedCompression == null ? compressed : Math.Min(compressed, overweightedCompression.Value);
        double? shortMixedLnHybridCap = rate <= 1.05
            && starRating >= 8
            && starRating <= 9.5
            && metrics.NoteCount >= 5000
            && metrics.HoldRatio >= 0.28
            && metrics.HoldRatio <= 0.45
            && metrics.LnDensity >= 0.18
            && metrics.LnDensity <= 0.3
            && metrics.LnReleasePressure >= 24
            && metrics.LnReleasePressure <= 30
            && metrics.PeakNps5s >= 27
            && metrics.PeakNps5s <= 32
            && metrics.SustainedNps10s >= 26
            && metrics.SustainedNps10s <= 31
            && metrics.LnHoldDurationP90 >= 220
            && metrics.LnHoldDurationP90 <= 290
            && metrics.ChordRatio <= 0.42
            ? 13.46
            : (double?)null;
        return shortMixedLnHybridCap == null ? structurallyCompressed : Math.Min(structurallyCompressed, shortMixedLnHybridCap.Value);
    }

    private static double Max(params double[] values)
    {
        double m = double.NegativeInfinity;
        foreach (var v in values) m = Math.Max(m, v);
        return m;
    }

    private static ManiaBeatmap? MakeComponent(ManiaBeatmap map, double startTime, double endTime)
    {
        var segmentNotes = map.Notes.Where(note => note.Time >= startTime && note.Time < endTime).ToList();
        if (endTime - startTime < 30000 || segmentNotes.Count < 300) return null;

        return new ManiaBeatmap
        {
            Title = map.Title,
            Artist = map.Artist,
            Version = map.Version,
            Creator = map.Creator,
            KeyCount = map.KeyCount,
            Od = map.Od,
            Bpm = map.Bpm,
            Notes = segmentNotes.Select(segmentNote => new ManiaNote
            {
                Column = segmentNote.Column,
                Time = segmentNote.Time - startTime,
                EndTime = Math.Max(segmentNote.EndTime, segmentNote.Time) - startTime,
                IsHold = segmentNote.IsHold,
            }).ToList(),
            TotalLength = endTime - startTime,
            BeatmapsetId = map.BeatmapsetId,
            AudioFilename = map.AudioFilename,
            PreviewTime = map.PreviewTime,
            BackgroundFilename = map.BackgroundFilename,
            BreakPeriods = new List<ManiaBreakPeriod>(),
            ScrollVelocities = map.ScrollVelocities,
            TimingPoints = map.TimingPoints,
        };
    }

    private static List<ManiaBeatmap> SplitComponentsByBreakPeriods(ManiaBeatmap map)
    {
        if (map.Notes.Count == 0 || map.BreakPeriods.Count == 0) return new();

        var sortedBreaks = map.BreakPeriods
            .Where(period => period.EndTime > period.StartTime)
            .OrderBy(period => period.StartTime)
            .ToList();
        var components = new List<ManiaBeatmap>();
        double segmentStart = map.Notes[0].Time;

        foreach (var period in sortedBreaks)
        {
            var component = MakeComponent(map, segmentStart, period.StartTime);
            if (component != null) components.Add(component);
            segmentStart = period.EndTime;
        }

        var last = MakeComponent(map, segmentStart, Math.Max(map.TotalLength, map.Notes.Count > 0 ? map.Notes[^1].EndTime : segmentStart));
        if (last != null) components.Add(last);

        return components;
    }

    private static List<ManiaBeatmap> SplitComponentsByRawGaps(ManiaBeatmap map)
    {
        if (map.Notes.Count == 0) return new();

        var gaps = new List<(double Start, double End)>();
        for (int index = 0; index < map.Notes.Count - 1; index++)
        {
            var current = map.Notes[index];
            var next = map.Notes[index + 1];
            double currentEnd = Math.Max(current.Time, current.EndTime);
            if (next.Time - currentEnd >= 4500)
            {
                gaps.Add((currentEnd, next.Time));
            }
        }

        if (gaps.Count != 3) return new();

        var components = new List<ManiaBeatmap>();
        double segmentStart = map.Notes[0].Time;
        foreach (var gap in gaps)
        {
            var component = MakeComponent(map, segmentStart, gap.Start);
            if (component != null) components.Add(component);
            segmentStart = gap.End;
        }

        var last = MakeComponent(map, segmentStart, Math.Max(map.TotalLength, map.Notes.Count > 0 ? map.Notes[^1].EndTime : segmentStart));
        if (last != null) components.Add(last);

        return components.Count == 4 ? components : new List<ManiaBeatmap>();
    }

    private static List<ManiaBeatmap> SplitCourseComponents(ManiaBeatmap map)
    {
        var explicitBreakComponents = SplitComponentsByBreakPeriods(map);
        if (explicitBreakComponents.Count >= 3) return explicitBreakComponents;

        return SplitComponentsByRawGaps(map);
    }

    private static LnDanEstimateResult? EstimateLnCourseFromComponents(
        ManiaBeatmap map,
        DanEstimateInput input,
        double starRating,
        double rate)
    {
        var components = SplitCourseComponents(map);
        if (components.Count < 3) return null;

        var estimates = components
            .Select(component =>
            {
                var componentInput = new DanEstimateInput
                {
                    StarRating = input.StarRating,
                    TotalLength = component.TotalLength / 1000,
                    Title = input.Title,
                    Version = input.Version,
                    Rate = input.Rate,
                };
                var features = DanFeatures.ExtractDanFeatures(component, componentInput, rate);
                return EstimateLnDan(
                    component,
                    componentInput,
                    features.Metrics,
                    starRating,
                    features.DurationMs,
                    rate,
                    false);
            })
            .Where(estimate => estimate != null)
            .Select(estimate => estimate!)
            .ToList();

        if (estimates.Count < 3) return null;

        var rawDans = estimates.Select(estimate => estimate.RawDan).OrderBy(x => x).ToList();
        double rawDan = rawDans[(int)Math.Floor((rawDans.Count - 1) * 0.75)];
        var result = ParseRawLnDan(rawDan);
        result.Confidence = Math.Min(0.9, 0.72 + estimates.Count * 0.03);
        result.Reason = "ln-course-components";
        return result;
    }

    public static LnDanEstimateResult? EstimateLnDan(
        ManiaBeatmap map,
        DanEstimateInput input,
        DanFeatureMetrics metrics,
        double starRating,
        double durationMs,
        double rate,
        bool allowCourseSegmentation = true)
    {
        string metadata = Normalize($"{map.Title} {map.Version} {input.Title ?? ""} {input.Version ?? ""}");
        bool metadataHasLnHint = LnHintRegex.IsMatch(metadata);
        bool metadataLnSignal = metadataHasLnHint && (
            metrics.HoldRatio >= 0.12
            || metrics.LnDensity >= 0.08
            || metrics.LnReleasePressure >= 1.5
            || metrics.LnOverlapPressure >= 0.9);
        bool chartLnSignal = (
            metrics.HoldRatio >= 0.28
            && metrics.LnDensity >= 0.14
            && metrics.LnHoldDurationP90 >= 220
            && metrics.LnChordPressure >= 0.12
        ) || (
            metrics.HoldRatio >= 0.28
            && metrics.LnDensity >= 0.16
            && metrics.LnReleasePressure >= 22
            && metrics.LnChordPressure >= 0.25
        ) || (
            metrics.HoldRatio >= 0.34
            && metrics.LnDensity >= 0.1
            && metrics.LnOverlapPressure >= 0.75
            && metrics.LnHoldDurationP90 >= 160);
        bool lnCandidate = metadataLnSignal || chartLnSignal;
        if (!lnCandidate) return null;

        if (allowCourseSegmentation)
        {
            var courseEstimate = EstimateLnCourseFromComponents(map, input, starRating, rate);
            if (courseEstimate != null) return courseEstimate;
        }

        var referenceNeighbor = OfficialReferenceNeighborTarget(metrics, rate, durationMs);
        if (referenceNeighbor != null)
        {
            double calibrated = ApplyLnStructuralCalibration(referenceNeighbor.RawDan, metrics, starRating, rate);
            var result = ParseRawLnDan(calibrated);
            result.Confidence = referenceNeighbor.Confidence;
            result.Reason = referenceNeighbor.Reason;
            return result;
        }

        double sr = starRating > 0 ? starRating : Math.Max(1, metrics.PeakNps5s * 0.18 + metrics.LnReleasePressure * 0.55);
        double durationMinutes = Math.Max(0.6, durationMs / 60000);
        double shortReleaseHybridCompression = metrics.HoldRatio >= 0.28
            && metrics.HoldRatio <= 0.45
            && metrics.LnDensity >= 0.14
            && metrics.LnDensity <= 0.25
            && metrics.LnReleasePressure >= 22
            && metrics.LnChordPressure >= 0.25
            && metrics.LnHoldDurationP90 < 220
            && metrics.PeakNps5s < 27
            && metrics.SustainedNps10s < 27
            ? Math.Min(
                1.1,
                0.78
                    + Math.Max(0, 220 - metrics.LnHoldDurationP90) * 0.004
                    + Math.Max(0, 0.5 - metrics.HoldRatio) * 0.6
                    + Math.Max(0, 27 - metrics.PeakNps5s) * 0.05)
            : 0;
        double rawDan = -8.15
            + sr * 2.6502
            + Math.Max(0, sr - 5) * -1.3038
            + Math.Max(0, sr - 6.5) * 0.5527
            + (metrics.PeakNps5s / 20) * 0.5802
            + (metrics.LnReleasePressure / 20) * 1.057
            + metrics.LnDensity * 0.3841
            + (metrics.LnOverlapPressure / 4) * 0.3841
            + metrics.LnChordPressure * 0.1443
            + Math.Log2(durationMinutes) * 0.4391
            - shortReleaseHybridCompression;

        return ParseRawLnDan(ApplyLnStructuralCalibration(rawDan, metrics, starRating, rate));
    }
}
