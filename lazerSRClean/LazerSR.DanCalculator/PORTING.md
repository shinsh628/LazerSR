# LazerSR.DanCalculator — port of mania-hub's dan classifier

Faithful C# port of mania-hub's production dan classification pipeline
(`live-backend/src/dan/**` + vendored `live-backend/vendor/leoblack/**`),
minus LeoBlack's sunny algorithm — we substitute our own `LazerSR.SunnyCalculator`.

## Source locations (read-only)

- mania-hub own layer: `C:\dev\lazerSR\temp\mania-hub\live-backend\src\dan\`
- vendored LeoBlack:    `C:\dev\lazerSR\temp\mania-hub\live-backend\vendor\leoblack\`
- LeoBlack standalone (identical commit a36a21b, has `.d.ts` gone / cleaner tree):
  `C:\dev\lazerSR\temp\osumania_map_analyser\ManiaMapAnalyser by Leo_Black\js\`
  Use the vendored copy as the source of truth (mania-hub applied local patches —
  see `vendor/leoblack/PORT_NOTES.md`), but the standalone tree is handy for context.

## Entry point contract (what the finished port exposes)

```csharp
namespace LazerSR.DanCalculator;
public static class DanClassifier
{
    // sync path — no Companella (ONNX). matches chart-classifier.ts classifyChart()
    public static ChartClassification ClassifyChart(string osuText, ClassifyChartInput input);
    // async path — runs Companella when the sync verdict asked for it
    public static Task<ChartClassification> ClassifyChartWithCompanellaAsync(string osuText, ClassifyChartInput input);
}
```
`osuText` is the raw `.osu` file text. The Hook layer produces it from an
`osu.Game` `IBeatmap` (via `LegacyBeatmapEncoder` or the realm file). The ported
code below `LazerSR.DanCalculator` must NEVER reference `osu.Game`.

## Sunny substitution — READ THIS

Wherever LeoBlack JS calls `runSunnyEstimatorFromText` / `calculate` from
`rework/sunnyAlgorithm.js`, call our shim instead:

```csharp
namespace LazerSR.DanCalculator.Estimators;
public static class SunnyShim
{
    // returns { star, lnRatio, columnCount, estDiff, graph? } shaped like
    // reworkEstimatorUtils.normalizeReworkResult + sunnyEstimator.js output.
    public static SunnyResult Run(string osuText, double speedRate = 1.0, double? odFlag = null,
                                  string? cvtFlag = null, bool withGraph = false);
}
```
Implementation (owned by the classifier-integration agent, NOT the estimator agents):
- `star` — `LazerSR.SunnyCalculator` vanilla value. Parse `osuText` -> osu.Game
  `IBeatmap` in the Hook glue, or (preferred, keeps the port pure) port a minimal
  `.osu`->notes path and call the calc's `IBeatmap`-free entry. Force vanilla:
  `SunnyConstants.WithIsolatedDiff(new double[SunnyConstants.Count], forceVanillaTail:true, () => ...)`.
- `lnRatio` — hold-note-count / total-note-count from the parsed chart.
- `columnCount` — key count.
- `estDiff` — computed by the ported `ReworkEstimatorUtils.EstDiff(star, lnRatio, columnCount, ...)`.
- `graph` — from our `GetStrainTimeline` if `withGraph`, adapted to `{ times[], values[] }`.

Estimator agents: assume `SunnyShim.Run(...)` exists and returns `SunnyResult`.
Define `SunnyResult` in `Estimators/SunnyResult.cs` (fields: `Star`, `LnRatio`,
`ColumnCount` (double), `EstDiff` (string), `Graph` (`(double[] Times, double[] Values)?`),
`NumericDifficulty` (double?), `NumericDifficultyHint` (string?)).

## MSD substitution

LeoBlack's `ett/**` (MinaCalc WASM) is NOT ported. `msd.ts` becomes a thin adapter
over an in-project MinaCalc P/Invoke (port `LazerSR.Hook/Calculators/MsdCalculator.cs`
into `Msd/MinaCalcNative.cs`, dropping the osu.Game `IBeatmap` dependency — feed it
note rows built from our parsed chart). Output shape must match what `msd.ts`
consumers expect: `MsdResult { string EtternaVersion; Dictionary<string,double> Values; ... }`
with skillset keys `overall/stream/jumpstream/handstream/stamina/jackspeed/chordjack/technical`.
Owned by the classifier-integration agent.

## JS -> C# conventions (follow exactly)

| JS | C# |
|---|---|
| `number` | `double` (always, even "ints" — indices stay `int`) |
| `x ?? y` | `x ?? y`; `a?.b` | `a?.b` |
| `[...arr]` | `arr.ToArray()` / `new List<T>(arr)` |
| `arr.at(-1)` | `arr[^1]` |
| `Math.min(...xs)` on array | manual loop (JS empty -> +Infinity; preserve) |
| `Array<[number, T[]]>` | `List<(double, List<T>)>` |
| `Record<string, number>` | `Dictionary<string, double>` |
| `Map<K,V>` | `Dictionary<K,V>` |
| object literal returned | small `record` or `sealed class` in the same file |
| `for...of entries()` | `foreach` over `.ToArray()` when mutating during iterate |
| `str.match(re)` | `Regex.Match`; precompile static `Regex` |
| `JSON`/`structuredClone` | manual clone / `record with` |
| bitwise on possibly-float | cast to `int`/`long` first (JS coerces to int32) |
| `**` | `Math.Pow` |
| `Math.log2` `Math.log1p` `Math.cbrt` `Math.hypot` `Math.sign` `Math.trunc` | `Math.Log2`, `Math.Log(1+x)`, `Math.Cbrt`, `double.Hypot`, `Math.Sign`, `Math.Truncate` |
| `parseFloat`/`parseInt` leading-number semantics | use the `ParseFloatJs`/`ParseIntJs` pattern from `Beatmap/ManiaBeatmap.cs` |
| division by zero | JS gives `Infinity`/`NaN`; C# double does too — do NOT add guards the source lacks |
| array index OOB | JS gives `undefined`; port must guard (source often relies on `?? 0`) |

- **Faithfulness over idiom.** Keep function names (PascalCase), keep the same
  branching, keep magic numbers inline with the source's comment. One TS file ->
  one C# file where practical; name it after the source.
- Keep every code comment from the source (translate long Korean-free English
  comments verbatim). They encode calibration rationale.
- No `osu.Game` imports below the project root. No network, no file writes.
- Deterministic: no `DateTime.Now`, no unseeded RNG (source uses none).
- Nullable enabled. Prefer `sealed class` for the many small DTOs; `record` for
  pure value bags.

## Fixed foundation (already ported — DO NOT modify, just consume)

| C# | from |
|---|---|
| `LazerSR.DanCalculator.Beatmap.ManiaBeatmap` / `ManiaNote` / `ManiaBeatmapParser.Parse` | `beatmap-parser.ts` |
| `LazerSR.DanCalculator.Features.DanMath` | `dan-estimator/math.ts` |
| `LazerSR.DanCalculator.Types.*` (`DanFeatureMetrics`, `DanEstimate`, `ManiaPatternAnalysis`, `SkillScores`, `DanSkillFamily`, `ManiaPatternId`, `DanEstimateInput`, `DanFeatureExtractionResult`, ...) | `dan-estimator/types.ts` |

If a consumer needs a type not in `Types/`, add it to `Types/DanTypes.cs` (or a new
`Types/*.cs`) — never redefine an existing one.

## Directory / namespace layout

```
Beatmap/      ManiaBeatmap (mania-hub parser)            [DONE]
Features/     DanMath [DONE], DanFeatures, DanPatterns (dan-estimator/features.ts, patterns.ts), MotionFeatures, JackDemand, NoteBpm
Types/        DanTypes [DONE] + extras
Parser/       OsuFileParser, PatternOsuParser, NoteColumn        (LeoBlack js/parser/)
Intervals/    Rc4K/Ln4K/.../DanIndex                             (LeoBlack js/estimator/intervals/)
Interlude/    ChartBuilder, Strain, NoteDifficulty, Difficulty, Variety, Layout, NumberUtils, Types, Index  (LeoBlack js/interlude/)
Patterns/     PatternsDef, Clustering, Primitives, FindPatterns, Config, Categorise, Chart, Summary, Service  (LeoBlack js/patterns/)
Estimators/   SunnyResult, SunnyShim*, ReworkMathCore, ReworkEstimatorUtils, RcDifficultyFormat, DanielAlgorithm, DanielEstimator,
              AzusaEstimator, RoxyEstimator, RoxyMetaModel, MarathonCorrection, MixedEstimator, CompanellaEstimator
Vibro/        VibroDetection, VibroSections, VibroClearEvidence, LongjackVibro (LeoBlack js/app/vibro.js)
Classifier/   ChartClassification, ClassifyChartInput, ChartClassifier, LeoBlackEstimator, DanEligibility, InvertMod, Labels, LnDan, Companella
Credit/       DanCredit                                          (dan-credit.ts)
Msd/          MinaCalcNative, Msd
Benchmark/    Scoring, FamilyChoice, DanEstimator, Courses        (the deprecated estimateDan path — port for parity, not wired into ClassifyChart)
Assets/       dan_model.onnx
```
`*` SunnyShim body is the integration agent's; estimator agents only rely on its signature.

## Verification

Green build is only expected after integration. Each agent:
1. Re-reads its source files fully before and after porting.
2. Self-reviews the diff against the source line-by-line for logic drift.
3. Notes any place C#/JS semantics forced a deviation, in a `// PORT NOTE:` comment.
4. Leaves stub `throw new NotImplementedException("depends on <X> — <agent>")` only
   for genuinely cross-agent gaps, and lists them in its final report.
