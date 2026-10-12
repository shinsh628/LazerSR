"""7K 기법 설명 그림: 핫 컬럼, 대형 묶음, 브리지. 수치는 판정 규칙/감쇠식을 직접 적용한 모델값 (SR 실측 아님)."""
import sys, math
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager as fm
from matplotlib.patches import Rectangle

FONT = "/usr/share/fonts/opentype/unifont/unifont.otf"
fm.fontManager.addfont(FONT)
plt.rcParams["font.family"] = fm.FontProperties(fname=FONT).get_name()
OUT = sys.argv[1]

SURF, INK, INK2, GRID = "#fcfcfb", "#0b0b0b", "#52514e", "#e4e3df"
GREEN, ORANGE, RED, GRAYN, BLUE = "#1baf7a", "#eb6834", "#d03b3b", "#b9b8b3", "#2a78d6"
sig = lambda d: 1 / (1 + math.exp(0.27 * (30 - d)))
big = lambda a, b: a - 1 > b                      # Precision.DefinitelyBigger(a, b, 1)


def judge(notes, cols):
    """notes: [(col, start, end)] → {(col, start): (holdFactor, holdAddition)}. OverallStrainEvaluator 규칙 그대로."""
    out, prev = {}, [None] * cols
    objs = sorted(notes, key=lambda n: round(n[1]))
    for i, (c, s, e) in enumerate(objs):
        if i > 0:                                  # 첫 오브젝트는 판정 대상도, 이전 목록도 아님
            hf, ov, closest = 1.0, False, abs(e - s)
            for p in prev:
                if p is None:
                    continue
                ps, pe = p
                ov |= big(pe, s) and big(e, pe) and big(s, ps)
                if big(pe, e) and big(s, ps):
                    hf = 1.25
                closest = min(closest, abs(e - pe))
            out[(c, s)] = (hf, sig(closest) if ov else 0.0)
        prev[c] = (s, e)
    return out


def style(ax):
    ax.set_facecolor(SURF)
    for sp in ax.spines.values():
        sp.set_color(GRID)
    ax.tick_params(colors=INK2)


def lanes(ax, notes, cols, t0, t1, vals=None, title="", names=None, cover_col=None, label_a=True):
    style(ax)
    for c, s, e in notes:
        if e < t0 or s > t1:
            continue
        if c == cover_col:
            col = GRAYN
        elif vals is None:
            col = BLUE
        else:
            a = vals.get((c, s), (1, 0))[1]
            col = GREEN if a > 0.9 else ORANGE if a > 0.3 else RED
        ys, ye = max(s, t0), min(e, t1)
        ax.add_patch(Rectangle((c + 0.08, ys), 0.84, max(ye - ys, 3), color=col, lw=0))
        if vals is not None and label_a and c != cover_col and (c, s) in vals and ye - ys > 18 and s >= t0:
            ax.text(c + 0.5, ys + 4, f"{vals[(c, s)][1]:.2f}", ha="center", va="bottom", fontsize=10, color="white")
    ax.set_xlim(0, cols); ax.set_ylim(t0, t1)
    ax.set_xticks([i + 0.5 for i in range(cols)])
    ax.set_xticklabels(names or [f"{i + 1}" for i in range(cols)], fontsize=10)
    ax.set_ylabel("시간 (ms)", color=INK2)
    ax.set_title(title, loc="left", fontsize=13, color=INK)


def periodic(P, cyc, n, base=1000):
    return [(c, base + P * k + s, base + P * k + e) for k in range(n) for c, s, e in cyc]


# ============ 공통 패턴 (7K: 열0 = 덮개) ============
COVER = [(0, 0, 0), (0, 10, 10 ** 6)]              # 더미 + 무한 덮개 (덮개가 첫 오브젝트면 안 됨)
UNI = [(1 + i, 30 * i, 30 * i + 164) for i in range(6)]          # 6열 균일: 열당 180ms (5.56kps), 30ms 엇갈림
HOT_C = ([(1, -30 + 60 * k, 14 + 60 * k) for k in range(5)] +      # 핫 열: 60ms 주기, 길이 44
         [(2 + j, 2 * j, 284 - 60 * j) for j in range(5)])        # 묶음 5열: 0~8ms 누름, 284..44 뗌
NAMES7 = ["덮개", "1", "2", "3", "4", "5", "6"]

# ============ 그림 1: 핫 컬럼 ============
def h_curves(notes, cols, t_end, dt=1.0):
    """열별 개별 스트레인 H(t): 노트마다 ×0.125^(경과초) 후 +2×1.25 (덮개가 모든 노트를 감쌈)."""
    ev = sorted((s, c) for c, s, e in notes if s > 20)
    ind, last = [0.0] * cols, [None] * cols
    ts, hs = [], [[] for _ in range(cols)]
    k, t = 0, 0.0
    while t <= t_end:
        while k < len(ev) and ev[k][0] <= t:
            s, c = ev[k]
            if last[c] is not None:
                ind[c] *= 0.125 ** ((s - last[c]) / 1000)
            ind[c] += 2.5; last[c] = s; k += 1
        ts.append(t)
        for c in range(cols):
            hs[c].append(ind[c] * 0.125 ** ((t - last[c]) / 1000) if last[c] is not None else 0)
        t += dt
    return ts, hs

