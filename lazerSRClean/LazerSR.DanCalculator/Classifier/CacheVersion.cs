// Port of mania-hub live-backend/src/dan/dan-estimator/cache-version.ts

namespace LazerSR.DanCalculator.Classifier;

public static class CacheVersion
{
    // v15: 4K LN routing handed to leoblack's LN interval table
    // (chart-classifier), with the in-house kNN kept only below that table's LN 5
    // floor, and the LN ladder opened up to its real top of 17 (16 Yokaze, 17
    // Yeehee) instead of clamping labels at 15.
    // v16: localized 4K rice vibro is removed before rating; also LeoBlack's
    // low-band Azusa/Companella fusion.
    // v17: ordinary chart estimates rate every note. Localized vibro adjustment
    // is reserved for player ratings and cached as its own internal chart variant.
    // v18: refresh rate metadata and player variants for accompanied longjacks.
    // v19: refresh repeated-burst coverage and the remaining-material variants.
    // v20: enable marathon correction before Mixed routing and Companella fusion.
    // Only 4K charts with original note spans over five minutes can move.
    public const int DAN_ESTIMATE_CACHE_VERSION = 20;
}
