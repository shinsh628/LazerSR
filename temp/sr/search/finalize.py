"""탐색 결과 상위 후보 → .osu + 이미지 + 요약."""
import sys, os, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from search import build, score, hand_easy, feasible
from PIL import Image, ImageDraw

res_files, out_dir, top_n, easy_n = sys.argv[1].split(","), sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
os.makedirs(out_dir, exist_ok=True)
cands = []
for rf in res_files:
    for r in json.load(open(rf)):
        for key in ("best", "best_easy"):
            b = r[key]
            if b["P"] is None:
                continue
            cols = [[tuple(x) for x in n] for n in b["cols"]]
            cands.append((round(b["sr"], 4), r["family"], b["P"], cols))
uniq = {}
for c in cands:
    uniq.setdefault((c[0], c[2]), c)
cands = sorted(uniq.values(), key=lambda c: -c[0])
top = cands[:top_n]
easy = [c for c in cands if hand_easy(c[2], c[3])][:easy_n]

def x_of(col): return int(512 * col / 4 + 64)

def write_osu(path, notes):
    lines = ["osu file format v14", "", "[General]", "Mode: 3", "", "[Difficulty]", "HPDrainRate:8", "CircleSize:4",
             "OverallDifficulty:8", "ApproachRate:5", "SliderMultiplier:1.4", "SliderTickRate:1", "",
             "[TimingPoints]", "0,500,4,2,0,100,1,0", "", "[HitObjects]"]
    for c, s, e in sorted(notes, key=lambda n: (n[1], n[0])):
        lines.append(f"{x_of(c)},192,{s},128,0,{e}:0:0:0:0:" if e > s else f"{x_of(c)},192,{s},1,0,0:0:0:0:")
    open(path, "w").write("\n".join(lines) + "\n")

def render(path, P, cols, label, periods=None):
    win_len = P * (periods or max(1, round(756 / P)))
    scale, cw, gap, header = 1.6, 72, 6, 36
    W, H = 4 * cw + 5 * gap, header + int(win_len * scale)
    img = Image.new("RGB", (W, H), "black"); d = ImageDraw.Draw(img)
    d.text((gap, 10), label, fill=(150, 150, 150))
    start = P * 3
    for c, notes in enumerate(cols):
        for k in range(0, 8):
            for o, L in notes:
                s, e = P * k + o, P * k + o + L
                lo, hi = max(s, start), min(e, start + win_len)
                if hi <= lo:
                    continue
                yb, yt = H - (lo - start) * scale, max(H - (hi - start) * scale, header)
                if yb - 1 <= yt:
                    continue
                x0 = gap + c * (cw + gap)
                d.rectangle([x0, yt, x0 + cw - 1, yb - 1], fill="white")
    img.save(path)

summary = []
for tag, lst in (("top", top), ("easy", easy)):
    for i, (sr, fam, P, cols) in enumerate(lst):
        name = f"{tag}{i + 1:02d}_{fam}_P{P}_sr{sr:.3f}"
        write_osu(os.path.join(out_dir, name + ".osu"), build(P, cols))
        render(os.path.join(out_dir, name + ".png"), P, cols, name)
        summary.append({"name": name, "sr_py": sr, "family": fam, "P": P, "cols": cols, "easy": hand_easy(P, cols)})
json.dump(summary, open(os.path.join(out_dir, "summary.json"), "w"), indent=1)
for s in summary:
    print(s["name"], "easy" if s["easy"] else "", s["cols"])