fig, axes = plt.subplots(2, 2, figsize=(14, 10), facecolor=SURF,
                         gridspec_kw={"width_ratios": [1, 1.6], "hspace": 0.35, "wspace": 0.18})
for row, (cyc, P, title) in enumerate([(UNI, 180, "(가) 균일: 6열이 똑같이 5.6kps"),
                                       (HOT_C, 300, "(나) 핫 컬럼: 1열만 16.7kps, 나머지 3.3kps")]):
    notes = COVER + periodic(P, cyc, 40)
    lanes(axes[row][0], notes, 7, 5000, 5600, None, title, NAMES7, cover_col=0)
    ts, hs = h_curves(notes, 7, 7000)
    ax = axes[row][1]; style(ax)
    lo = [i for i, t in enumerate(ts) if 5000 <= t <= 6500]
    for c in range(1, 7):
        hot = row == 1 and c == 1
        ax.plot([ts[i] for i in lo], [hs[c][i] for i in lo], color=RED if hot else BLUE,
                lw=2.4 if hot else 1.1, alpha=1 if hot else 0.55, label=("1열 (핫)" if hot else None))
    peak = max(max(hs[c][i] for i in lo) for c in range(1, 7))
    ax.axhline(peak, color=INK2, ls="--", lw=1)
    ax.text(5010, peak + 0.6, f"피크에 쓰이는 H = 가장 높은 열 ≈ {peak:.1f}", fontsize=12, color=INK)
    ax.set_ylim(0, 25); ax.set_xlim(5000, 6500); ax.grid(True, color=GRID); ax.set_axisbelow(True)
    ax.set_title("열별 개별 스트레인 H (파랑 = 일반 열, 빨강 = 핫 열)", loc="left", fontsize=13, color=INK)
    ax.set_xlabel("시간 (ms)", color=INK2)
fig.text(0.02, 0.005,
         "원리: 노트 하나의 S = O(전체) + H(그 노트가 있는 열만). 400ms 구간 점수는 그 구간 S의 최댓값.\n"
         "→ 모든 열이 고르게 뜨거울 필요가 없다. 한 열만 뜨거우면 그 열 노트를 칠 때 S가 최고가 된다. "
         "같은 노트 수로 H 8.0 → 21.3.", fontsize=12, color=INK)
fig.savefig(f"{OUT}/7k_1_hot_column.png", dpi=110, bbox_inches="tight", facecolor=SURF)
plt.close(fig)

# ============ 그림 2: 대형 묶음 ============
def o_curve(events, P, n, t_from, t_to, dt=0.5):
    """O(t): 노트마다 ×0.3^(경과초) 후 +v. events: 한 주기 안 [(시각, v)]."""
    ev = sorted((P * k + t, v) for k in range(n) for t, v in events)
    O, last, k, t, ts, os_ = 1.0, 0.0, 0, 0.0, [], []
    while t <= t_to:
        while k < len(ev) and ev[k][0] <= t:
            O = O * 0.3 ** ((ev[k][0] - last) / 1000) + ev[k][1]; last = ev[k][0]; k += 1
        if t >= t_from:
            ts.append(t); os_.append(O * 0.3 ** ((t - last) / 1000))
        t += dt
    return ts, os_

FREE, PAY = 2.5, 1.875                    # 1.25×(1+1.0), 1.25×(1+0.5)
spread = [(30 * i, FREE if i % 2 else PAY) for i in range(10)]                           # 30ms마다 1개
pairs = [(60 * i, FREE) for i in range(5)] + [(60 * i + 2, PAY) for i in range(5)]       # 2개씩 (E4식)
burst = [(2 * j, FREE) for j in range(5)] + [(-30 + 60 * k + 300 * (k == 0), PAY) for k in range(5)]  # 5개 묶음 + 핫

fig, axes = plt.subplots(3, 1, figsize=(13, 11), facecolor=SURF, sharex=True,
                         gridspec_kw={"hspace": 0.38})
cases = [(spread, "(가) 고르게: 30ms마다 1개"), (pairs, "(나) 2개씩 묶음 (4K E4 방식)"),
         (burst, "(다) 5개 대형 묶음 + 핫 노트는 고르게 (7K)")]
