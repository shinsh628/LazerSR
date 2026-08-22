using System;
using System.Linq;

namespace LazerSR.SunnyCalculator.Tuning;

/// <summary>
/// Fits a personal step to one player's own accuracy records, against Jacobians baked once at the
/// universal point (<see cref="PersonalJacobianBaker"/>). Since SR is linear in the step near that
/// point, the whole fit is a single ridge-regularised 13x13 linear solve - no sunny call involved.
/// Port of <c>OsuScoreModel/dev/sunnyplus/personal_fit.py</c>'s <c>solve()</c>; see that file for the
/// derivation and the reasoning behind the ridge.
/// </summary>
public static class PersonalFitSolver
{
    /// <summary>
    /// Fixed client-side ridge, chosen offline by 5-fold cross-validation (handover_lazersr.md §3). The
    /// client never re-picks it.
    /// <para>
    /// 2026-08-20/21: briefly capped the <c>n</c> fed into the penalty below at 100, on the theory that
    /// growing the queue past the size the offline CV assumed would over-shrink. That was wrong - <c>xtx</c>
    /// (the data term) also scales with n, so the original <c>ridge * n</c> term was already keeping
    /// shrinkage strength constant relative to the data regardless of n. Capping only the penalty side
    /// broke that balance and made regularisation ~4x weaker once the two-pool redesign pushed n from
    /// ~100 to ~400 - confirmed by three of eleven UnitStep components landing exactly on the +-0.5 clamp.
    /// Reverted; the real, uncapped n is correct here.
    /// </para>
    /// </summary>
    public const double Ridge = 0.01;

    /// <summary>
    /// <paramref name="unitStep"/> is in unit space (box half-width = 1 unit), already clipped to
    /// [-0.5, 0.5] so the fit never claims a value outside where the Jacobian was measured.
    /// <paramref name="alpha"/>/<paramref name="beta"/> are the profiled-out nuisance terms of
    /// y ~= alpha + beta * SR - kept here (unlike the offline tool, which discards them) because the
    /// widget's "accuracy 95% at what SR" figure is exactly their inverse.
    /// </summary>
    public record Result(double[] UnitStep, double Alpha, double Beta);

