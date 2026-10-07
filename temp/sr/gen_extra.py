import sys, importlib.util
spec = importlib.util.spec_from_file_location("g", sys.argv[2])
sys.argv = [sys.argv[0], sys.argv[1]]
g = importlib.util.module_from_spec(spec); spec.loader.exec_module(g)
# 42ms 미만 간격 (규칙 3 밖)
for sp in (30, 32, 34, 36):
    L = sp + 15
    g.write(f"22_b2_2col_s{sp}_L{L}", g.cover() + g.roll([1, 2], sp, L))
    g.write(f"13_b_3col_s{sp}_L{L}", g.cover() + g.roll([1, 2, 3], sp, L))
# 해석 A: b2(s42) + 3열에 덮개 안쪽 3ms짜리 롱노트를 촘촘히
for gap in (42, 21, 10):
    g.write(f"50_b2_s42_plus_col3_tinyLN_every{gap}", g.cover() + g.roll([1, 2], 42, 60) + g.roll([3], gap, 3, t0=g.T0 + 1))
