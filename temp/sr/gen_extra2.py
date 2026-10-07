import sys, importlib.util
spec = importlib.util.spec_from_file_location("g", sys.argv[2])
sys.argv = [sys.argv[0], sys.argv[1]]
g = importlib.util.module_from_spec(spec); spec.loader.exec_module(g)
for sp in (20, 24, 26, 28):
    L = sp + 12
    g.write(f"23_b2_2col_s{sp}_L{L}", g.cover() + g.roll([1, 2], sp, L))
