"""'릴리즈 간격 예산' 설명용 그림. 노트 색 = 실제 계산된 홀드어디션 값 (Python 재현기)."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from PIL import Image, ImageDraw, ImageFont
from mania_sr import evaluate

OUT = sys.argv[1]
FONT = ImageFont.truetype("/usr/share/fonts/opentype/unifont/unifont.otf", 16)
SMALL = ImageFont.truetype("/usr/share/fonts/opentype/unifont/unifont.otf", 14)
GREEN, ORANGE, GRAY = (90, 220, 120), (245, 150, 60), (110, 110, 110)
CW, GAP = 56, 6

def color_of(add):
    return GREEN if add > 0.9 else ORANGE

def per_note_values(notes):
    """(col, start) -> (holdFactor, holdAddition)"""
    _, _, per = evaluate(notes, detail=True)
    return {(c, s): (hf, ha) for s, c, hf, ha, _ in per}

def draw_panel(img, x_off, notes, vals, t0, t1, scale, header, title, show_gaps=True, note_labels=None, neutral=()):
    d = ImageDraw.Draw(img)
    H = img.height
    y = lambda t: H - 20 - (t - t0) * scale
    d.text((x_off, 8), title, font=FONT, fill="white")
    vis = [(c, s, e) for c, s, e in notes if e > t0 and s < t1]
    for c, s, e in vis:
        hf, ha = vals.get((c, s), (1.0, 0.0))
        x0 = x_off + GAP + c * (CW + GAP)
        yt, yb = max(y(min(e, t1)), header), y(max(s, t0))
        if yb - 1 <= yt:
            continue
        d.rectangle([x0, yt, x0 + CW - 1, yb - 1], fill=(170, 170, 170) if (c, s) in neutral else color_of(ha))
        if hf > 1.0:                                   # 홀드팩터 표시: 테두리
            d.rectangle([x0 + 3, yt + 3, x0 + CW - 4, yb - 4], outline="black", width=3)
        label = note_labels.get((c, s)) if note_labels else f"{ha:.2f}"
        if label and yb - yt > 22:
            d.text((x0 + 4, yb - 20), label, font=SMALL, fill="black")
    if not show_gaps:
        return
    # 릴리즈 선 + 간격 + 누가 부담하는지
    rel = sorted((e, c, s) for c, s, e in vis if t0 < e < t1)
    gx = x_off + 4 * (CW + GAP) + 10
    for e, c, s in rel:
        d.line([(x_off + GAP + c * (CW + GAP), y(e)), (gx, y(e))], fill=GRAY, width=1)
    for (e1, c1, s1), (e2, c2, s2) in zip(rel, rel[1:]):
        g = e2 - e1
        later = (c1, s1, e1) if s1 > s2 else (c2, s2, e2)
        earlier = (c2, s2, e2) if later[0] == c1 and later[1] == s1 else (c1, s1, e1)
        overlap = later[1] < earlier[2]                 # 나중 노트 시작 시점에 앞 노트가 눌려 있나
        ym = (y(e1) + y(e2)) / 2
        d.line([(gx, y(e1)), (gx, y(e2))], fill="white", width=2)
        txt = f"{g:.0f}ms → {later[0] + 1}열 부담" if overlap and g < 45 else f"{g:.0f}ms (충분히 멂)"
        d.text((gx + 8, ym - 8), txt, font=SMALL, fill=ORANGE if overlap and g < 45 else GRAY)

def legend(img, x, y0):
    d = ImageDraw.Draw(img)
    d.rectangle([x, y0, x + 16, y0 + 16], fill=GREEN); d.text((x + 22, y0), "홀드어디션 ≈1.0", font=SMALL, fill="white")
    d.rectangle([x + 170, y0, x + 186, y0 + 16], fill=ORANGE); d.text((x + 192, y0), "깎임", font=SMALL, fill="white")
    d.rectangle([x + 250, y0, x + 266, y0 + 16], fill="white"); d.rectangle([x + 252, y0 + 2, x + 264, y0 + 14], outline="black", width=3)
    d.text((x + 272, y0), "검은 테두리 = 홀드팩터", font=SMALL, fill="white")

def periodic(P, cyc, n=12, base=1000):
    out = [(3, 0, 0)]
    for k in range(n):
        out += [(c, base + P * k + s, base + P * k + e) for c, s, e in cyc]
    return out

# ---------- 그림 1: 규칙 하나 ----------
W1, H1 = 2 * 640, 520
img = Image.new("RGB", (W1, H1), "black")
for i, (b_end, title) in enumerate([(120, "(가) B의 릴리즈가 A와 20ms"), (150, "(나) B의 릴리즈가 A와 50ms")]):
    notes = [(3, 0, 0), (0, 1000, 1100), (1, 1020, 1000 + b_end)]
    vals = per_note_values(notes)
    labels = {(0, 1000): "A", (1, 1020): f"B {vals[(1, 1020)][1]:.2f}"}
    draw_panel(img, i * 640 + 10, notes, vals, 980, 1180, 2.2, 40, title, note_labels=labels, neutral={(0, 1000)})
    d = ImageDraw.Draw(img)
    ty = 70
    for line in ["A: 먼저 시작.", "   시작할 때 B가 없으니", "   B와의 거리는 안 봄", "",
                 "B: 나중 시작.", "   시작할 때 A가 눌려 있음", f"   → A 릴리즈와 거리 {b_end - 100}ms",
                 f"   → 홀드어디션 {vals[(1, 1020)][1]:.2f}"]:
        d.text((i * 640 + 430, ty), line, font=SMALL, fill="white"); ty += 20
img.save(f"{OUT}/1_rule_two_notes.png")

# ---------- 그림 2, 3: 롤 vs E4 ----------
ROLL = (126, [(0, 0, 80), (1, 31, 111), (2, 63, 143), (3, 94, 174)])
E4 = (126, [(0, 0, 92), (1, 2, 60), (2, 63, 155), (3, 65, 123)])
for fname, (P, cyc), title in [("2_roll.png", ROLL, "4열 롤: 모두가 간격 1개씩 부담"),
                               ("3_E4.png", E4, "E4: 긴 노트는 부담 0, 짧은 노트가 2개씩 부담")]:
    notes = periodic(P, cyc)
    vals = per_note_values(notes)
    t0 = 1000 + P * 6
    W, H = 4 * (CW + GAP) + 330, 40 + int(252 * 2.6) + 40
    img = Image.new("RGB", (W, H), "black")
    draw_panel(img, 4, notes, vals, t0, t0 + 252, 2.6, 60, title)
    legend(img, 10, 32)
    img.save(f"{OUT}/{fname}")
    s = sorted({round(v[1], 3) for k, v in vals.items() if k[1] > 2000})
    print(fname, "holdAddition values:", s)
print("ok")
