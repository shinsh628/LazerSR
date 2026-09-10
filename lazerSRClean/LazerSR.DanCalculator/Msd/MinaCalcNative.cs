// Port of LazerSR.Hook/Calculators/MsdCalculator.cs, minus the osu.Game IBeatmap
// dependency. Feeds MinaCalc.dll note rows built from our own parsed ManiaBeatmap.
//
// PORT NOTE (MSD version): lazerSR ships ONE MinaCalc.dll build. LeoBlack's
//   vendor/leoblack/ett pins 6 Etterna engine versions (0.68..0.75) and selects
//   per keycount. We have no version selection: EtternaVersion is a fixed string.
// PORT NOTE (4K only): our MinaCalc.dll's calc_msd returns null / rejects any
//   keycount other than 4 (matching the Hook's ConvertBeatmap TotalColumns != 4
//   guard). LeoBlack's harness rates 4..18K. Non-4K charts get a null result here.
// PORT NOTE (rate handling): the native calc_msd computes all 14 discrete rates
//   (0.7x..2.0x, step 0.1) at once, at the MSD baseline score goal (~0.93). It
//   takes no rate and no scoreGoal argument. To evaluate an arbitrary requested
//   rate we RESCALE every row time by 1/rate before the call and read the R10
//   (1.0x) field of the result — equivalent to feeding `musicRate` into the WASM.
//   We never read the other Rxx fields.
// PORT NOTE (scoreGoal): the DLL ALSO exports `calc_ssr(calc, rows, num, music_rate,
//   score_goal, keycount) -> Ssr` — the standard MinaSDCalc SSR path, which DOES
//   honour both music_rate and score_goal (verified: goal 0.93 vs 0.99 shift the
//   skillset vector). `MsdAtGoalNative` uses it for the player-rating pipeline,
//   which needs SSR at the play's wife accuracy goal. Rows are passed RAW (no 1/rate
//   rescale) because calc_ssr applies music_rate internally. Still 4K-only.
// PORT NOTE (engine version): our DLL is MinaCalc v505. mania-hub's player-rating
//   MSD path runs LeoBlack's vendored Etterna WASM — v0.72.3 for 4K, v0.74.0 for
//   non-4K (vendor/leoblack/ett/versions/index.js). So our skillset values are NOT
//   bit-identical to mania-hub's; the bucket argmax is generally stable across the
//   gap but the published SSR numbers differ by a few percent. See
//   temp/player-rating-port/MINACALC-ENGINE-NOTE.md.

using System.Runtime.InteropServices;
using LazerSR.DanCalculator.Beatmap;

namespace LazerSR.DanCalculator.Msd;

/// <summary>A calculator note row: an absolute time in ms and a column bitmask (bit c = column c).</summary>
public readonly record struct MsdNoteRow(int TimeMs, uint ColumnMask);

/// <summary>The eight MinaCalc skillset SSR values for one rate. All doubles (JS `number`).</summary>
public sealed record MsdSkillset(
    double Overall, double Stream, double Jumpstream, double Handstream,
    double Stamina, double Jackspeed, double Chordjack, double Technical);

/// <summary>Per-interval skillset difficulty arrays from calc_timeline (at the requested rate).</summary>
public sealed record MsdTimeline(
    float[] Stream, float[] Js, float[] Hs, float[] Jack, float[] Cj, float[] Tech, int IntervalCount);

// MinaCalc 505 caches a thread_local reference to the Calc object passed on a thread's
// first calc_msd call (Ulbu.h: `Calc& _calc`). Creating/destroying a Calc per call left
// that reference dangling whenever a thread-pool thread was reused — a use-after-free.
// Fix: one persistent Calc, and all native calls confined to a single dedicated worker
// thread so that cached reference stays valid.
public static class MinaCalcNative
{
    private static volatile bool _available;
    private static volatile bool _ssrExportMissing;
    private static IntPtr _calc;

    private static readonly object _mailboxLock = new();
    private static Request? _pending;
    private static readonly AutoResetEvent _signal = new(false);

    private abstract class Request
    {
        public abstract void Run();
    }

    private sealed class MsdRequest : Request
    {
        public required uint[] Masks;
        public required float[] Times;
        public readonly TaskCompletionSource<MsdSkillset?> Result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run() => Result.TrySetResult(RunMsd(this));
    }

