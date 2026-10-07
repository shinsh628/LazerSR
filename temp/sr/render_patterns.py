"""대표 패턴 4개를 채보 형태 이미지로 그린다. 시간은 아래→위, 검정 배경 + 흰 직사각형."""
import sys
from PIL import Image, ImageDraw

OUT = sys.argv[1]
T0, T_END = 2000, 22000
WIN_START, WIN_LEN = 3000, 756          # 보여줄 구간 (126ms × 6)
SCALE = 1.6                              # px per ms
COL_W, GAP, HEADER = 72, 6, 36
MIN_H = 5                                # 아주 짧은 노트도 보이게

def roll(cols, spacing, length):
    t, i, out = T0, 0, []
    while t < T_END:
        out.append((cols[i % len(cols)], t, t + length)); t += spacing; i += 1
    return out

def alt(shadow):
    out, B = [], T0
    while B < T_END:
        out += [(0, B, B + 120), (1, B + 2, B + 78), (3, B + 42, B + 162)]   # 덮개 1·4열
        if shadow:
            out.append((2, B + 4, B + 78))
        B += 126
    return out

cover = [(0, T0 - 60, T_END + 300)]
patterns = {
    "1_b_cover+3col_roll":        cover + roll([1, 2, 3], 42, 80),
    "2_b2_cover+2col_trill":      cover + roll([1, 2], 42, 60),
    "3_alt_cover_with_shadow":    alt(True),
    "4_alt_cover_no_shadow":      alt(False),
}

W = 4 * COL_W + 5 * GAP
H = HEADER + int(WIN_LEN * SCALE)
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
    print(name, img.size)
