"""E2/E4 실측용 패턴. 같은 손 누름 간격 2ms는 스케일과 무관하게 유지한다."""
import sys, importlib.util
spec = importlib.util.spec_from_file_location("g", sys.argv[2])
sys.argv = [sys.argv[0], sys.argv[1]]
g = importlib.util.module_from_spec(spec); spec.loader.exec_module(g)

def periodic(period, cycle):
    out, B = [], g.T0
    while B < g.T_END:
        out += [(c, B + s, B + e) for c, s, e in cycle]
        B += period
    return out

# 원안
E2 = (126, [(0, 0, 120), (1, 2, 78), (2, 40, 78), (3, 42, 162)])
E4 = (168, [(0, 0, 122), (1, 2, 80), (2, 84, 206), (3, 86, 164)])
# 주기 맞춤 (시간 균일 스케일, 같은 손 2ms 누름은 유지)
E2_p168 = (168, [(0, 0, 160), (1, 2, 104), (2, 54, 104), (3, 56, 216)])   # ×4/3
E4_p126 = (126, [(0, 0, 92), (1, 2, 60), (2, 63, 155), (3, 65, 123)])     # ×3/4

for name, (p, cyc) in {"E2_orig_p126": E2, "E4_orig_p168": E4,
                       "E2_scaled_p168": E2_p168, "E4_scaled_p126": E4_p126}.items():
    g.write(name, periodic(p, cyc))
