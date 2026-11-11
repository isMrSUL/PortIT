"""Reference for the C# simplifier check: qem.simplify on real 2K meshes with the kinds of input rock_model gives it
(skin weights as attributes, weight 2.0; importance 3 on the head, x2 on hands), its inputs saved for C# (<dir>/case_N.bin:
i32 n, m, k, target, lock_borders; f64 V[n*3]; i32 F[m*3]; f64 attr[n*k]; f64 imp[n]) and its result.
    python qem_ref.py <out dir> <model.mcd>:<mesh index>:<target>[:free]..."""
import hashlib, json, os, struct, sys, time
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('readers', 'k26')]
import mcd_parse, qem  # noqa: E402
out_dir = sys.argv[1]
os.makedirs(out_dir, exist_ok=True)
ref = []
for k, spec in enumerate(sys.argv[2:]):
    parts = spec.rsplit(':', 3) if spec.endswith(':free') else spec.rsplit(':', 2)
    path, mi, target = parts[0], int(parts[1]), int(parts[2])
    free = spec.endswith(':free')
    m = mcd_parse.Mcd(path)
    V = np.asarray(m.stream(mi, 'POSITION'), np.float64)
    F = np.asarray(m.lods[0][mi]['triangles'], np.int64)
    mb = m.meshes[mi]
    bones = sorted(set(mb['skin_bones'][mb['skin_weights'] > 0].tolist())) or [0]
    col = {b: i for i, b in enumerate(bones)}
    A = np.zeros((len(V), len(bones)))
    for c in range(mb['skin_bones'].shape[1]):
        for v in range(len(V)):
            b = int(mb['skin_bones'][v, c]); w = mb['skin_weights'][v, c]
            if w > 0: A[v, col[b]] += w
    A = A / np.maximum(A.sum(1, keepdims=True), 1e-9)
    imp = np.ones(len(V)) * (3.0 if mb['material'] == 'M_Head' else 1.0)
    imp[np.arange(len(V)) % 7 == 0] *= 2.0          # (some vertices weighted up, as the hands are)
    t0 = time.time()
    keep, tris = qem.simplify(V, F, target, importance=imp, attr=A, attr_weight=2.0, lock_borders=not free)
    secs = time.time() - t0
    with open(os.path.join(out_dir, 'case_%d.bin' % k), 'wb') as f:
        f.write(struct.pack('<5i', len(V), len(F), A.shape[1], target, 0 if free else 1))
        f.write(V.astype('<f8').tobytes()); f.write(F.astype('<i4').tobytes())
        f.write(A.astype('<f8').tobytes()); f.write(imp.astype('<f8').tobytes())
    ref.append({'case': k, 'what': '%s mesh %d (%s) %d -> %d%s' % (os.path.basename(path), mi, mb['material'], len(V), target, ' free edges' if free else ''),
                'keep': hashlib.sha256(np.asarray(keep, '<i8').tobytes()).hexdigest(), 'tris': hashlib.sha256(np.asarray(tris, '<i8').tobytes()).hexdigest(),
                'kept': int(len(keep)), 'ntris': int(len(tris)), 'secs': secs})
    print(ref[-1]['what'], '->', len(keep), 'kept,', len(tris), 'triangles, %.1f s' % secs, flush=True)
json.dump(ref, open(os.path.join(out_dir, 'qem_ref.json'), 'w'), indent=0)
