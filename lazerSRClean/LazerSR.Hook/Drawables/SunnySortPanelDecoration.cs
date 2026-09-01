using System;
using System.Collections.Generic;
using LazerSR.Hook.SunnySort;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Lines;
using osu.Framework.Layout;
using osu.Framework.Utils;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Screens.Select;
using osuTK;
using osuTK.Graphics;

namespace LazerSR.Hook.Drawables;

/// <summary>
/// 캐러셀 패널(<c>PanelBeatmapStandalone</c>)에 붙는 sunny 정렬 장식. 매 프레임:
/// <list type="bullet">
/// <item>SR pill 옆 sunny pill 값/표시 갱신 — 정렬 버튼 ON + 그 rate로 캐시된 맵일 때만.</item>
/// <item>화면에 보이는 패널이고 <c>osu NoMod SR − sunny NoMod SR &gt; 0.5</c>이면, 패널 테두리를
///       따라 시계방향으로 흐르는 금색 혜성 꼬리(둥근 사각형 둘레를 하나의 선처럼 부드럽게 이어짐,
///       머리는 진하고 꼬리로 갈수록 투명).</item>
/// </list>
/// 전부 캐시 조회만 — 재계산 없음.
/// </summary>
public partial class SunnySortPanelDecoration : CompositeDrawable
{
    private const float thickness = 6f;
    private const float revolutions_per_second = 0.4f;
    private const float corner_radius = 10f; // Panel.CORNER_RADIUS
    private const double glow_threshold = 0.5;

    private static readonly Color4 gold = new Color4(255, 198, 66, 255);

    // 혜성 레이어: (꼬리쪽 길이 = 둘레 대비 비율, PathRadius 배수, alpha). 겹쳐서 머리쪽이 진해진다.
    private static readonly (float backFraction, float radiusScale, float alpha)[] layers =
    {
        (0.72f, 1.15f, 0.10f), // 넓고 아주 흐린 글로우 꼬리
        (0.45f, 0.60f, 0.22f),
        (0.22f, 0.60f, 0.45f),
        (0.09f, 0.65f, 1.00f), // 머리
    };

    private readonly Panel panel;
    private readonly StarRatingDisplay pill;

    private readonly Container traceRoot;
    private readonly SmoothPath[] paths;

    private readonly LayoutValue geometry = new LayoutValue(Invalidation.DrawSize);

    private Vector2[] loopPts = Array.Empty<Vector2>();
    private float[] loopCum = Array.Empty<float>();
    private float totalLen;

    private float phase;

    public SunnySortPanelDecoration(Panel panel, StarRatingDisplay pill)
    {
        this.panel = panel;
        this.pill = pill;

        RelativeSizeAxes = Axes.Both;

        paths = new SmoothPath[layers.Length];
        var container = new Container { RelativeSizeAxes = Axes.Both, Alpha = 0f };

        for (int i = 0; i < layers.Length; i++)
        {
            container.Add(paths[i] = new SmoothPath
            {
                AutoSizeAxes = Axes.None,
                RelativeSizeAxes = Axes.Both,
                PathRadius = thickness * 0.5f * layers[i].radiusScale,
                Colour = gold,
                Alpha = layers[i].alpha,
            });
        }

        InternalChild = traceRoot = container;

        AddLayout(geometry);
    }

    protected override void Update()
    {
        base.Update();

        if (!geometry.IsValid)
        {
            rebuildLoop();
            geometry.Validate();
        }

        var beatmap = resolveBeatmap();

        if (beatmap == null || !SunnySortState.SortActive)
        {
            pill.Alpha = 0f;
            traceRoot.Alpha = 0f;
            return;
        }

        updatePill(beatmap);
        updateTrace(beatmap);
    }

    private BeatmapInfo? resolveBeatmap()
        => panel.Item?.Model is GroupedBeatmap gb ? gb.Beatmap : null;

    private void updatePill(BeatmapInfo beatmap)
    {
        if (SunnySortCache.TryGet(beatmap.Hash, SunnySortState.ActiveRate, out double sr))
        {
            pill.Current.Value = new StarDifficulty(sr, 0);
            pill.Alpha = 1f;
        }
        else
        {
            pill.Alpha = 0f;
        }
    }

