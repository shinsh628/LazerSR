// Port of vendor/leoblack/interlude/types.js
// NoteType is re-exported from patterns/chart.js (single source).

using LazerSR.DanCalculator.Patterns;

namespace LazerSR.DanCalculator.Interlude;

/// <summary>An interlude note row: { time, data }.</summary>
public sealed class InterludeRow
{
    public double Time;
    public NoteType[] Data = Array.Empty<NoteType>();
}

public static class InterludeTypes
{
    /// <summary>new Array(keyCount).fill(NoteType.NOTHING) — default NoteType is NOTHING (0).</summary>
    public static NoteType[] CreateEmptyRow(int keyCount) => new NoteType[keyCount];

    public static bool IsPlayableNoteType(NoteType noteType)
        => noteType == NoteType.NORMAL || noteType == NoteType.HOLDHEAD;

    public static bool IsRowEmpty(NoteType[] row)
    {
        for (int i = 0; i < row.Length; i += 1)
        {
            NoteType noteType = row[i];
            if (noteType != NoteType.NOTHING && noteType != NoteType.HOLDBODY)
            {
                return false;
            }
        }
        return true;
    }
}
