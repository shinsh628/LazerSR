"""바닐라 osu!mania SR(osu.Game.Rulesets.Mania/Difficulty, Version 20241007)의 Python 재현.
대응 파일: ManiaDifficultyCalculator.cs, ManiaDifficultyHitObject.cs, Skills/Strain.cs,
Evaluators/IndividualStrainEvaluator.cs, Evaluators/OverallStrainEvaluator.cs,
Skills/StrainSkill.cs, Skills/StrainDecaySkill.cs (클락레이트 1, 모드 없음 전제)."""
import math

SECTION = 400
DECAY_W = 0.9
MULT = 0.018


def _bigger(a, b):                       # Precision.DefinitelyBigger(a, b, 1)
    return a - 1 > b


def _logistic(x):                        # DiffUtils.Logistic(x, midpointOffset: 30, multiplier: 0.27)
    return 1.0 / (1.0 + math.exp(0.27 * (30 - x)))


def evaluate(notes, cols=4, detail=False):
    """notes: [(col, start, end)] (탭은 end == start). 반환: SR (detail=True면 부가정보 포함)."""
    objs = sorted(notes, key=lambda n: round(n[1]))   # LegacySortHelper: 반올림 시작시각 비교 (동률 없음 전제)
    prev_in_col = [None] * cols                       # 열별 직전 난이도 오브젝트 시작시각
    prev_objs = [None] * cols                         # PreviousHitObjects (열별 마지막 오브젝트 (start, end))
    last_obj = None                                   # 직전 난이도 오브젝트 (col, start, end)

    ind = [0.0] * cols
    highest = 0.0
    overall = 1.0
    current_strain = 0.0

    peaks = []
    section_peak = 0.0
    section_end = None
    prev_start = None
    first = True
    per_note = []

    for i in range(1, len(objs)):
        col, s, e = objs[i]
        delta = s - objs[i - 1][1]
        col_strain_time = s - prev_in_col[col] if prev_in_col[col] is not None else s

        if not first:
            prev_objs[last_obj[0]] = (last_obj[1], last_obj[2])

        # --- StrainSkill.ProcessInternal ---
        if first:
            section_end = math.ceil(s / SECTION) * SECTION
        while s > section_end:
            peaks.append(section_peak)
            off = section_end - prev_start
            section_peak = highest * 0.125 ** (off / 1000) + overall * 0.30 ** (off / 1000)
            section_end += SECTION

        # --- IndividualStrainEvaluator ---
        hold_factor_i = 1.0
        for p in prev_objs:
            if p is None:
                continue
            if _bigger(p[1], e) and _bigger(s, p[0]):
                hold_factor_i = 1.25
                break
        ind_add = 2.0 * hold_factor_i

        # --- OverallStrainEvaluator ---
        overlapping = False
        closest = abs(e - s)
        hold_factor = 1.0
        for p in prev_objs:
            if p is None:
                continue
            overlapping |= _bigger(p[1], s) and _bigger(e, p[1]) and _bigger(s, p[0])
            if _bigger(p[1], e) and _bigger(s, p[0]):
                hold_factor = 1.25
            closest = min(closest, abs(e - p[1]))
        hold_addition = _logistic(closest) if overlapping else 0.0
        ov_add = (1 + hold_addition) * hold_factor

        # --- Strain.StrainValueOf + StrainDecaySkill ---
        ind[col] = ind[col] * 0.125 ** (col_strain_time / 1000) + ind_add
        highest = max(highest, ind[col]) if delta <= 1 else ind[col]
        overall = overall * 0.30 ** (delta / 1000) + ov_add
        current_strain = highest + overall
        section_peak = max(section_peak, current_strain)

        if detail:
            per_note.append((s, col, hold_factor, hold_addition, current_strain))
        prev_in_col[col] = s
        last_obj = (col, s, e)
        prev_start = s
        first = False

    peaks.append(section_peak)
    total, w = 0.0, 1.0
    for p in sorted((p for p in peaks if p > 0), reverse=True):
        total += p * w
        w *= DECAY_W
    sr = total * MULT
    if detail:
        return sr, max(peaks), per_note
    return sr


def load_osu(path):
    """.osu에서 (col, start, end) 목록과 열 수를 읽는다 (mania 전용, 최소 파서)."""
    cols, notes, section = 4, [], None
    for line in open(path, encoding="utf-8-sig"):
        line = line.strip()
        if line.startswith("//"):
            continue
        if line.startswith("["):
            section = line
            continue
        if section == "[Difficulty]" and line.startswith("CircleSize:"):
            cols = int(round(float(line.split(":")[1])))
        if section == "[HitObjects]" and line:
            parts = line.split(",")
            x, t, typ = int(parts[0]), float(parts[2]), int(parts[3])
            col = min(cols - 1, max(0, int(x * cols / 512)))
            end = float(parts[5].split(":")[0]) if typ & 128 else t
            notes.append((col, t, end))
    return notes, cols
