using System;
using System.Globalization;
using System.IO;

namespace LazerSR.Hook.SunnySort;

public enum SunnySortMode
{
    Off,
    NoMod,
    HalfTime,
    DoubleTime,
}

/// <summary>
/// sunny 정렬 위젯의 상태. 정렬 모드 + 범위. 세션 간 유지된다
/// (<c>%LocalAppData%\LazerSR\sunnysort\state</c>, <see cref="LazerSrLeaderboardState"/>와 같은 방식).
/// 캐러셀 패치들과 위젯이 <see cref="Version"/>을 매 프레임 비교해 갱신을 안다.
/// </summary>
public static class SunnySortState
{
    /// <summary>
    /// osu-tree <c>ManiaDifficultyCalculator.Version</c>. 그 값이 바뀌면(계산식 변경) 캐시된 SR이
    /// 전부 무효 — 여기 숫자도 같이 올린다.
    /// </summary>
    public const int CalcVersion = 20241007;

    public const double RangeLower = 0;

    /// <summary>이 값 이상이면 "상한 없음"(∞)으로 취급.</summary>
    public const double RangeUpper = 15;

    private const string folder = "sunnysort";
    private const string file_name = "state";

    private static SunnySortMode activeSort;
    private static double rangeMin = RangeLower;
    private static double rangeMax = RangeUpper;

    static SunnySortState()
    {
        try
        {
            string? text = LazerSrStorage.ReadText(Path.Combine(LazerSrStorage.GetFolder(folder), file_name));
            if (!string.IsNullOrWhiteSpace(text))
            {
                // "sort|min|max"
                var parts = text.Trim().Split('|');
                if (parts.Length == 3)
                {
                    if (Enum.TryParse(parts[0], out SunnySortMode m))
                        activeSort = m;
                    if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lo))
                        rangeMin = Math.Clamp(lo, RangeLower, RangeUpper);
                    if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double hi))
                        rangeMax = Math.Clamp(hi, RangeLower, RangeUpper);
                }
            }
        }
        catch
        {
            // 손상 시 기본값으로 시작.
        }
    }

    public static SunnySortMode ActiveSort
    {
        get => activeSort;
        set
        {
            if (activeSort == value)
                return;

            activeSort = value;
            Version++;
            save();
        }
    }

    public static double RangeMin => rangeMin;
    public static double RangeMax => rangeMax;

    public static void SetRange(double min, double max)
    {
        if (Math.Abs(min - rangeMin) < 1e-6 && Math.Abs(max - rangeMax) < 1e-6)
            return;

        rangeMin = Math.Clamp(min, RangeLower, RangeUpper);
        rangeMax = Math.Clamp(max, RangeLower, RangeUpper);
        Version++;
        save();
    }

    /// <summary>변경마다 증가 — 이벤트 대신 폴링용.</summary>
    public static int Version { get; private set; }

    public static bool SortActive => activeSort != SunnySortMode.Off;

    public static bool RangeActive =>
        rangeMin > RangeLower + 1e-6 || rangeMax < RangeUpper - 1e-6;

    public static double RateFor(SunnySortMode mode) => mode switch
    {
        SunnySortMode.HalfTime => 0.75,
        SunnySortMode.DoubleTime => 1.5,
        _ => 1.0,
    };

    /// <summary>현재 활성 정렬 버튼이 뜻하는 rate (Off면 NoMod = 1.0).</summary>
    public static double ActiveRate =>
        RateFor(activeSort == SunnySortMode.Off ? SunnySortMode.NoMod : activeSort);

    private static void save()
    {
        try
        {
            string text = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", activeSort, rangeMin, rangeMax);
            LazerSrStorage.WriteText(Path.Combine(LazerSrStorage.GetFolder(folder), file_name), text);
        }
        catch
        {
            // 저장 실패는 무시.
        }
    }
}
