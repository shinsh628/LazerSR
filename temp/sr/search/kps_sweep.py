"""E4 구조를 주기 P로 비례 축소/확대하며 SR·노트별 보너스 측정 (바닐라 재현기)."""
import sys, os, json, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import search as S
from mania_sr import evaluate

def e4(P):
    h, L, s = round(P / 2), round(0.73 * P), round(0.46 * P)
    return P, [[(0, L)], [(2, s)], [(h, L)], [(h + 2, s)]]

def comp(P):                      # 탐색 1위(126ms)를 비례 축소/확대
    k = P / 126
    r0 = round(26 * k); l0 = r0 + max(3, round(11 * k))
    return P, [[(l0 + 2, round(91 * k))], [(l0, min(P - 2, round(124 * k)))],
               [(r0 + 2, round(39 * k))], [(r0, round(73 * k))]]

rows = []
for P in range(60, 252, 2):
    for fam, f in (("E4", e4), ("COMP", comp)):
        p = f(P)
        notes = S.build(*p)
        sr, peak, per = evaluate(notes, detail=True)
        lo, hi = S.T0 + 5000, S.T0 + 5000 + 10 * P
        mid = [(hf, ha) for s, c, hf, ha, _ in per if lo <= s < hi]
        free = [ha for hf, ha in mid if hf == 1.0]
        sac = [ha for hf, ha in mid if hf > 1.0]
        rows.append(dict(fam=fam, P=P, kps=1000 / P, nps=4000 / P, sr=sr,
                         free_add=sum(free) / max(1, len(free)), sac_add=sum(sac) / max(1, len(sac)),
                         n_free=len(free), n_sac=len(sac)))
json.dump(rows, open(sys.argv[1], "w"))
for r in rows:
    if r["fam"] == "E4" and r["P"] % 10 in (0, 6) or r["fam"] == "COMP" and r["P"] in (100, 126, 150):
        print(f'{r["fam"]:4s} P={r["P"]:3d} kps={r["kps"]:5.2f} SR={r["sr"]:6.3f} free_add={r["free_add"]:.3f} sac_add={r["sac_add"]:.3f} (free {r["n_free"]}, sac {r["n_sac"]})')
