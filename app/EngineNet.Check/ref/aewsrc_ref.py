"""Reference for the C# source_aew check: source_aew.load on AEW wrestlers -> bones (name, parent, world), meshes (name,
material, kind, cutout, arrays as sha256, images as sha256 of their RGBA pixels, scalars).
    python aewsrc_ref.py <out.json> <AEW paks folder> <wrestler folder>..."""
import hashlib, json, os, sys
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import source_aew  # noqa: E402


def hf(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.float64)).tobytes()).hexdigest()
def hi(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.int64)).tobytes()).hexdigest()


paks = sys.argv[2]
out = {}
for f in sys.argv[3:]:
    m = source_aew.load(paks, f)
    rec = {'bones': [[b['name'], int(b['parent']), np.asarray(b['world'], float).ravel().tolist()] for b in m['bones']], 'meshes': [],
           'female': m['female']}
    for s in m['meshes']:
        rec['meshes'].append({'name': str(s.name), 'material': s.material, 'kind': s.kind, 'cutout': s.cutout,
                              'positions': hf(s.positions), 'normals': hf(s.normals), 'uvs': hf(s.uvs), 'tris': hi(s.tris),
                              'bone_idx': hi(s.bone_idx), 'bone_w': hf(s.bone_w),
                              'images': {k: hashlib.sha256(v.convert('RGBA').tobytes()).hexdigest() for k, v in s.images.items()},
                              'scalars': {k: float(v) for k, v in s.scalars.items()}})
    out[f] = rec
    print(f, len(rec['meshes']), 'meshes', flush=True)
json.dump(out, open(sys.argv[1], 'w'))
