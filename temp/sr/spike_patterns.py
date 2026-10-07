"""폭발 배치: E4 준비 + 끝부분 '완성 묶음'(전부 홀드팩터+홀드어디션) + 비용을 폭발 뒤로 미룸.
render: 이미지 / osu: .osu 파일 생성"""
import sys, importlib.util
from PIL import Image, ImageDraw

T0, T_END = 2000, 22000

def e4_pair(t, hand):
    a, b = (0, 1) if hand == 0 else (2, 3)
    return [(a, t, t + 92), (b, t + 2, t + 60)]

def spike_cycle(C, m, col2_tail_gap, extra=False):
    """한 주기 C ms: E4 쌍 m개(0..m-1) → 오른손 '긴 덮개'(R*) → 왼손 폭발 쌍(L*). m은 홀수."""
    notes, B, spikes = [], T0, []
    while B + C <= T_END:
        for k in range(m):
            notes += e4_pair(B + 63 * k, k % 2)
        r = B + 63 * m                       # R*: col2 긴 덮개 + col3 짧은 노트
        s = B + 63 * (m + 1)                 # L*: 폭발 쌍
        a_end = s + 122
        notes += [(2, r, a_end + col2_tail_gap), (3, r + 2, r + 85)]
        notes += [(0, s, a_end), (1, s + 2, s + 77)]
        if extra:   # 폭발 뒤 4열 빈자리: 3열 덮개 안, 다음 주기 시작 전에 끝남
            col2_end = a_end + col2_tail_gap
            notes.append((3, s + 70, min(s + 160, B + C - 2, col2_end - 2)))
        spikes.append(s + 2)
        B += C
    return notes, spikes

PATTERNS = {
    "X1_spike_every800": spike_cycle(800, 9, 60),
    "X2_spike_every400": spike_cycle(400, 3, 46),
    "X3_spike_every1200": spike_cycle(1200, 15, 60),
    "X1x_spike_every800_col4extra": spike_cycle(800, 9, 60, True),
    "X2x_spike_every400_col4extra": spike_cycle(400, 3, 46, True),
    "X3x_spike_every1200_col4extra": spike_cycle(1200, 15, 60, True),
}

def check(name, notes):
    by = {}
    for c, s, e in notes:
        by.setdefault(c, []).append((s, e))
    for c, lst in by.items():
        lst.sort()
        for (s1, e1), (s2, e2) in zip(lst, lst[1:]):
            assert s2 > e1 and s2 - s1 >= 125, f"{name} col{c}: {s1}-{e1} / {s2}-{e2}"

def render(out):
    WIN_START, WIN_LEN, SCALE = 3200, 1600, 1.0
    COL_W, GAP, HEADER, MARK = 72, 6, 36, 16
    W, H = 4 * COL_W + 5 * GAP + MARK, HEADER + int(WIN_LEN * SCALE)
    for name, (notes, spikes) in PATTERNS.items():
        img = Image.new("RGB", (W, H), "black")
        d = ImageDraw.Draw(img)
        d.text((GAP, 10), name, fill=(150, 150, 150))
        y_of = lambda t: H - (t - WIN_START) * SCALE
        for t in range(WIN_START, WIN_START + WIN_LEN + 1, 400):      # 400ms 구간 경계
            d.line([(0, y_of(t)), (W, y_of(t))], fill=(70, 70, 70))
        for col, s, e in notes:
            lo, hi = max(s, WIN_START), min(e, WIN_START + WIN_LEN)
            if hi < lo:
                continue
            yb, yt = y_of(lo), max(y_of(hi), HEADER)
            if yb - 1 <= yt:
                continue
            x0 = GAP + col * (COL_W + GAP)
            d.rectangle([x0, yt, x0 + COL_W - 1, yb - 1], fill="white")
        for t in spikes:                                                # 폭발 지점 표시
            if WIN_START <= t <= WIN_START + WIN_LEN:
                y = y_of(t); x = W - MARK + 2
                d.polygon([(x, y), (x + 12, y - 6), (x + 12, y + 6)], fill=(230, 60, 60))
        img.save(f"{out}/{name}.png")
        print("image", name)

def write_osu(out, gen_path):
    spec = importlib.util.spec_from_file_location("g", gen_path)
    argv = sys.argv; sys.argv = [argv[0], out]
    g = importlib.util.module_from_spec(spec); spec.loader.exec_module(g)
    sys.argv = argv
    for name, (notes, _) in PATTERNS.items():
        g.write(name, notes)
        print("osu", name)

if __name__ == "__main__":
    for name, (notes, _) in PATTERNS.items():
        check(name, notes)
    render(sys.argv[1])
    if len(sys.argv) > 3:
        write_osu(sys.argv[2], sys.argv[3])