    private sealed class TimelineRequest : Request
    {
        public required uint[] Masks;
        public required float[] Times;
        public readonly TaskCompletionSource<MsdTimeline?> Result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run() => Result.TrySetResult(RunTimeline(this));
    }

    private sealed class MsdAtGoalRequest : Request
    {
        public required uint[] Masks;
        public required float[] Times;
        public required float Rate;
        public required float Goal;
        public required uint KeyCount;
        public readonly TaskCompletionSource<MsdSkillset?> Result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run() => Result.TrySetResult(RunMsdAtGoal(this));
    }

    static MinaCalcNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MinaCalcNative).Assembly, (name, _, _) =>
        {
            if (name != "MinaCalc") return IntPtr.Zero;
            string? d = Path.GetDirectoryName(typeof(MinaCalcNative).Assembly.Location);
            if (string.IsNullOrEmpty(d)) d = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(d)) return IntPtr.Zero;
            return NativeLibrary.TryLoad(Path.Combine(d, "MinaCalc.dll"), out var h) ? h : IntPtr.Zero;
        });

        new Thread(WorkerLoop) { IsBackground = true, Name = "LazerSR-DanCalc-MSD" }.Start();
    }

    public static bool IsAvailable => _available;

    // ---- public entry points --------------------------------------------------

    /// <summary>Build calculator rows from a parsed chart. 4K only (returns null otherwise).
    /// When <paramref name="lnTailTaps"/> is set, each hold's release lands as an extra row
    /// in its column so the calc sees the release work (holds shorter than 50ms add nothing).</summary>
    public static IReadOnlyList<MsdNoteRow>? BuildRows(ManiaBeatmap map, bool lnTailTaps = false)
    {
        if (map.KeyCount != 4) return null; // PORT NOTE: 4K-only native calc.

        const int minTailGapMs = 50;
        var byTime = new SortedDictionary<int, uint>();
        foreach (var note in map.Notes)
        {
            int col = note.Column;
            int start = (int)Math.Truncate(note.Time);
            if (col < 0 || col > 31) continue;

            byTime.TryGetValue(start, out uint prev);
            byTime[start] = prev | (1u << col);

            if (lnTailTaps && note.IsHold)
            {
                int end = (int)Math.Truncate(note.EndTime);
                if (end - start >= minTailGapMs)
                {
                    byTime.TryGetValue(end, out uint prevEnd);
                    byTime[end] = prevEnd | (1u << col);
                }
            }
        }

        var rows = new List<MsdNoteRow>(byTime.Count);
        foreach (var kv in byTime) rows.Add(new MsdNoteRow(kv.Key, kv.Value));
        return rows;
    }

    /// <summary>MSD skillset values for the given rows at an arbitrary <paramref name="rate"/>.
    /// Rows are rescaled by 1/rate and the R10 (1.0x) result field is read — see PORT NOTE.
    /// Returns null when the native lib is unavailable, the compute fails, or rows.Count &lt;= 1.</summary>
    public static MsdSkillset? MsdForAllRatesNative(IReadOnlyList<MsdNoteRow> rows, double rate)
    {
        if (rows == null || rows.Count <= 1) return null;
        if (!double.IsFinite(rate) || rate <= 0) rate = 1.0;

        var (masks, times) = ToNativeArrays(rows, rate);

        var req = new MsdRequest { Masks = masks, Times = times };
        Enqueue(req);
        return req.Result.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// SSR skillset values for the given rows at an arbitrary <paramref name="rate"/> AND
    /// <paramref name="goal"/> (wife accuracy goal), via the DLL's <c>calc_ssr</c> export.
    /// Rows are passed raw — calc_ssr applies the music rate internally. 4K only
    /// (<paramref name="keyCount"/> must be 4; v505 has no n-key pipeline).
    /// Returns null when the native lib / calc_ssr export is unavailable, the compute
    /// fails, or rows.Count &lt;= 1.
    /// </summary>
    public static MsdSkillset? MsdAtGoalNative(IReadOnlyList<MsdNoteRow> rows, double rate, double goal, int keyCount)
    {
        if (rows == null || rows.Count <= 1) return null;
        if (_ssrExportMissing) return null;
        if (keyCount != 4) return null; // PORT NOTE: v505 calc_ssr is 4K-only.
        if (!double.IsFinite(rate) || rate <= 0) rate = 1.0;
        if (!double.IsFinite(goal) || goal <= 0) goal = 0.93;

        var (masks, times) = RawNativeArrays(rows);

        var req = new MsdAtGoalRequest
        {
            Masks = masks,
            Times = times,
            Rate = (float)rate,
            Goal = (float)goal,
            KeyCount = (uint)keyCount,
        };
        Enqueue(req);
        return req.Result.Task.GetAwaiter().GetResult();
    }

    /// <summary>Per-interval skillset timeline for the given rows at <paramref name="rate"/>.</summary>
    public static MsdTimeline? CalcTimelineNative(IReadOnlyList<MsdNoteRow> rows, double rate)
    {
        if (rows == null || rows.Count <= 1) return null;
        if (!double.IsFinite(rate) || rate <= 0) rate = 1.0;

        var (masks, times) = ToNativeArrays(rows, rate);

        var req = new TimelineRequest { Masks = masks, Times = times };
        Enqueue(req);
        return req.Result.Task.GetAwaiter().GetResult();
    }

    // ---- row conversion -----------------------------------------------------

    private static (uint[] Masks, float[] Times) ToNativeArrays(IReadOnlyList<MsdNoteRow> rows, double rate)
    {
        // osu! allows notes before the audio lead-in (negative timestamps); a negative
        // row time walks MinaCalc's interval index out of bounds. Only the gaps between
        // rows carry difficulty, so shift such a chart to start at zero instead.
        int offset = rows.Count > 0 && rows[0].TimeMs < 0 ? -rows[0].TimeMs : 0;

        var masks = new uint[rows.Count];
        var times = new float[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            masks[i] = rows[i].ColumnMask;
            times[i] = (float)((rows[i].TimeMs + offset) / 1000.0 / rate);
        }
        return (masks, times);
    }

    /// <summary>Native rows in seconds, NO 1/rate rescale (calc_ssr takes music_rate itself).
    /// Same negative-timestamp shift as <see cref="ToNativeArrays"/>.</summary>
    private static (uint[] Masks, float[] Times) RawNativeArrays(IReadOnlyList<MsdNoteRow> rows)
    {
        int offset = rows.Count > 0 && rows[0].TimeMs < 0 ? -rows[0].TimeMs : 0;

        var masks = new uint[rows.Count];
        var times = new float[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            masks[i] = rows[i].ColumnMask;
            times[i] = (float)((rows[i].TimeMs + offset) / 1000.0);
        }
        return (masks, times);
    }

    // ---- worker thread ----------------------------------------------------------

    private static void Enqueue(Request req)
    {
        Request? superseded;
        lock (_mailboxLock)
        {
            superseded = _pending;
            _pending = req;
        }

        switch (superseded)
        {
            case MsdRequest m: m.Result.TrySetResult(null); break;
            case TimelineRequest t: t.Result.TrySetResult(null); break;
            case MsdAtGoalRequest g: g.Result.TrySetResult(null); break;
        }

        _signal.Set();
    }

    private static void WorkerLoop()
    {
        try
        {
            _calc = CreateCalc();
            _available = _calc != IntPtr.Zero;
        }
        catch
        {
            _available = false;
        }

        while (true)
        {
            _signal.WaitOne();

            Request? req;
            lock (_mailboxLock)
            {
                req = _pending;
                _pending = null;
            }
            if (req == null) continue;

            try { req.Run(); }
            catch
            {
                switch (req)
                {
                    case MsdRequest m: m.Result.TrySetResult(null); break;
                    case TimelineRequest t: t.Result.TrySetResult(null); break;
                    case MsdAtGoalRequest g: g.Result.TrySetResult(null); break;
                }
            }
        }
    }

    private static MsdSkillset? RunMsd(MsdRequest req)
    {
        if (!_available) return null;

        try
        {
            var rows = BuildNative(req.Masks, req.Times);
            if (rows.Length == 0) return null;

            var result = CalcMsd(_calc, rows, (UIntPtr)rows.Length);
            var r = result.R10;
            return new MsdSkillset(
                r.Overall, r.Stream, r.Jumpstream, r.Handstream,
                r.Stamina, r.Jackspeed, r.Chordjack, r.Technical);
        }
        catch
        {
            return null;
        }
    }

    private static MsdSkillset? RunMsdAtGoal(MsdAtGoalRequest req)
    {
        if (!_available || _ssrExportMissing) return null;

        try
        {
            var rows = BuildNative(req.Masks, req.Times);
            if (rows.Length == 0) return null;

            var r = CalcSsr(_calc, rows, (UIntPtr)rows.Length, req.Rate, req.Goal, req.KeyCount, CALC_MODE_SSR);
            return new MsdSkillset(
                r.Overall, r.Stream, r.Jumpstream, r.Handstream,
                r.Stamina, r.Jackspeed, r.Chordjack, r.Technical);
        }
        catch (EntryPointNotFoundException)
        {
            _ssrExportMissing = true; // DLL predates calc_ssr — degrade quietly forever.
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static MsdTimeline? RunTimeline(TimelineRequest req)
    {
        if (!_available) return null;

        try
        {
            var rows = BuildNative(req.Masks, req.Times);
            if (rows.Length == 0) return null;

            const int maxIntervals = 32768;
            var buf = new float[maxIntervals * 6];
            int n = CalcTimeline(_calc, rows, (UIntPtr)rows.Length, buf, buf.Length);
            if (n <= 0) return null;

            var stream = new float[n]; var js = new float[n]; var hs = new float[n];
            var jack = new float[n]; var cj = new float[n]; var tech = new float[n];
            for (int i = 0; i < n; i++)
            {
                stream[i] = buf[i * 6 + 0]; js[i] = buf[i * 6 + 1]; hs[i] = buf[i * 6 + 2];
                jack[i] = buf[i * 6 + 3]; cj[i] = buf[i * 6 + 4]; tech[i] = buf[i * 6 + 5];
            }
            return new MsdTimeline(stream, js, hs, jack, cj, tech, n);
        }
        catch
        {
            return null;
        }
    }

    private static NoteInfoNative[] BuildNative(uint[] masks, float[] times)
    {
        var rows = new NoteInfoNative[masks.Length];
        for (int i = 0; i < masks.Length; i++)
            rows[i] = new NoteInfoNative { Notes = masks[i], RowTime = times[i] };
        return rows;
    }

    // ---- P/Invoke -------------------------------------------------------------

    [DllImport("MinaCalc", EntryPoint = "create_calc", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateCalc();

    [DllImport("MinaCalc", EntryPoint = "calc_msd", CallingConvention = CallingConvention.Cdecl)]
    private static extern MsdAllRatesRaw CalcMsd(IntPtr calc, NoteInfoNative[] rows, UIntPtr numRows);

    // Mirrors minacalc c_code/API.cpp `calc_at_rate`:
    //   Ssr calc_at_rate(calc, rows, num, float music_rate, float score_goal,
    //                    unsigned int keycount, CalcMode mode)
    // CalcMode: 0 = MSD (uncapped, goal ignored), 1 = SSR (capped, goal applies).
    // Fork C's first wiring dropped the trailing `mode` arg -> stack misaligned ->
    // zeroed Ssr. We want SSR mode (mode = 1).
    [DllImport("MinaCalc", EntryPoint = "calc_ssr", CallingConvention = CallingConvention.Cdecl)]
    private static extern SsrNative CalcSsr(IntPtr calc, NoteInfoNative[] rows, UIntPtr numRows,
        float musicRate, float scoreGoal, uint keycount, int mode);

    private const int CALC_MODE_SSR = 1;

    [DllImport("MinaCalc", EntryPoint = "calc_timeline", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CalcTimeline(IntPtr calc, NoteInfoNative[] rows, UIntPtr numRows, float[] outBuf, int bufCapacity);

    [StructLayout(LayoutKind.Sequential)]
    private struct NoteInfoNative { public uint Notes; public float RowTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SsrNative
    {
        public float Overall, Stream, Jumpstream, Handstream, Stamina, Jackspeed, Chordjack, Technical;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsdAllRatesRaw
    {
        public SsrNative R07, R08, R09, R10, R11, R12, R13, R14, R15, R16, R17, R18, R19, R20;
    }
}
