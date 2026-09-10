// Port of mania-hub live-backend/vendor/leoblack/estimator/intervals/index.js
//
// The DAN_INDEX structure: keymode -> { RC: {default, extended?}, LN?: {default, extended?} }.
// Shape mirrors index.d.ts: RC is always present; LN is optional (10K has no LN key —
// see PORT_NOTES.md / the ".d.ts marks it optional" note).
//
// DanIntervalTable == (double Lo, double Hi, string Name)[]  (see Tables.cs).

namespace LazerSR.DanCalculator.Intervals;

/// <summary>A {default, extended?} table pair (JS `{ default, extended }`).</summary>
public sealed class DanTablePair
{
    /// <summary>JS `.default` — always present.</summary>
    public required (double Lo, double Hi, string Name)[] Default;

    /// <summary>JS `.extended` — <c>null</c> when the keymode has no extended table.</summary>
    public (double Lo, double Hi, string Name)[]? Extended;

    /// <summary>JS `pair[useExtended ? "extended" : "default"] ?? pair.default`.</summary>
    public (double Lo, double Hi, string Name)[] Resolve(bool useExtended)
        => (useExtended ? Extended : Default) ?? Default;
}

/// <summary>One keymode entry: <c>DAN_INDEX[keyCount]</c>.</summary>
public sealed class DanKeyEntry
{
    public required DanTablePair Rc;

    /// <summary>JS `.LN` — <c>null</c> for keymodes without an LN table (e.g. 10K).</summary>
    public DanTablePair? Ln;
}

public static class DanIndex
{
    private static readonly DanKeyEntry Entry4 = new()
    {
        Rc = new DanTablePair { Default = Tables.Rc4K, Extended = Tables.RcExt4K },
        Ln = new DanTablePair { Default = Tables.Ln4K, Extended = Tables.LnExt4K },
    };

    private static readonly DanKeyEntry Entry6 = new()
    {
        Rc = new DanTablePair { Default = Tables.Rc6K },
        Ln = new DanTablePair { Default = Tables.Ln6K },
    };

    private static readonly DanKeyEntry Entry7 = new()
    {
        Rc = new DanTablePair { Default = Tables.Rc7K, Extended = Tables.RcExt7K },
        Ln = new DanTablePair { Default = Tables.Ln7K },
    };

    private static readonly DanKeyEntry Entry10 = new()
    {
        Rc = new DanTablePair { Default = Tables.Rc10K },
        // LN intentionally absent (DAN_INDEX[10] has no LN key).
    };

    /// <summary>
    /// JS `DAN_INDEX[columnCount]`. Returns <c>null</c> for any keymode not in the
    /// index (JS object lookup yields <c>undefined</c>). Non-integral key counts also
    /// miss (JS would coerce e.g. 4.5 to the key "4.5").
    /// </summary>
    public static DanKeyEntry? For(double keyCount)
    {
        if (keyCount == 4) return Entry4;
        if (keyCount == 6) return Entry6;
        if (keyCount == 7) return Entry7;
        if (keyCount == 10) return Entry10;
        return null;
    }
}