    /// <summary>
    /// y[n] = -log(1 - accuracy) for each queued item, sr0[n] = its SR at the universal point,
    /// jac[n] = its unit-space Jacobian (length <see cref="PersonalBox.Tuned"/>.Length). All three
    /// arrays must be the same length and index the same items.
    /// <para>
    /// alpha/beta and the 11 personal deltas are fit in two fully decoupled stages, not jointly -
    /// 2026-08-21: jointly solving a single 13-dim ridge system (jac columns penalised, alpha/beta not)
    /// let beta get inflated whenever a jac column correlated with sr0, since the unpenalised beta ends
    /// up absorbing variance the penalised, correlated column "should" have explained. Orthogonalizing
    /// the jac columns against [1, sr0] first (Frisch-Waugh-Lovell) fixed that for alpha/beta - but the
    /// resulting per-dimension coefficients are then only valid when dotted with orthogonalized jac, and
    /// every consumer of <see cref="ToRealDeltas"/> dots them with a chart's raw (non-orthogonalized)
    /// Jacobian instead (a fixed real-unit constant delta can't know a chart's sr0 to detrend by). Applied
    /// that way, the orthogonalized coefficients reintroduced almost the same bias one level down: nearly
    /// every chart in the player's own pool came out personally *easier* than universal (354 of 355,
    /// mean shift -0.70 SR) - not personalisation, just the mean of each jac column leaking back in.
    /// </para>
    /// <para>
    /// Fixing beta from the plain two-variable fit (sr0 alone) *before* looking at jac at all, then
    /// ridge-regressing the residual (y minus that fixed alpha/beta line) directly against the raw jac
    /// columns, avoids both problems at once: beta can never be contaminated (it's computed with no jac
    /// term in scope to begin with), and the per-dimension coefficients are fit and applied against the
    /// same, un-detrended jac basis, so there is no basis mismatch to leak a mean offset back in (same
    /// real data: mean shift -0.70 -> -0.01, split 354/1 -> 217/138).
    /// </para>
    /// <para>
    /// 2026-08-22: tried, then reverted, scaling each of the 11 jac columns to unit variance before
    /// penalising Stage 2 (to fix <c>MixFirst</c>/<c>PressingWeight</c> anti-correlating at r=-0.89 and
    /// the raw-jac fit dumping nearly all of that shared credit onto <c>MixFirst</c> alone). The rescaling
    /// idea itself was right - a held-out 5-fold CV against 398 real records did show it generalises
    /// better (93/100 folds lower error) - but shipping it with the *same* <see cref="Ridge"/> constant was
    /// wrong: that constant was chosen (offline, 5-fold CV) against raw-jac scale, and several columns have
    /// small natural variance (e.g. <c>ChordScale</c>, <c>UnevennessHighThreshold</c>), so dividing by their
    /// std left the same penalty far too weak for them - the *unclamped* solution wanted deltas up to 6x the
    /// personal box (confirmed on real data: <c>ChordScale</c> unit-step wanted +3.13 against a box that
    /// only trusts up to +-0.5). Clamping those back into the box is what the box is *for*, but clamping is
    /// nonlinear and broke an otherwise-exact cancellation between the columns' means and their fitted
    /// coefficients that Stage 1's alpha already fully owns - the leftover leaked back in as a uniform,
    /// chart-independent shift (confirmed: 350/350 baked charts came out personally *easier*,
    /// mean -0.54 SR, vs raw-jac's 254/398, mean -0.01) - not personalisation, every chart moved the same
    /// direction regardless of its own pattern. Reverted to raw-jac Stage 2 below; a properly re-tuned
    /// ridge for the rescaled objective is a separate follow-up, not something to rush back in.
    /// </para>
    /// </summary>
    public static Result Solve(double[] y, double[] sr0, double[][] jac, double ridge = Ridge)
    {
        int n = y.Length;
        int tunedCount = PersonalBox.Tuned.Length;

        if (n == 0)
            return new Result(new double[tunedCount], 0.0, 0.0);

        // Stage 1: alpha/beta from sr0 alone - no jac term in scope, so nothing downstream can bias it.
        double srMean = sr0.Average();
        double yMean = y.Average();
        double sxy = 0.0, sxx = 0.0;

        for (int k = 0; k < n; k++)
        {
            sxy += (sr0[k] - srMean) * (y[k] - yMean);
            sxx += (sr0[k] - srMean) * (sr0[k] - srMean);
        }

        if (sxx < 1e-12)
            return new Result(new double[tunedCount], yMean, 0.0);

        double beta = sxy / sxx;
        double alpha = yMean - beta * srMean;

        // A player whose accuracy doesn't track difficulty at all - nothing to personalise, and
        // dividing by beta below would blow up.
        if (!double.IsFinite(beta) || Math.Abs(beta) < 1e-9)
            return new Result(new double[tunedCount], alpha, beta);

        // Stage 2: ridge-regress the residual (what alpha/beta alone can't explain) against the raw
        // per-chart Jacobian - same basis ToRealDeltas' consumers will dot the resulting deltas against.
        var yResid = new double[n];
        for (int k = 0; k < n; k++)
            yResid[k] = y[k] - alpha - beta * sr0[k];

        var jtj = new double[tunedCount, tunedCount];
        var jty = new double[tunedCount];

        for (int k = 0; k < n; k++)
        {
            for (int i = 0; i < tunedCount; i++)
            {
                jty[i] += jac[k][i] * yResid[k];
                for (int j = 0; j < tunedCount; j++)
                    jtj[i, j] += jac[k][i] * jac[k][j];
            }
        }

        double penalty = ridge * n / (beta * beta);
        for (int c = 0; c < tunedCount; c++)
            jtj[c, c] += penalty;

        double[]? gamma = linearSolve(jtj, jty);
        if (gamma == null)
            return new Result(new double[tunedCount], alpha, beta);

        var unitStep = new double[tunedCount];
        for (int c = 0; c < tunedCount; c++)
        {
            double d = gamma[c] / beta;
            unitStep[c] = double.IsFinite(d) ? Math.Clamp(d, -0.5, 0.5) : 0.0;
        }

        return new Result(unitStep, alpha, beta);
    }

    /// <summary>unit-space step -> delta in real constant units, for <see cref="PersonalDiff"/>.</summary>
    public static double[] ToRealDeltas(double[] unitStep)
    {
        var deltas = new double[SunnyConstants.Count];

        for (int c = 0; c < PersonalBox.Tuned.Length; c++)
            deltas[PersonalBox.Index[c]] = unitStep[c] * PersonalBox.RealWidth(c);

        return deltas;
    }

    /// <summary>Gaussian elimination with partial pivoting. Null if the system is (near-)singular.</summary>
    private static double[]? linearSolve(double[,] a, double[] b)
    {
        int dim = b.Length;
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();

        for (int col = 0; col < dim; col++)
        {
            int pivotRow = col;
            double pivotValue = Math.Abs(m[col, col]);

            for (int row = col + 1; row < dim; row++)
            {
                double candidate = Math.Abs(m[row, col]);
                if (candidate > pivotValue)
                {
                    pivotValue = candidate;
                    pivotRow = row;
                }
            }

            if (pivotValue < 1e-12)
                return null;

            if (pivotRow != col)
            {
                for (int c = 0; c < dim; c++)
                    (m[col, c], m[pivotRow, c]) = (m[pivotRow, c], m[col, c]);

                (v[col], v[pivotRow]) = (v[pivotRow], v[col]);
            }

            double pivot = m[col, col];

            for (int row = col + 1; row < dim; row++)
            {
                double factor = m[row, col] / pivot;
                if (factor == 0.0) continue;

                for (int c = col; c < dim; c++)
                    m[row, c] -= factor * m[col, c];

                v[row] -= factor * v[col];
            }
        }

        var x = new double[dim];

        for (int row = dim - 1; row >= 0; row--)
        {
            double sum = v[row];
            for (int c = row + 1; c < dim; c++)
                sum -= m[row, c] * x[c];

            x[row] = sum / m[row, row];
        }

        return x;
    }
}
