"""손 친화형 배치 4개를 채보 이미지로 그린다. 시간은 아래→위, 검정 배경 + 흰 직사각형."""
import sys
from PIL import Image, ImageDraw

OUT = sys.argv[1]
T0, T_END = 2000, 22000
WIN_START, WIN_LEN = 3024, 756
SCALE = 1.6
COL_W, GAP, HEADER, MIN_H = 72, 6, 36, 5

def periodic(period, cycle):
    """cycle: 주기 시작 B 기준 (col, start_offset, end_offset) 목록"""
    out, B = [], T0
    while B < T_END:
        out += [(c, B + s, B + e) for c, s, e in cycle]
        B += period
    return out

def roll(cols, spacing, length):
    t, i, out = T0, 0, []
    while t < T_END:
        out.append((cols[i % len(cols)], t, t + length)); t += spacing; i += 1
    return out

patterns = {
    "E1_b2_cover1_trill23": [(0, T0 - 60, T_END + 300)] + roll([1, 2], 42, 60),
    "E2_altcover_2ms": periodic(126, [(0, 0, 120), (1, 2, 78), (2, 40, 78), (3, 42, 162)]),
    "E3_cover4_pair12_single3": [(3, T0 - 60, T_END + 300)] + periodic(126, [(0, 0, 102), (1, 2, 60), (2, 42, 144)]),
    "E4_nested_pairs_no_cover": periodic(168, [(0, 0, 122), (1, 2, 80), (2, 84, 206), (3, 86, 164)]),
}

W, H = 4 * COL_W + 5 * GAP, HEADER + int(WIN_LEN * SCALE)
for name, notes in patterns.items():
    img = Image.new("RGB", (W, H), "black")
    d = ImageDraw.Draw(img)
    d.text((GAP, 10), name, fill=(150, 150, 150))
    for col, s, e in notes:
        lo, hi = max(s, WIN_START), min(max(e, s), WIN_START + WIN_LEN)
        if hi < lo:
            continue
        y_bottom = H - (lo - WIN_START) * SCALE
        y_top = H - (hi - WIN_START) * SCALE
        if y_bottom - y_top < MIN_H:
            y_top = y_bottom - MIN_H
        x0 = GAP + col * (COL_W + GAP)
        d.rectangle([x0, max(y_top, HEADER), x0 + COL_W - 1, y_bottom - 1], fill="white")
    img.save(f"{OUT}/{name}.png")
    print(name)
