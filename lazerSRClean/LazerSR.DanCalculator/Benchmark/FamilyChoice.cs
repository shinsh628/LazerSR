// Port of mania-hub live-backend/src/dan/dan-estimator/family-choice.ts
//
// Part of the deprecated estimateDan benchmark path (see PORTING.md). Not wired
// into the production ClassifyChart.

using LazerSR.DanCalculator.Types;

namespace LazerSR.DanCalculator.Benchmark;

public sealed class DanFamilyChoiceResult
{
    public DanSkillFamily Family;
    public DanFamilyChoiceDebug Debug = new();
}

public static class FamilyChoice
{
    private static readonly DanSkillFamily[] RATING_FAMILIES =
    {
        DanSkillFamily.Jack, DanSkillFamily.Stream, DanSkillFamily.Handstream,
        DanSkillFamily.Stamina, DanSkillFamily.Chordjack, DanSkillFamily.Tech,
    };

    private readonly record struct RuleContext(DanFeatureMetrics Metrics, SkillScores SkillScores, double TopScore);

    private sealed class DanFamilyChoiceRule
    {
        public required string Id;
        public required DanSkillFamily Family;
        public required Func<RuleContext, bool> Applies;
    }

    private static readonly DanFamilyChoiceRule[] FAMILY_CHOICE_RULES =
    {
        new()
        {
            Id = "localized-high-density-jack-spike",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 4000
                && c.Metrics.ChordRatio >= 0.48
                && c.Metrics.ChordRatio <= 0.64
                && c.Metrics.HoldRatio < 0.1
                && c.Metrics.PeakNps5s >= 35
                && c.Metrics.SustainedNps10s >= 34
                && c.Metrics.JackPressure >= 190
                && c.Metrics.StrainSpikiness >= 1.6
                && c.Metrics.Nps5sP90 <= c.Metrics.PeakNps5s - 4
                && c.Metrics.Nps5sP50 <= c.Metrics.PeakNps5s - 10
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.45,
        },
        new()
        {
            Id = "long-jumpstream-stamina",
            Family = DanSkillFamily.Stamina,
            Applies = c => c.Metrics.NoteCount >= 7600
                && c.Metrics.ChordRatio >= 0.45
                && c.Metrics.ChordRatio <= 0.56
                && c.Metrics.HoldRatio < 0.03
                && c.Metrics.JackPressure < 135
                && c.Metrics.SustainedNps10s >= 29
                && c.Metrics.SustainedNps10s <= 32
                && c.Metrics.FastRowRatio >= 0.8
                && c.Metrics.SustainedPressureRatio >= 0.9
                && c.Metrics.PatternVariety <= 2.2
                && c.SkillScores[DanSkillFamily.Stamina] >= c.TopScore - 1.35,
        },
        new()
        {
            Id = "steady-low-rate-jumpstream",
            Family = DanSkillFamily.Jumpstream,
            Applies = c => c.Metrics.NoteCount >= 3600
                && c.Metrics.NoteCount <= 5000
                && c.Metrics.ChordRatio >= 0.42
                && c.Metrics.ChordRatio <= 0.52
                && c.Metrics.TwoNoteChordRatio >= 0.2
                && c.Metrics.HoldRatio < 0.03
                && c.Metrics.JackPressure < 130
                && c.Metrics.PeakNps5s <= 21
                && c.Metrics.SustainedNps10s <= 20
                && c.Metrics.ActiveNps <= 16.5
                && c.Metrics.RowBurstPressure <= 14
                && c.Metrics.RhythmMotifRepeatRatio >= 0.55
                && c.SkillScores[DanSkillFamily.Jumpstream] >= c.TopScore - 0.65,
        },
        new()
        {
            Id = "compact-jumpstream",
            Family = DanSkillFamily.Jumpstream,
            Applies = c => c.Metrics.NoteCount >= 1500
                && c.Metrics.NoteCount <= 3800
                && c.Metrics.ChordRatio >= 0.3
                && c.Metrics.ChordRatio <= 0.56
                && c.Metrics.TwoNoteChordRatio >= 0.18
                && c.Metrics.HoldRatio < 0.12
                && c.Metrics.JackPressure < 165
                && c.Metrics.JumpstreamPressure >= 16
                && c.Metrics.SustainedNps10s >= 18
                && c.Metrics.PeakNps5s >= 20
                && c.Metrics.ChordSizeChangeRate >= 0.3
                && c.SkillScores[DanSkillFamily.Jumpstream] >= c.TopScore - 0.55,
        },
        new()
        {
            Id = "sustained-jumpstream",
            Family = DanSkillFamily.Jumpstream,
            Applies = c => c.Metrics.NoteCount >= 2800
                && c.Metrics.ChordRatio >= 0.3
                && c.Metrics.ChordRatio <= 0.58
                && c.Metrics.TwoNoteChordRatio >= 0.18
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.JackPressure < 155
                && c.Metrics.SustainedNps10s >= 23
                && c.Metrics.SustainedPressureRatio >= 0.72
                && c.SkillScores[DanSkillFamily.Jumpstream] >= c.TopScore - 0.8,
        },
        new()
        {
            Id = "mid-chord-speedjack",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 2200
                && c.Metrics.NoteCount <= 2800
                && c.Metrics.ChordRatio >= 0.45
                && c.Metrics.ChordRatio <= 0.56
                && c.Metrics.HoldRatio < 0.06
                && c.Metrics.JackPressure >= 175
                && c.Metrics.ChordjackPressure >= 175
                && c.Metrics.SustainedNps10s >= 25
                && c.Metrics.SustainedNps10s <= 28
                && c.Metrics.FastRowRatio >= 0.2
                && c.Metrics.FastRowRatio <= 0.42
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.45,
        },
        new()
        {
            Id = "dense-mid-chord-chordjack",
            Family = DanSkillFamily.Chordjack,
            Applies = c => c.Metrics.NoteCount >= 2600
                && c.Metrics.NoteCount <= 3600
                && c.Metrics.ChordRatio >= 0.62
                && c.Metrics.ChordRatio <= 0.72
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.SustainedNps10s >= 28
                && c.Metrics.JackPressure >= 125
                && c.Metrics.JackPressure <= 190
                && c.Metrics.ChordjackPressure >= 170
                && !(c.Metrics.ChordRatio < 0.64
                    && c.Metrics.NoteCount >= 2800
                    && c.Metrics.SustainedNps10s <= 30.5
                    && c.SkillScores[DanSkillFamily.Jack] >= c.SkillScores[DanSkillFamily.Chordjack] - 0.25)
                && c.SkillScores[DanSkillFamily.Chordjack] >= c.SkillScores[DanSkillFamily.Jack] - 0.25
                && c.SkillScores[DanSkillFamily.Chordjack] >= c.TopScore - 0.7,
        },
        new()
        {
            Id = "short-dense-jack-file",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 1800
                && c.Metrics.NoteCount <= 3200
                && c.Metrics.ChordRatio >= 0.54
                && c.Metrics.ChordRatio <= 0.72
                && c.Metrics.HoldRatio < 0.06
                && c.Metrics.JackPressure >= 130
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.25,
        },
        new()
        {
            Id = "slow-repetitive-jackstream",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 1800
                && c.Metrics.NoteCount <= 3200
                && c.Metrics.ChordRatio >= 0.45
                && c.Metrics.ChordRatio <= 0.6
                && c.Metrics.HoldRatio < 0.06
                && c.Metrics.JackPressure >= 115
                && c.Metrics.ChordjackPressure >= 105
                && c.Metrics.SustainedNps10s >= 16
                && c.Metrics.SustainedNps10s <= 22
                && c.Metrics.FastRowRatio < 0.08
                && c.Metrics.RowIntervalEntropy < 1.6
                && c.Metrics.SustainedPressureRatio >= 0.65
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.5,
        },
        new()
        {
            Id = "rated-repetitive-speedjack",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 1800
                && c.Metrics.NoteCount <= 3200
                && c.Metrics.ChordRatio >= 0.45
                && c.Metrics.ChordRatio <= 0.6
                && c.Metrics.HoldRatio < 0.06
                && c.Metrics.JackPressure >= 150
                && c.Metrics.ChordjackPressure >= 150
                && c.Metrics.SustainedNps10s >= 22.5
                && c.Metrics.SustainedNps10s <= 30
                && c.Metrics.FastRowRatio < 0.1
                && c.Metrics.RowIntervalEntropy < 1.7
                && c.Metrics.SustainedPressureRatio >= 0.65
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.5,
        },
        new()
        {
            Id = "compact-high-chord-wall-chordjack",
            Family = DanSkillFamily.Chordjack,
            Applies = c => c.Metrics.NoteCount >= 1800
                && c.Metrics.NoteCount <= 2200
                && c.Metrics.ChordRatio >= 0.84
                && c.Metrics.ChordRatio <= 0.9
                && c.Metrics.HoldRatio < 0.06
                && c.Metrics.PeakNps5s >= 29
                && c.Metrics.SustainedNps10s >= 28
                && c.SkillScores[DanSkillFamily.Chordjack] >= c.TopScore - 0.95,
        },
        new()
        {
            Id = "dense-wall-jack",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 1800
                && c.Metrics.ChordRatio >= 0.74
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.JackPressure >= 140
                && c.Metrics.SustainedNps10s >= 23
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.45,
        },
        new()
        {
            Id = "medium-wall-jack",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 3000
                && c.Metrics.ChordRatio >= 0.62
                && c.Metrics.ChordRatio <= 0.74
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.JackPressure >= 145
                && c.Metrics.SustainedNps10s >= 27
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.5,
        },
        new()
        {
            Id = "long-sparse-jack-drop",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 4800
                && c.Metrics.NoteCount <= 7200
                && c.Metrics.ChordRatio >= 0.48
                && c.Metrics.ChordRatio <= 0.66
                && c.Metrics.HoldRatio < 0.13
                && c.Metrics.SustainedNps10s >= 18
                && c.Metrics.SustainedNps10s <= 27.5
                && c.Metrics.JackPressure >= 135
                && c.Metrics.JackPressure <= 180
                && c.Metrics.FastRowRatio <= 0.38
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.35,
        },
        new()
        {
            Id = "compact-mid-chord-handstream",
            Family = DanSkillFamily.Handstream,
            Applies = c => c.Metrics.NoteCount >= 2000
                && c.Metrics.NoteCount <= 3300
                && c.Metrics.ChordRatio >= 0.38
                && c.Metrics.ChordRatio <= 0.56
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.JackPressure < 150
                && c.Metrics.SustainedNps10s >= 21
                && c.Metrics.SustainedNps10s < 34
                && c.Metrics.PeakNps5s >= 22
                && c.Metrics.ChordSizeChangeRate >= 0.45
                && c.Metrics.DirectionChangeRate >= 0.55
                && c.SkillScores[DanSkillFamily.Handstream] >= c.TopScore - 0.5,
        },
        new()
        {
            Id = "sustained-mid-chord-handstream-speed",
            Family = DanSkillFamily.Handstream,
            Applies = c => c.Metrics.NoteCount >= 3000
                && c.Metrics.NoteCount <= 4300
                && c.Metrics.ChordRatio >= 0.32
                && c.Metrics.ChordRatio <= 0.42
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.JackPressure < 155
                && c.Metrics.PeakNps5s >= 30
                && c.Metrics.SustainedNps10s >= 29
                && c.Metrics.SustainedPressureRatio >= 0.75
                && c.Metrics.RowIntervalEntropy < 2
                && c.SkillScores[DanSkillFamily.Handstream] >= c.TopScore - 0.25,
        },
        new()
        {
            Id = "low-sr-technical-rhythm",
            Family = DanSkillFamily.Tech,
            Applies = c => c.Metrics.NoteCount >= 2200
                && c.Metrics.NoteCount <= 4200
                && c.Metrics.ChordRatio >= 0.16
                && c.Metrics.ChordRatio <= 0.38
                && c.Metrics.HoldRatio < 0.16
                && c.Metrics.RowBurstPressure >= 20
                && c.Metrics.FastRowRatio >= 0.5
                && c.Metrics.ChordSizeChangeRate >= 0.24
                && c.Metrics.DirectionChangeRate >= 0.62
                && c.SkillScores[DanSkillFamily.Tech] >= c.TopScore - 0.45,
        },
        new()
        {
            Id = "syncopated-chord-tech",
            Family = DanSkillFamily.Tech,
            Applies = c => c.Metrics.NoteCount >= 1600
                && c.Metrics.NoteCount <= 2600
                && c.Metrics.ChordRatio >= 0.28
                && c.Metrics.ChordRatio <= 0.38
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.FastRowRatio >= 0.42
                && c.Metrics.FastRowRatio <= 0.72
                && c.Metrics.RowIntervalEntropy >= 2
                && c.Metrics.ChordSizeChangeRate >= 0.34
                && c.SkillScores[DanSkillFamily.Tech] >= c.TopScore - 0.45,
        },
        new()
        {
            Id = "compact-chord-switch-tech",
            Family = DanSkillFamily.Tech,
            Applies = c => c.Metrics.NoteCount >= 1600
                && c.Metrics.NoteCount <= 2500
                && c.Metrics.ChordRatio >= 0.3
                && c.Metrics.ChordRatio <= 0.48
                && c.Metrics.HoldRatio >= 0.025
                && c.Metrics.HoldRatio <= 0.12
                && c.Metrics.FastRowRatio >= 0.72
                && c.Metrics.ChordSizeChangeRate >= 0.48
                && c.Metrics.JackPressure >= 165
                && c.Metrics.TechPressure >= 6.8
                && c.SkillScores[DanSkillFamily.Tech] >= c.TopScore - 0.45,
        },
        new()
        {
            Id = "technical-anchor",
            Family = DanSkillFamily.Tech,
            Applies = c => c.Metrics.NoteCount >= 1700
                && c.Metrics.NoteCount <= 3300
                && c.Metrics.ChordRatio >= 0.26
                && c.Metrics.ChordRatio <= 0.38
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.JackPressure >= 185
                && c.Metrics.PeakNps1s >= 32
                && c.Metrics.PeakNps5s >= 27
                && c.SkillScores[DanSkillFamily.Tech] >= c.TopScore - 0.55,
        },
        new()
        {
            Id = "rated-vibro-jumptrill",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 4000
                && c.Metrics.ChordRatio >= 0.42
                && c.Metrics.ChordRatio <= 0.58
                && c.Metrics.HoldRatio >= 0.1
                && c.Metrics.HoldRatio <= 0.24
                && c.Metrics.JackPressure >= 165
                && c.Metrics.PeakNps1s >= 44
                && c.Metrics.SustainedNps10s >= 30
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.4,
        },
        new()
        {
            Id = "high-sustained-mid-chord-stamina",
            Family = DanSkillFamily.Stamina,
            Applies = c => c.Metrics.SustainedNps10s >= 34
                && c.Metrics.ChordRatio >= 0.32
                && c.Metrics.ChordRatio <= 0.7
                && c.Metrics.JackPressure < 195
                && c.SkillScores[DanSkillFamily.Stamina] >= c.TopScore - 0.95,
        },
        new()
        {
            Id = "long-fast-mid-chord-stamina-transition",
            Family = DanSkillFamily.Stamina,
            Applies = c => c.Metrics.NoteCount >= 5000
                && c.Metrics.ChordRatio >= 0.42
                && c.Metrics.ChordRatio <= 0.58
                && c.Metrics.HoldRatio < 0.04
                && c.Metrics.JackPressure < 145
                && c.Metrics.SustainedNps10s >= 27
                && c.Metrics.SustainedNps10s <= 33
                && c.Metrics.FastRowRatio >= 0.8
                && c.Metrics.SustainedPressureRatio >= 0.86
                && c.Metrics.PatternVariety <= 2.6
                && c.SkillScores[DanSkillFamily.Stamina] >= c.TopScore - 1.35,
        },
        new()
        {
            Id = "long-mid-chord-stamina",
            Family = DanSkillFamily.Stamina,
            Applies = c => c.Metrics.SustainedNps10s >= 28
                && c.Metrics.ChordRatio >= 0.38
                && c.Metrics.ChordRatio <= 0.75
                && c.Metrics.JackPressure < 165
                && c.Metrics.NoteCount >= 4500
                && c.SkillScores[DanSkillFamily.Stamina] >= c.TopScore - 1.05,
        },
        new()
        {
            Id = "cyber-like-mid-chord-stamina",
            Family = DanSkillFamily.Stamina,
            Applies = c => c.Metrics.NoteCount >= 4200
                && c.Metrics.ChordRatio >= 0.42
                && c.Metrics.ChordRatio <= 0.6
                && c.Metrics.JackPressure < 150
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.SustainedNps10s >= 23
                && c.SkillScores[DanSkillFamily.Stamina] >= c.TopScore - 0.65,
        },
        new()
        {
            Id = "long-mid-chord-stamina-family-bias",
            Family = DanSkillFamily.Stamina,
            Applies = c => c.Metrics.NoteCount >= 4500
                && c.Metrics.ChordRatio >= 0.42
                && c.Metrics.ChordRatio <= 0.6
                && c.Metrics.JackPressure < 150
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.SustainedNps10s >= 20
                && c.SkillScores[DanSkillFamily.Stamina] >= c.TopScore - 0.75,
        },
        new()
        {
            Id = "simple-fast-high-chord-wall-jack",
            Family = DanSkillFamily.Jack,
            Applies = c => c.Metrics.NoteCount >= 3000
                && c.Metrics.ChordRatio >= 0.78
                && c.Metrics.HoldRatio < 0.08
                && c.Metrics.JackPressure >= 125
                && c.Metrics.JackPressure < 145
                && c.Metrics.PeakNps5s >= 32
                && c.Metrics.SustainedNps10s >= 31
                && c.Metrics.RowIntervalEntropy <= 1.2
                && c.Metrics.PatternVariety <= 1.8
                && c.Metrics.RowPatternChangeRate <= 0.5
                && c.SkillScores[DanSkillFamily.Jack] >= c.TopScore - 0.5,
        },
        new()
        {
            Id = "low-chord-sustained-speed",
            Family = DanSkillFamily.Stream,
            Applies = c => c.Metrics.ChordRatio <= 0.28
                && c.Metrics.SustainedNps10s >= 25
                && c.Metrics.PeakNps5s >= 26
                && c.Metrics.JackPressure < 175
                && c.Metrics.TechPressure < 6.4
                && c.SkillScores[DanSkillFamily.Stream] >= c.TopScore - 0.7,
        },
        new()
        {
            Id = "burst-tech",
            Family = DanSkillFamily.Tech,
            Applies = c => c.Metrics.PeakNps1s >= 34
                && c.Metrics.ChordRatio >= 0.18
                && c.Metrics.ChordRatio <= 0.36
                && c.Metrics.TechPressure >= 5.6
                && c.Metrics.JackPressure >= 130
                && c.Metrics.JackPressure <= 190
                && c.SkillScores[DanSkillFamily.Tech] >= c.TopScore - 0.45,
        },
        new()
        {
            Id = "steady-stream",
            Family = DanSkillFamily.Stream,
            Applies = c => c.Metrics.ChordRatio <= 0.38
                && c.Metrics.SustainedNps10s >= 25
                && c.Metrics.PeakNps5s >= 26
                && c.Metrics.JackPressure < 155
                && c.SkillScores[DanSkillFamily.Stream] >= c.TopScore - 0.35,
        },
        new()
        {
            Id = "dense-chordjack",
            Family = DanSkillFamily.Chordjack,
            Applies = c => c.Metrics.ChordRatio >= 0.72
                && c.Metrics.HoldRatio < 0.18
                && c.Metrics.JackPressure < 150
                && c.SkillScores[DanSkillFamily.Chordjack] >= c.TopScore - 0.35,
        },
    };

