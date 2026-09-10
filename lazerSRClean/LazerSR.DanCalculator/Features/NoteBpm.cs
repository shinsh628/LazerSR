// Port of mania-hub live-backend/src/dan/note-bpm.ts
//
// Note-weighted song tempo at 1.0x: the median BPM in effect at the
// hit-object start times. The osu! API's nominal bpm field is the most-common
// timing point by wall-clock duration, which misreads marathons/medleys,
// charts with long off-tempo intros or breaks, and BPM-gimmick timing - the
// player never plays those tempos. Taking the median over the notes instead of
// the clock reads "the tempo under the notes you actually hit" while keeping
// song-tempo units. Only [TimingPoints] and [HitObjects] start times are
// parsed; this stays deliberately lighter than beatmap-parser.ts.
//
// See source file for the full inflated-timing fold rationale.

using System.Globalization;
using System.Text.RegularExpressions;

namespace LazerSR.DanCalculator.Features;

public static class NoteBpm
{
    private const double FOLD_TRIGGER_BPM = 300;
    // Above this no song tempo exists: fold regardless of snap evidence.
    private const double FOLD_MANDATORY_BPM = 500;
    // Candidate divisors walked past the first plausible one. Real inflation is
    // 2x-8x; the cap keeps a beatLength-1 meme point (60000 BPM, first divisor
    // 172) from walking hundreds of divisors that all read garbage.
    private const int FOLD_MAX_CANDIDATES = 16;
    // Beyond a 1ms beat the timing is a number, not a tempo (SV gimmick charts
    // carry 1e11 and 1e24 points): no fold, the clamp takes it.
    private const double FOLD_MAX_RAW_BPM = 60000;
    private const double FOLD_TARGET_MAX_BPM = 350;
    // Deepest fold considered; inflated timing below this is not a thing.
    private const double FOLD_MIN_BPM = 100;
    private const double COARSE_GAP_BEATS = 0.45;
    // Gaps between consecutive rows a mapper places, in beats: binary snaps and
    // the triplet ones.
    private static readonly double[] BINARY_ROW_GAPS = { 1.0 / 16, 1.0 / 8, 3.0 / 16, 1.0 / 4, 3.0 / 8, 1.0 / 2, 3.0 / 4, 1, 1.5, 2, 3, 4 };
    private static readonly double[] TRIPLET_ROW_GAPS = { 1.0 / 12, 1.0 / 6, 1.0 / 3, 2.0 / 3 };
    // Gaps longer than this are pauses, not rhythm, and carry no snap evidence.
    private const double MAX_EVIDENCE_GAP_BEATS = 4;
    // Sections with fewer gaps than this keep their raw tempo.
    private const int MIN_FOLD_EVIDENCE_GAPS = 8;
    // A gap sits on a snap when it lands within this share of the snap's length.
    private const double SNAP_TOLERANCE = 0.03;
    private const double SNAP_TOLERANCE_MIN_MS = 1;
    private const double TRIPLET_SNAP_WEIGHT = 0.5;
    // A deeper fold must explain at least this much more of the evidence than the
    // shallowest candidate to take over.
    private const double FOLD_DEEPER_MARGIN = 0.1;

    // Final safety clamp for tempos the fold cannot make sense of.
    private const double MIN_BPM = 10;
    private const double MAX_BPM = 1200;

    private sealed class TimingSection
    {
        public double Time;
        public double BeatLength;
    }

    private static readonly Regex LeadingFloat = new(@"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?", RegexOptions.Compiled);
    private static readonly Regex LeadingInt = new(@"^\s*[+-]?\d+", RegexOptions.Compiled);

    private static double ParseFloatJs(string s)
    {
        var m = LeadingFloat.Match(s);
        return m.Success ? double.Parse(m.Value.Trim(), CultureInfo.InvariantCulture) : double.NaN;
    }

    private static double ParseIntJs(string s)
    {
        var m = LeadingInt.Match(s);
        return m.Success ? double.Parse(m.Value.Trim(), CultureInfo.InvariantCulture) : double.NaN;
    }

    public static double? ComputeNoteBpm(string osuText)
    {
        var timingPoints = new List<TimingSection>();
        var noteTimes = new List<double>();

        string section = "";
        foreach (var rawLine in osuText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//")) continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }

            if (section == "TimingPoints" && line.Contains(','))
            {
                var parts = line.Split(',');
                if (parts.Length >= 2)
                {
                    double time = ParseFloatJs(parts[0]);
                    double beatLength = ParseFloatJs(parts[1]);
                    bool uninherited = parts.Length < 7 || parts[6].Trim() != "0";
                    if (double.IsFinite(time) && beatLength > 0 && uninherited)
                    {
                        timingPoints.Add(new TimingSection { Time = time, BeatLength = beatLength });
                    }
                }
            }

            if (section == "HitObjects" && line.Contains(','))
            {
                var parts = line.Split(',');
                if (parts.Length >= 5)
                {
                    double time = ParseIntJs(parts[2]);
                    if (double.IsFinite(time)) noteTimes.Add(time);
                }
            }
        }

        if (timingPoints.Count == 0 || noteTimes.Count == 0) return null;

        timingPoints = timingPoints.OrderBy(p => p.Time).ToList();
        noteTimes.Sort();

        // Walk notes and timing points together; the first timing point applies
        // retroactively to notes before it, matching osu!'s behavior.
        var sectionIndexPerNote = new List<int>();
        int pointIndex = 0;
        foreach (var time in noteTimes)
        {
            while (pointIndex + 1 < timingPoints.Count && timingPoints[pointIndex + 1].Time <= time)
            {
                pointIndex += 1;
            }
            sectionIndexPerNote.Add(pointIndex);
        }

        var sectionBpms = new double[timingPoints.Count];
        for (int index = 0; index < timingPoints.Count; index++)
        {
            sectionBpms[index] = ResolveSectionBpm(timingPoints[index], CollectSectionRowGaps(noteTimes, sectionIndexPerNote, index));
        }

        var bpms = sectionIndexPerNote.Select(index => sectionBpms[index]).ToList();
        bpms.Sort();
        int mid = bpms.Count / 2;
        double median = bpms.Count % 2 == 1 ? bpms[mid] : (bpms[mid - 1] + bpms[mid]) / 2;
        return Math.Floor(median * 100 + 0.5) / 100;
    }

