"""Reference for the C# MCD check: every field mcd_parse.Mcd reads, per model (arrays as sha256 of their float64 /
int bytes; the bone world matrices as numbers).
    python mcd_ref.py <out.json> <list.txt: one model path a line>"""
import hashlib, json, os, sys
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, 'readers')]
import mcd_parse  # noqa: E402

def hf(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.float64)).tobytes()).hexdigest()
def hi(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.int64)).tobytes()).hexdigest()
def hb(b): return hashlib.sha256(bytes(b)).hexdigest()

out = {}
for p in [l.strip() for l in open(sys.argv[2], encoding='utf-8') if l.strip()]:
    try:
        m = mcd_parse.Mcd(p)
    except Exception as e:      # noqa: BLE001
        out[p] = {'error': '%s: %s' % (type(e).__name__, e)}
        continue
    r = {'version': m.version, 'game': m.game, 'bbox': hf(m.bbox), 'strings': hb('\n'.join(m.strings).encode('latin1')),
         'bone_f': [m.bone_f0, m.bone_f1],
         'bones': [[b['name'], b['parent'], hf(b['t']), hf(b['euler']), b['flag'], b['jiggle'],
                    hf(b['jiggle_data']) if b['jiggle_data'] is not None else None] for b in m.bones],
         'world': [np.asarray(b['world'], np.float64).ravel().tolist() for b in m.bones],
         'objects': [[o[0], o[1]] for o in m.objects], 'gtpt': [list(g) for g in m.gtpt],
         'meshes': [], 'lods': [], 'trailer': hb(m.trailer), 'female': mcd_parse.looks_female(p)}
    for k, mb in enumerate(m.meshes):
        u = m.uvs(k)
        r['meshes'].append({
            'material': mb['material'], 'object': mb.get('object', ''), 'bbox': hf(mb['bbox']), 'vertices': mb['vertices'],
            'streams': [[s, key[0], key[1], a.dtype.str, list(a.shape), hf(a)] for s, key, a in mb['streams']],
            'skin': [list(mb['skin_bones'].shape), hi(mb['skin_bones']), hf(mb['skin_weights'])],
            'remap': hi(mb['remap']), 'stats': hb(mb['stats']),
            'shapes': [[s['name'], hi(s['index']), hf(s['delta']), hi(s['word1'])] for s in mb['shapes']],
            'uvs': hf(u) if u is not None else None})
    for row in m.lods:
        r['lods'].append([[x['mesh'], hi(x['triangles']), [list(map(int, s)) for s in x['subsets']]] for x in row])
    out[p] = r
json.dump(out, open(sys.argv[1], 'w'))
print('reference written:', len(out), 'models,', sum('error' in v for v in out.values()), 'errors')
