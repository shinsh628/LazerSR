"""열당 8kps(같은 열 시작 간격 >=125ms) 규칙 아래 주기 패턴 탐색 (시뮬레이티드 어닐링).
패턴 = (P, cols): cols[c] = [(offset, length), ...] (offset 오름차순, 0 <= offset < P)."""
import sys, os, json, math, random, time
from multiprocessing import Pool
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mania_sr import evaluate

T0, T_END = 2000, 22000
MIN_GAP = 125          # 열당 최대 8kps
MIN_LEN = 30           # 탭 금지: 롱노트 최소 길이
COLS = 4
FAMILIES = {"A": (1, 125, 130), "B": (2, 250, 260), "C": (3, 375, 390)}
HANDS = [(0, 1), (2, 3)]


def gaps(P, notes):
    """각 노트에서 같은 열 다음 노트까지의 (순환) 간격"""
    k = len(notes)
    return [((notes[(i + 1) % k][0] - notes[i][0]) % P) or P for i in range(k)]


def feasible(P, cols):
    seen = set()
    for notes in cols:
        if not notes:
            return False
        for (o, L), g in zip(notes, gaps(P, notes)):
            if g < MIN_GAP or L < MIN_LEN or L > g - 1 or not (0 <= o < P):
                return False
            if o in seen:                     # 시작 시각 동률 금지
                return False
            seen.add(o)
    return True


def build(P, cols):
    out, B = [(3, 0, 0)], T0                 # 리드 탭(첫 오브젝트 문제 회피용)
    while B < T_END:
        for c, notes in enumerate(cols):
            for o, L in notes:
                out.append((c, B + o, B + o + L))
        B += P
    return out


def score(P, cols):
    return evaluate(build(P, cols))


def hand_easy(P, cols, tight=5, loose=100):
    """같은 손 다른 열 누름 간격이 tight 이내 또는 loose 이상이면 통과"""
    for a, b in HANDS:
        for oa, _ in cols[a]:
            for ob, _ in cols[b]:
                d = abs(oa - ob) % P
                d = min(d, P - d)
                if tight < d < loose:
                    return False
    return True


def random_pattern(rng, fam):
    K, pmin, pmax = FAMILIES[fam]
    for _ in range(1000):
        P = rng.randint(pmin, pmax)
        cols = []
        for c in range(COLS):
            k = rng.randint(1, K)
            slack = P - MIN_GAP * k
            cuts = sorted(rng.randint(0, slack) for _ in range(k - 1))
            parts = [b - a for a, b in zip([0] + cuts, cuts + [slack])]
            g = [MIN_GAP + x for x in parts]
            o = rng.randrange(P)
            notes = []
            for gi in g:
                notes.append((o % P, rng.randint(MIN_LEN, gi - 1)))
                o += gi
            cols.append(sorted(notes))
        if feasible(P, cols):
            return P, cols
    raise RuntimeError("no feasible random pattern")


def mutate(rng, fam, P, cols):
    K, pmin, pmax = FAMILIES[fam]
    cols = [list(n) for n in cols]
    r = rng.random()
    d = rng.choice([1, 1, 2, 3, 5, 8, 13, 21, 34]) * rng.choice([-1, 1])
    c = rng.randrange(COLS)
    if r < 0.35:                                  # 노트 시작 이동
        i = rng.randrange(len(cols[c])); o, L = cols[c][i]
        cols[c][i] = ((o + d) % P, L)
    elif r < 0.70:                                # 길이 변경
        i = rng.randrange(len(cols[c])); o, L = cols[c][i]
        cols[c][i] = (o, L + d)
    elif r < 0.82:                                # 열 전체 이동
        cols[c] = [((o + d) % P, L) for o, L in cols[c]]
    elif r < 0.90:                                # 주기 변경 (오프셋 비율 유지 안 함)
        P = min(pmax, max(pmin, P + (1 if d > 0 else -1)))
        cols = [[(o % P, L) for o, L in n] for n in cols]
    else:                                         # 노트 추가/삭제
        if len(cols[c]) > 1 and rng.random() < 0.5:
            cols[c].pop(rng.randrange(len(cols[c])))
        elif len(cols[c]) < K:
            cols[c].append((rng.randrange(P), MIN_LEN + rng.randrange(60)))
    cols = [sorted(n) for n in cols]
    return P, cols


def anneal(args):
    fam, seed, init, iters = args
    rng = random.Random(seed)
    P, cols = init if init else random_pattern(rng, fam)
    cur = score(P, cols)
    best = (cur, P, cols)
    best_easy = (cur, P, cols) if hand_easy(P, cols) else (0, None, None)
    t_hi, t_lo = 0.4, 0.002
    for it in range(iters):
        T = t_hi * (t_lo / t_hi) ** (it / iters)
        nP, ncols = mutate(rng, fam, P, cols)
        if not feasible(nP, ncols):
            continue
        s = score(nP, ncols)
        if s >= cur or rng.random() < math.exp((s - cur) / T):
            P, cols, cur = nP, ncols, s
            if s > best[0]:
                best = (s, P, cols)
            if s > best_easy[0] and hand_easy(P, cols):
                best_easy = (s, P, cols)
    return {"family": fam, "seed": seed, "seeded": bool(init),
            "best": {"sr": best[0], "P": best[1], "cols": best[2]},
            "best_easy": {"sr": best_easy[0], "P": best_easy[1], "cols": best_easy[2]}}


def tile(P, cols, times):
    return P * times, [sorted((o + P * t, L) for t in range(times) for o, L in n) for n in cols]


E4 = (126, [[(0, 92)], [(2, 58)], [(63, 92)], [(65, 58)]])
ALT = (126, [[(0, 120)], [(42, 120)], [(2, 76)], [(4, 74)]])
SEEDS = {"A": [E4, ALT], "B": [tile(*E4, 2), tile(*ALT, 2)], "C": [tile(*E4, 3), tile(*ALT, 3)]}

if __name__ == "__main__":
    out = sys.argv[1]
    runs_per_family, iters = int(sys.argv[2]), int(sys.argv[3])
    for name, pat in [("E4", E4), ("ALT", ALT)]:
        print(name, "feasible", feasible(*pat), "score", round(score(*pat), 4), "easy", hand_easy(*pat), flush=True)
    jobs = []
    for fam in FAMILIES:
        for i in range(runs_per_family):
            init = SEEDS[fam][i] if i < len(SEEDS[fam]) else None
            jobs.append((fam, 1000 * ord(fam) + i, init, iters))
    t = time.time()
    with Pool(4) as pool:
        results = pool.map(anneal, jobs, chunksize=1)
    json.dump(results, open(out, "w"))
    print(f"done {len(results)} runs in {time.time() - t:.0f}s", flush=True)
    for fam in FAMILIES:
        rs = sorted((r for r in results if r["family"] == fam), key=lambda r: -r["best"]["sr"])
        print(fam, "top:", [round(r["best"]["sr"], 3) for r in rs[:5]],
              "| easy top:", sorted((round(r["best_easy"]["sr"], 3) for r in rs), reverse=True)[:3])
