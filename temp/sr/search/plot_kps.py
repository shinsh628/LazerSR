"""E4 구조 kps 스윕: 실측(바닐라 재현기) + 단순 공식 모델. 3단 small multiples, 축은 패널마다 1개."""
import sys, json, math
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager as fm

fm.fontManager.addfont("/usr/share/fonts/opentype/unifont/unifont.otf")
plt.rcParams["font.family"] = fm.FontProperties(fname="/usr/share/fonts/opentype/unifont/unifont.otf").get_name()

SURF, INK, INK2, GRID = "#fcfcfb", "#0b0b0b", "#52514e", "#e4e3df"
BLUE, ORANGE, AQUA = "#2a78d6", "#eb6834", "#1baf7a"

rows = json.load(open(sys.argv[1]))
e4 = sorted((r for r in rows if r["fam"] == "E4"), key=lambda r: r["kps"])
comp = sorted((r for r in rows if r["fam"] == "COMP"), key=lambda r: r["kps"])

sig = lambda d: 1 / (1 + math.exp(0.27 * (30 - d)))
def model(k):                     # 단순 공식: SR ≈ 0.18·(R/1.2 + H)
    P = 1000 / k
    R = (1000 / P) * (2 * (1 + sig(P / 2)) + 2.5 * (1 + sig(P / 4)))
    H = 2.5 / (1 - 0.125 ** (P / 1000))
    return 0.18 * (R / 1.2 + H)
ks = [4 + i * 0.02 for i in range(int((16.6 - 4) / 0.02))]
ms = [model(k) for k in ks]
dms = [(model(k + 0.01) - model(k - 0.01)) / 0.02 for k in ks]

def slope(rs, w=4):               # 실측 국소 선형회귀 기울기
    out = []
    for i in range(w, len(rs) - w):
        xs = [r["kps"] for r in rs[i - w:i + w + 1]]; ys = [r["sr"] for r in rs[i - w:i + w + 1]]
        mx, my = sum(xs) / len(xs), sum(ys) / len(ys)
        out.append((rs[i]["kps"], sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sum((x - mx) ** 2 for x in xs)))
    return out

fig, axes = plt.subplots(3, 1, figsize=(9, 11), sharex=True, facecolor=SURF,
                         gridspec_kw={"height_ratios": [1.3, 1, 1], "hspace": 0.28})
for ax in axes:
    ax.set_facecolor(SURF)
    ax.grid(True, color=GRID, linewidth=1); ax.set_axisbelow(True)
    for sp in ("top", "right"): ax.spines[sp].set_visible(False)
    for sp in ("left", "bottom"): ax.spines[sp].set_color(GRID)
    ax.tick_params(colors=INK2)
    for x in (6.25, 8.2, 10.4):
        ax.axvline(x, color=INK2, linewidth=1, alpha=0.5)

a = axes[0]
a.plot(ks, ms, color=BLUE, linewidth=2, label="단순 공식 모델")
a.plot([r["kps"] for r in e4], [r["sr"] for r in e4], "o", color=BLUE, markersize=4,
       markeredgecolor=SURF, markeredgewidth=1, label="E4 실측")
a.plot([r["kps"] for r in comp], [r["sr"] for r in comp], "o", color=ORANGE, markersize=4,
       markeredgecolor=SURF, markeredgewidth=1, label="묶음 동시치기형 실측")
a.set_title("SR (kps를 올리면 계속 오름)", color=INK, loc="left", fontsize=14)
a.legend(frameon=False, labelcolor=INK, loc="upper left")

b = axes[1]
sl = slope(e4)
b.plot(ks, dms, color=BLUE, linewidth=2, label="단순 공식 모델")
b.plot([x for x, _ in sl], [y for _, y in sl], "o", color=BLUE, markersize=4,
       markeredgecolor=SURF, markeredgewidth=1, label="E4 실측(국소 기울기)")
b.set_title("kps 1 올릴 때 늘어나는 SR (한계 이득)", color=INK, loc="left", fontsize=14)
b.legend(frameon=False, labelcolor=INK, loc="upper right")
b.annotate("골짜기: 희생 노트의\n어디션이 무너지는 구간", xy=(8.2, 0.5), xytext=(9.0, 1.25), color=INK,
           arrowprops=dict(arrowstyle="-", color=INK2, linewidth=1))

c = axes[2]
c.plot([r["kps"] for r in e4], [r["free_add"] for r in e4], color=AQUA, linewidth=2, label="긴 노트 2개 (홀드어디션)")
c.plot([r["kps"] for r in e4], [r["sac_add"] for r in e4], color=ORANGE, linewidth=2, label="희생 노트 2개 (홀드어디션)")
c.set_title("노트 종류별 홀드어디션 (E4 실측)", color=INK, loc="left", fontsize=14)
c.legend(frameon=False, labelcolor=INK, loc="center right")
c.set_xlabel("열당 kps", color=INK2)
for x, t in ((6.25, "6.25: 희생 노트\n하락 시작"), (8.2, "8.2: 골짜기"), (10.4, "10.4: 긴 노트\n1.0 유지 한계")):
    c.text(x + 0.1, 0.08 if x == 8.2 else 0.30, t, color=INK2, fontsize=10)
fig.savefig(sys.argv[2], dpi=110, bbox_inches="tight", facecolor=SURF)
print("max |model-measured| / measured:",
      round(max(abs(model(r["kps"]) - r["sr"]) / r["sr"] for r in e4), 3))