    // Gaps between consecutive distinct note rows inside one timing section.
    private static List<double> CollectSectionRowGaps(List<double> noteTimes, List<int> sectionIndexPerNote, int sectionIndex)
    {
        var gaps = new List<double>();
        double? previousTime = null;
        for (int i = 0; i < noteTimes.Count; i++)
        {
            if (sectionIndexPerNote[i] != sectionIndex)
            {
                previousTime = null;
                continue;
            }
            if (previousTime != null && noteTimes[i] > previousTime.Value)
            {
                gaps.Add(noteTimes[i] - previousTime.Value);
            }
            previousTime = noteTimes[i];
        }
        return gaps;
    }

    private static double ResolveSectionBpm(TimingSection point, List<double> rowGaps)
    {
        double rawBpm = 60000 / point.BeatLength;
        double bpm = rawBpm;

        if (rawBpm > FOLD_TRIGGER_BPM && rawBpm <= FOLD_MAX_RAW_BPM && rowGaps.Count >= MIN_FOLD_EVIDENCE_GAPS)
        {
            if (rawBpm > FOLD_MANDATORY_BPM || DominantGapIsCoarse(point.BeatLength, rowGaps))
            {
                bpm = FoldInflatedTempo(rawBpm, point.BeatLength, rowGaps);
            }
        }

        return Math.Min(MAX_BPM, Math.Max(MIN_BPM, bpm));
    }

    private static bool DominantGapIsCoarse(double beatLength, List<double> rowGaps)
    {
        // Snap evidence: gaps in beats on a 1/12 grid (covers 1/3 + 1/4 rhythms).
        var gapCounts = new Dictionary<double, int>();
        foreach (var gap in rowGaps)
        {
            double beats = Math.Floor((gap / beatLength) * 12 + 0.5) / 12;
            if (beats <= 0 || beats > MAX_EVIDENCE_GAP_BEATS) continue;
            gapCounts[beats] = gapCounts.GetValueOrDefault(beats) + 1;
        }
        // Tie-break toward the finer gap: a chart that streams as much as it
        // jacks reads as genuinely timed and keeps its tempo.
        (double Beats, int Count)? dominant = null;
        foreach (var entry in gapCounts)
        {
            if (dominant == null
                || entry.Value > dominant.Value.Count
                || (entry.Value == dominant.Value.Count && entry.Key < dominant.Value.Beats))
            {
                dominant = (entry.Key, entry.Value);
            }
        }
        return dominant != null && dominant.Value.Beats >= COARSE_GAP_BEATS;
    }

    private static double FoldInflatedTempo(double rawBpm, double beatLength, List<double> rowGaps)
    {
        int firstDivisor = (int)Math.Max(2, Math.Ceiling(rawBpm / FOLD_TARGET_MAX_BPM));

        int bestDivisor = firstDivisor;
        double bestScore = SnapGridScore(beatLength * firstDivisor, rowGaps);
        int lastDivisor = firstDivisor + FOLD_MAX_CANDIDATES;
        for (int divisor = firstDivisor + 1; divisor <= lastDivisor && rawBpm / divisor >= FOLD_MIN_BPM; divisor += 1)
        {
            double score = SnapGridScore(beatLength * divisor, rowGaps);
            if (score >= bestScore + FOLD_DEEPER_MARGIN)
            {
                bestDivisor = divisor;
                bestScore = score;
            }
        }
        return rawBpm / bestDivisor;
    }

    // Share of the gap evidence a candidate beat length explains with snaps a
    // mapper places: binary snaps in full, triplets at TRIPLET_SNAP_WEIGHT.
    private static double SnapGridScore(double beatLength, List<double> rowGaps)
    {
        double weight = 0;
        double evidence = 0;
        foreach (var gap in rowGaps)
        {
            if (gap > beatLength * MAX_EVIDENCE_GAP_BEATS) continue;
            evidence += 1;
            if (SitsOnSnap(gap, beatLength, BINARY_ROW_GAPS)) weight += 1;
            else if (SitsOnSnap(gap, beatLength, TRIPLET_ROW_GAPS)) weight += TRIPLET_SNAP_WEIGHT;
        }
        return evidence == 0 ? 0 : weight / evidence;
    }

    private static bool SitsOnSnap(double gap, double beatLength, double[] snaps)
    {
        foreach (var snap in snaps)
        {
            double snapMs = snap * beatLength;
            if (Math.Abs(gap - snapMs) <= Math.Max(SNAP_TOLERANCE_MIN_MS, snapMs * SNAP_TOLERANCE)) return true;
        }
        return false;
    }
}
