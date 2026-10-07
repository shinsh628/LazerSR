"""4K mania 패턴(.osu) 생성기. 모든 시간은 ms 정수."""
import os, sys

OUT = sys.argv[1]
T0, DUR = 2000, 20000          # 핵심 구간 시작, 길이
T_END = T0 + DUR
COLS = 4

def x_of(col):                  # mania: column = floor(x * cols / 512)
    return int(512 * col / COLS + 256 / COLS)

def write(name, notes, lead=True):
    notes = list(notes)
    if lead:
        notes.append((3, 0, 0))             # 리드 탭: 첫 오브젝트 문제 회피
    # 같은 열 겹침 검사 (롱노트 끝 이전에 다음 노트 시작 금지)
    by_col = {}
    for c, s, e in notes:
        by_col.setdefault(c, []).append((s, e))
    for c, lst in by_col.items():
        lst.sort()
        for (s1, e1), (s2, e2) in zip(lst, lst[1:]):
            assert s2 > e1, f"{name}: col{c} overlap {s1}-{e1} / {s2}-{e2}"
    lines = ["osu file format v14", "", "[General]", "Mode: 3", "", "[Difficulty]",
             "HPDrainRate:8", "CircleSize:4", "OverallDifficulty:8", "ApproachRate:5",
             "SliderMultiplier:1.4", "SliderTickRate:1", "", "[TimingPoints]",
             "0,500,4,2,0,100,1,0", "", "[HitObjects]"]
    for c, s, e in sorted(notes, key=lambda n: (n[1], n[0])):
        if e > s:
            lines.append(f"{x_of(c)},192,{s},128,0,{e}:0:0:0:0:")
        else:
            lines.append(f"{x_of(c)},192,{s},1,0,0:0:0:0:")
    with open(os.path.join(OUT, name + ".osu"), "w") as f:
        f.write("\n".join(lines) + "\n")

def roll(cols, spacing, length, t0=T0, t_end=T_END):
    """cols를 순환하며 spacing 간격으로 롱노트(length=0이면 탭)."""
    t, i, out = t0, 0, []
    while t < t_end:
        out.append((cols[i % len(cols)], round(t), round(t + length)))
        t += spacing; i += 1
    return out

def cover(col=0, q_col=3):
    """t0 직전부터 끝까지 이어지는 덮개 + 덮개 시작을 걸쳐주는 Q."""
    return [(col, T0 - 60, T_END + 300), (q_col, T0 - 100, T0 - 30)]

# ---- 기준선 ----
write("00_base_tap32_4col", roll([0, 1, 2, 3], 31.25, 0))
write("01_base_ln32_4col_L80", roll([0, 1, 2, 3], 31.25, 80))
write("02_a_roll42_4col_L80", roll([0, 1, 2, 3], 42, 80))

# ---- b: 덮개 + 3열 롤 ----
for L in (50, 80, 110):
    write(f"10_b_3col_s42_L{L}", cover() + roll([1, 2, 3], 42, L))
for sp in (38, 40, 45, 50):
    write(f"11_b_3col_s{sp}_L80", cover() + roll([1, 2, 3], sp, 80))
write("12_b_3col_s42_L80_noQ_noLead", [(0, T0 - 60, T_END + 300)] + roll([1, 2, 3], 42, 80), lead=False)

# ---- b2: 덮개 + 2열 트릴 ----
for L in (50, 60, 70, 80):
    write(f"20_b2_2col_s42_L{L}", cover() + roll([1, 2], 42, L))
for sp in (38, 40, 45):
    write(f"21_b2_2col_s{sp}_L60", cover() + roll([1, 2], sp, 60))

# ---- 버스트 k=3: 시작 2ms 간격, 릴리즈 42ms 간격, 주기 126 ----
burst = []
s = T0
while s < T_END:
    burst += [(1, s, s + 44), (2, s + 2, s + 86), (3, s + 4, s + 128)]
    s += 126
write("30_burst3_p126", cover() + burst)

# ---- 덮개 두 열 교대 + 슬롯/그림자 (주기 126) ----
def alt(period=126, shadow=True):
    out, B = [], T0
    while B < T_END:
        out += [(0, B, B + 120), (2, B + 2, B + 78), (1, B + 42, B + 162)]
        if shadow:
            out.append((3, B + 4, B + 78))
        B += period
    return out
write("40_alt_cover_p126_shadow", alt())
write("41_alt_cover_p126_noshadow", alt(shadow=False))
