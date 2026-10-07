import sys, csv, os
sys.path.insert(0, os.path.dirname(__file__))
from mania_sr import evaluate, load_osu
files = [l.strip() for l in open(sys.argv[1]) if l.strip()]
rows = list(csv.reader(open(sys.argv[2])))[1:]
worst = 0
for f, r in zip(files, rows):
    notes, cols = load_osu(f)
    py = evaluate(notes, cols)
    cs = float(r[1])
    diff = abs(py - cs)
    worst = max(worst, diff)
    if diff > 5e-4:
        print("MISMATCH", r[0], cs, round(py, 4))
print(f"files={len(files)} worst_abs_diff={worst:.6f}")
