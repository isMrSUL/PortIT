"""Reference for the C# rock_fbx check: rock_fbx.build as PortIT runs it (full_port.install's wrapped covered; the
default drop / not-gear sets) on each wrestler folder + attire -> every mesh and bone, arrays as sha256 of float64 /
int64, the textures it wrote as sha256 of their pixels, its report.
    python fbx_ref.py <out.json> <work dir> <char folder>|<attire>..."""
import hashlib, io, json, os, sys, contextlib
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import port_2k, full_port as FP, rock_fbx  # noqa: E402
from PIL import Image  # noqa: E402
FP.install(port_2k, lambda *a: None)

def hf(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.float64)).tobytes()).hexdigest()
def hi(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.int64)).tobytes()).hexdigest()
out, work = {}, sys.argv[2]
for k, spec in enumerate(sys.argv[3:]):
    char, att = spec.split('|')
    d = os.path.join(work, 'py_%d' % k)
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        meshes, bones = rock_fbx.build(char, att, d, want_bones=True)
    rec = {'char': char, 'attire': att, 'dir': d, 'report': buf.getvalue().splitlines(),
           'bones': [[b['name'], b['src'], b['parent'], np.asarray(b['world'], float).ravel().tolist()] for b in bones], 'meshes': []}
    for m in meshes:
        rec['meshes'].append({'name': m['name'], 'material': m['material'], 'texture': m['texture'],
                              'verts': hf(m['verts']), 'normals': hf(m['normals']), 'uv': hf(m['uv']), 'tris': hi(m['tris']),
                              'weights': [[int(b), hf(w)] for b, w in m['weights'].items()],
                              'lip_src': hi(m['lip_src']) if m.get('lip_src') is not None else None,
                              'jaw_src': hf(m['jaw_src']) if m.get('jaw_src') is not None else None})
    tx = {}
    for f in sorted(os.listdir(os.path.join(d, 'textures'))):
        im = Image.open(os.path.join(d, 'textures', f)).convert('RGBA')
        tx[f] = hashlib.sha256(im.tobytes()).hexdigest()
    rec['textures'] = tx
    out[str(k)] = rec
    print(os.path.basename(char), att, len(meshes), 'meshes', len(tx), 'textures', flush=True)
json.dump(out, open(sys.argv[1], 'w'))