    private void updateTrace(BeatmapInfo beatmap)
    {
        // 선택 여부와 무관 - 화면에 떠 있는(Item != null) 패널이면 조건만 맞으면 보인다.
        bool glow = SunnySortCache.TryGet(beatmap.Hash, 1.0, out double sunnyNoMod)
                    && (beatmap.StarRating - sunnyNoMod) > glow_threshold;

        traceRoot.Alpha = (float)Interpolation.DampContinuously(traceRoot.Alpha, glow ? 1f : 0f, 45, Time.Elapsed);

        if (traceRoot.Alpha < 0.01f || loopPts.Length < 3 || totalLen <= 1f)
            return;

        phase += (float)(Time.Elapsed / 1000.0) * revolutions_per_second;
        phase -= MathF.Floor(phase);

        float headS = phase * totalLen;

        for (int i = 0; i < paths.Length; i++)
            buildWindow(paths[i], headS, layers[i].backFraction * totalLen);
    }

    private void buildWindow(SmoothPath path, float headS, float backLen)
    {
        path.ClearVertices();

        int samples = Math.Clamp((int)(backLen / 12f) + 4, 6, 40);

        for (int i = 0; i <= samples; i++)
        {
            float s = headS - backLen + backLen * i / samples;
            path.AddVertex(pointAtArcLength(s));
        }
    }

    private Vector2 pointAtArcLength(float s)
    {
        s %= totalLen;
        if (s < 0)
            s += totalLen;

        int n = loopPts.Length;

        for (int i = 1; i < n; i++)
        {
            if (loopCum[i] >= s)
            {
                float seg = loopCum[i] - loopCum[i - 1];
                float f = seg <= 1e-4f ? 0f : (s - loopCum[i - 1]) / seg;
                return Vector2.Lerp(loopPts[i - 1], loopPts[i], f);
            }
        }

        // 마지막 정점 → 첫 정점(닫는 구간)
        float startS = loopCum[n - 1];
        float closeSeg = totalLen - startS;
        float ff = closeSeg <= 1e-4f ? 0f : (s - startS) / closeSeg;
        return Vector2.Lerp(loopPts[n - 1], loopPts[0], ff);
    }

    private void rebuildLoop()
    {
        float w = DrawWidth, h = DrawHeight;
        float inset = thickness * 0.5f + 0.5f;
        float r = MathF.Max(1f, corner_radius - inset);
        float x0 = inset, y0 = inset, x1 = w - inset, y1 = h - inset;

        if (x1 - x0 < 2 * r + 1 || y1 - y0 < 2 * r + 1)
        {
            loopPts = Array.Empty<Vector2>();
            totalLen = 0f;
            return;
        }

        const int arc_steps = 6;
        var pts = new List<Vector2>(4 + 4 * arc_steps + 4);

        pts.Add(new Vector2(x0 + r, y0));                                   // 상단 시작
        pts.Add(new Vector2(x1 - r, y0));                                   // 상단 끝
        addArc(pts, new Vector2(x1 - r, y0 + r), r, -MathF.PI / 2, 0f, arc_steps); // 우상
        pts.Add(new Vector2(x1, y1 - r));                                   // 우측 끝
        addArc(pts, new Vector2(x1 - r, y1 - r), r, 0f, MathF.PI / 2, arc_steps);  // 우하
        pts.Add(new Vector2(x0 + r, y1));                                   // 하단 끝
        addArc(pts, new Vector2(x0 + r, y1 - r), r, MathF.PI / 2, MathF.PI, arc_steps); // 좌하
        pts.Add(new Vector2(x0, y0 + r));                                   // 좌측 끝
        addArc(pts, new Vector2(x0 + r, y0 + r), r, MathF.PI, 3 * MathF.PI / 2, arc_steps); // 좌상 → 시작점으로

        loopPts = pts.ToArray();
        loopCum = new float[loopPts.Length];

        float acc = 0f;
        for (int i = 1; i < loopPts.Length; i++)
        {
            acc += Vector2.Distance(loopPts[i - 1], loopPts[i]);
            loopCum[i] = acc;
        }

        totalLen = acc + Vector2.Distance(loopPts[^1], loopPts[0]);
    }

    private static void addArc(List<Vector2> pts, Vector2 centre, float r, float a0, float a1, int steps)
    {
        for (int i = 1; i <= steps; i++)
        {
            float a = a0 + (a1 - a0) * i / steps;
            pts.Add(centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r);
        }
    }
}
