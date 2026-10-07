// 바닐라 osu! mania SR 검증 하네스.
// .osu 파일을 공식 디코더로 읽고, 공식 ManiaDifficultyCalculator로 SR을 구한 뒤
// 같은 DifficultyHitObject들을 공식 평가기에 넣어 노트별 보너스 분포를 집계한다.
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mania.Difficulty;
using osu.Game.Rulesets.Mania.Difficulty.Evaluators;
using osu.Game.Rulesets.Mania.Difficulty.Skills;
using osu.Game.Rulesets.Mods;

var ruleset = new ManiaRuleset();
var files = args.SelectMany<string, string>(a => Directory.Exists(a) ? Directory.GetFiles(a, "*.osu").OrderBy(f => f) : new[] { a }).ToList();

Console.WriteLine("name,SR,notes,nps,peakMax,top10Avg,avgOverallMul,pctHF,pctAdd,pctBoth,avgAddition");

foreach (string file in files)
{
    Beatmap decoded;
    using (var stream = File.OpenRead(file))
    using (var reader = new LineBufferedReader(stream))
    {
        var decoder = Decoder.GetDecoder<Beatmap>(reader);
        ((LegacyBeatmapDecoder)decoder).ApplyOffsets = false;
        decoded = decoder.Decode(reader);
    }
    decoded.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

    var working = new FlatWorkingBeatmap(decoded);
    var calc = new ExposedCalculator(ruleset.RulesetInfo, working);
    double sr = calc.Calculate().StarRating;

    // 같은 경로로 플레이어블 비트맵과 난이도 오브젝트를 다시 만들어 상세 집계
    var mods = Array.Empty<Mod>();
    var playable = (ManiaBeatmap)working.GetPlayableBeatmap(ruleset.RulesetInfo, mods);
    var objs = calc.Objects(playable, mods).ToList();

    var strain = new Strain(mods, playable.TotalColumns);
    foreach (var o in objs) strain.Process(o);
    var peaks = strain.GetCurrentStrainPeaks().Where(p => p > 0).OrderDescending().ToList();

    int hf = 0, add = 0, both = 0;
    double overallSum = 0, addSum = 0;
    foreach (var o in objs)
    {
        double overall = OverallStrainEvaluator.EvaluateDifficultyOf(o);
        double holdFactor = IndividualStrainEvaluator.EvaluateDifficultyOf(o) / 2.0;
        double addition = overall / holdFactor - 1;
        bool isHf = holdFactor > 1.0001, isAdd = addition > 0.001;
        if (isHf && isAdd) both++; else if (isHf) hf++; else if (isAdd) add++;
        overallSum += overall;
        addSum += addition;
    }

    int n = objs.Count;
    double span = (objs.Last().StartTime - objs.First().StartTime) / 1000.0;
    Console.WriteLine(string.Join(",",
        Path.GetFileNameWithoutExtension(file), sr.ToString("F4"), n, (n / span).ToString("F2"),
        peaks[0].ToString("F2"), peaks.Take(10).Average().ToString("F2"),
        (overallSum / n).ToString("F3"),
        (100.0 * hf / n).ToString("F1"), (100.0 * add / n).ToString("F1"), (100.0 * both / n).ToString("F1"),
        (addSum / n).ToString("F3")));
}

class ExposedCalculator : ManiaDifficultyCalculator
{
    public ExposedCalculator(osu.Game.Rulesets.IRulesetInfo r, IWorkingBeatmap b) : base(r, b) { }
    public IEnumerable<DifficultyHitObject> Objects(IBeatmap beatmap, Mod[] mods) => CreateDifficultyHitObjects(beatmap, mods);
}
