"""2차 정밀 탐색: 1차 상위 패턴을 시드로 더 길게 어닐링."""
import sys, os, json, time
from multiprocessing import Pool
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import search as S

summ = json.load(open(sys.argv[1]))
iters = int(sys.argv[3])
tops = [(s["P"], [[tuple(x) for x in n] for n in s["cols"]]) for s in summ if s["name"].startswith("top")][:3]
jobs, i = [], 0
for t in tops:
    jobs.append(("A", 9000 + i, t, iters)); i += 1
for t in tops[:2] + [S.E4]:
    jobs.append(("B", 9000 + i, S.tile(*t, 2), iters)); i += 1
for t in tops[:2] + [S.E4]:
    jobs.append(("C", 9000 + i, S.tile(*t, 3), iters)); i += 1
for fam in ("A", "B", "C"):
    jobs.append((fam, 9000 + i, None, iters)); i += 1
t0 = time.time()
with Pool(4) as pool:
    res = pool.map(S.anneal, jobs, chunksize=1)
json.dump(res, open(sys.argv[2], "w"))
print(f"done {len(res)} runs in {time.time() - t0:.0f}s")
for fam in ("A", "B", "C"):
    rs = sorted((r for r in res if r["family"] == fam), key=lambda r: -r["best"]["sr"])
    print(fam, [round(r["best"]["sr"], 4) for r in rs], "| easy", [round(r["best_easy"]["sr"], 4) for r in rs])
