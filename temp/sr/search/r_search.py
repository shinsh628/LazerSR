"""단순화 문제: 4구간 주기 패턴(열당 1개)에서 R = 초당 Σv 최대화.
v = (1+holdAddition)*holdFactor (바닐라 판정 그대로), 정상상태 구간에서 측정."""
import sys, os, json, time
from multiprocessing import Pool
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import search as S
from mania_sr import evaluate

S.FAMILIES["A"] = (1, 125, 160)          # 주기 125~160ms 허용 (열당 8kps 이하)

def R_of(P, cols):
    notes = S.build(P, cols)
    _, _, per = evaluate(notes, detail=True)
    lo, hi = S.T0 + 5000, S.T0 + 5000 + 20 * P    # 정상상태 20주기
    v = sum(hf * (1 + ha) for s, c, hf, ha, _ in per if lo <= s < hi)
    return v / (20 * P) * 1000                     # 초당 Σv

S.score = R_of

if __name__ == "__main__":
    runs, iters = int(sys.argv[2]), int(sys.argv[3])
    jobs = [("A", 7000 + i, S.E4 if i == 0 else None, iters) for i in range(runs)]
    t = time.time()
    with Pool(4) as pool:
        res = pool.map(S.anneal, jobs, chunksize=1)
    json.dump(res, open(sys.argv[1], "w"))
    best = sorted(res, key=lambda r: -r["best"]["sr"])
    print(f"done {len(res)} runs in {time.time()-t:.0f}s")
    print("E4 R =", round(R_of(*S.E4), 3))
    for r in best[:6]:
        print(round(r["best"]["sr"], 3), "P", r["best"]["P"], r["best"]["cols"])