    // True chordjack re-hits columns on consecutive chords (overlap 0.5+); the
    // raw chordjack score is density-driven and a dense bracket/jumpstream file
    // can carry the top score with overlap ~0.1, so below this floor the family
    // is ineligible outright and drops out of the ranking entirely (leaving its
    // inflated score in place would suppress the legitimate families' rules).
    private const double CHORDJACK_MIN_CHORD_OVERLAP = 0.25;

    public static DanFamilyChoiceResult ChooseSkillFamily(SkillScores skillScores, DanFeatureMetrics metrics)
    {
        bool chordjackEligible = metrics.ChordColumnOverlapRatio >= CHORDJACK_MIN_CHORD_OVERLAP;
        var ranked = RATING_FAMILIES
            .Where(family => chordjackEligible || family != DanSkillFamily.Chordjack)
            .Select(family => (Family: family, Score: skillScores[family]))
            .OrderByDescending(x => x.Score)
            .ToList();
        var (topFamily, topScore) = ranked[0];

        DanFamilyChoiceResult Choose(DanSkillFamily selectedFamily, string reason) => new()
        {
            Family = selectedFamily,
            Debug = new DanFamilyChoiceDebug
            {
                TopFamily = topFamily,
                TopScore = topScore,
                SelectedFamily = selectedFamily,
                Reason = reason,
            },
        };

        foreach (var rule in FAMILY_CHOICE_RULES)
        {
            if (!chordjackEligible && rule.Family == DanSkillFamily.Chordjack) continue;
            if (rule.Applies(new RuleContext(metrics, skillScores, topScore)))
            {
                return Choose(rule.Family, rule.Id);
            }
        }

        return Choose(topFamily, "top-score");
    }
}