mean = sum(v for _, v in spread) / 0.3 / (-math.log(0.3))
for ax, (ev, title) in zip(axes, cases):
    style(ax)
    ts, os_ = o_curve(ev, 300, 40, 9000, 9900)
    ax.plot(ts, os_, color=BLUE, lw=1.8)
    pk = max(os_)
    ax.axhline(mean, color=INK2, ls=":", lw=1); ax.axhline(pk, color=RED, ls="--", lw=1.2)
    ax.text(9905, mean - 1.6, f"평균 {mean:.1f}", fontsize=11, color=INK2, ha="right")
    ax.text(9905, pk + 0.5, f"피크 {pk:.1f}  (평균 +{pk - mean:.1f})", fontsize=12, color=RED, ha="right")
    for k in range(3):
        for t, v in ev:
            x = 9000 + 300 * k + t
            if 9000 <= x <= 9900:
                ax.plot([x, x], [44, 46.5], color=GREEN if v == FREE else ORANGE, lw=2)
    ax.set_ylim(43, 72); ax.grid(True, color=GRID); ax.set_axisbelow(True)
    ax.set_title(title + "   (아래 눈금 = 노트 시작, 초록 1.0 / 주황 0.5)", loc="left", fontsize=13, color=INK)
    ax.set_ylabel("O (전체 스트레인)", color=INK2)
axes[-1].set_xlabel("시간 (ms) — 세 경우 모두 300ms에 같은 노트 10개, 같은 점수", color=INK2)
fig.text(0.02, 0.0,
         "원리: O는 노트마다 값이 더해지고 1초에 0.3배로 줄어든다. 구간 점수는 '최댓값'이므로,\n"
         "같은 노트를 흩어 놓으면 더하는 사이사이 계속 줄어들고, 한꺼번에 몰면 줄어들 틈 없이 쌓인 꼭대기가 잡힌다.\n"
         "피크 상승 ≈ 묶음 가치의 절반. 4K는 열이 4개라 묶음 최대 4개, 7K는 5~6개까지 가능.", fontsize=12, color=INK)
fig.savefig(f"{OUT}/7k_2_burst.png", dpi=110, bbox_inches="tight", facecolor=SURF)
plt.close(fig)

# ============ 그림 3: 브리지 ============
NO_BRIDGE_LONGFIRST = [(2 + j, 2 * j, 284 - 60 * j) for j in range(5)]   # 핫 열 없이, 긴 것부터 누름
NO_BRIDGE_FAN = [(2 + j, 2 * j, 44 + 60 * j) for j in range(5)]         # 핫 열 없이, 짧은 것부터 누름(부채꼴)
fig, axes = plt.subplots(1, 3, figsize=(16, 9.5), facecolor=SURF, gridspec_kw={"wspace": 0.25})
panels = [(NO_BRIDGE_LONGFIRST, "(가) 브리지 없음, 긴 것부터"),
          (NO_BRIDGE_FAN, "(나) 브리지 없음, 짧은 것부터"),
          (HOT_C, "(다) 브리지 있음 (1열 핫 노트)")]
for ax, (cyc, title) in zip(axes, panels):
    notes = COVER + periodic(300, cyc, 30)
    vals = judge(notes, 7)
    T = 1000 + 300 * 10
    lanes(ax, notes, 7, T - 60, T + 320, vals, title, NAMES7, cover_col=0)
    ax.axhline(T, color=INK2, ls="--", lw=1)
    ax.text(6.95, T + 3, "묶음 시작", ha="right", fontsize=10, color=INK2)
    if cyc is HOT_C:                      # 브리지 릴리즈가 묶음 몸통들을 관통
        ax.axhline(T + 14, color=RED, lw=1.6)
        ax.text(2.1, T - 35, "빨간 선 = 브리지 릴리즈\n(모든 묶음 노트 몸통 안)", fontsize=10, color=RED,
                bbox=dict(facecolor=SURF, edgecolor=RED, boxstyle="round,pad=0.3"))
for ax in axes[1:]:
    ax.set_ylabel("")
h = [Rectangle((0, 0), 1, 1, color=c) for c in (GREEN, ORANGE, RED, GRAYN)]
fig.legend(h, ["홀드어디션 ≈1.0", "0.5 (희생)", "0 (겹침 조건 실패)", "덮개"], loc="upper right",
           ncol=4, fontsize=11, frameon=False)
fig.text(0.02, -0.06,
         "원리: 홀드어디션은 '먼저 시작해서 아직 눌려 있는 노트의 릴리즈가 내 몸통 안에 있을 때'만 붙는다.\n"
         "(가) 묶음 시작 순간 덮개 말고는 눌린 게 없고, 먼저 누른 노트가 더 늦게 끝나서 몸통 안에 릴리즈가 없다 → 전부 0.\n"
         "(나) 짧은 것부터 누르면 앞 노트 릴리즈가 뒤 노트 몸통에 들어가 살아나지만, 맨 처음 노트는 짝이 없어 0.\n"
         "(다) 묶음 직전에 눌러 묶음 직후에 떼는 브리지 1개가 모든 묶음 노트의 짝이 된다. "
         "4K E4+는 이 역할을 같은 열 노트가 하다가 16ms 규칙을 어겼고, 7K는 전용 열로 해결.",
         fontsize=12, color=INK)
fig.savefig(f"{OUT}/7k_3_bridge.png", dpi=110, bbox_inches="tight", facecolor=SURF)
plt.close(fig)
print("ok")
