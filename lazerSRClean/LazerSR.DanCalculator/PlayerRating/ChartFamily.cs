// Port of the chart-topology hash from mania-hub
// live-backend/src/features/chart-families.ts (chartTopologyKey ~28, hasComparableNotes ~22).
//
// mania-hub uses this only as a candidate finder, then confirms with
// sameChartAtDifferentRate (a uniform time scale+offset match against every
// sibling in the DB). The client has no chart DB to compare against, so this
// port stops at the topology key: v1 chartFamily == topologyKey. Reuploads with
// bit-identical note layout still family together (same key); rate reuploads
// whose timestamps differ do NOT (mania-hub would merge them). See
// CHART_FAMILY_VERSION.
//
// PORT NOTE: `Number(note.isHold)` in JS is 0/1. Ordering uses (time, column,
// endTime) exactly as `orderedNotes`.

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LazerSR.DanCalculator.Beatmap;

namespace LazerSR.DanCalculator.PlayerRating;

public static class ChartFamily
{
    /// <summary>chart-families.ts CHART_FAMILY_VERSION.</summary>
    public const int CHART_FAMILY_VERSION = 1;

    /// <summary>
    /// chart-families.ts hasComparableNotes — an integer key count, at least two
    /// notes, and every note in range with a finite, non-inverted span.
    /// </summary>
    public static bool HasComparableNotes(ManiaBeatmap map)
    {
        if (map.KeyCount <= 0) return false;
        if (map.Notes.Count < 2) return false;
        foreach (var note in map.Notes)
        {
            if (note.Column < 0 || note.Column >= map.KeyCount) return false;
            if (!double.IsFinite(note.Time) || !double.IsFinite(note.EndTime)) return false;
            if (note.EndTime < note.Time) return false;
        }
        return true;
    }

    /// <summary>
    /// chart-families.ts chartTopologyKey — sha256 of <c>"{keyCount}:"</c> then, for
    /// each note ordered by (time, column, endTime), <c>"{column},{isHold?1:0};"</c>.
    /// Null when the chart has no comparable notes.
    /// </summary>
    public static string? ChartTopologyKey(ManiaBeatmap map)
    {
        if (!HasComparableNotes(map)) return null;

        var ordered = map.Notes
            .OrderBy(n => n.Time)
            .ThenBy(n => n.Column)
            .ThenBy(n => n.EndTime);

        var sb = new StringBuilder();
        sb.Append(map.KeyCount).Append(':');
        foreach (var note in ordered)
            sb.Append(note.Column).Append(',').Append(note.IsHold ? 1 : 0).Append(';');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
