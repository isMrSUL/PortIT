"""Reference for the C# misc readers check: CityHash64 / import ids, .mtls files and colour maps, AEW containers, meshes
and textures (via the engine's own source_aew helpers).
    python misc_ref.py <out.json> <AEW paks folder> <aew wrestler folder>... -- <folder with .mtls files>..."""
import hashlib, json, os, sys, tempfile, glob
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('readers', 'portit', 'k26', 'svr')]
import cityhash, mtls, aew_mesh, aew_tex, source_aew  # noqa: E402

def hb(b): return hashlib.sha256(bytes(b)).hexdigest()
def hf(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.float64)).tobytes()).hexdigest()
def hi(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.int64)).tobytes()).hexdigest()
import re
def js(v):   # (a property both readers fail on carries its error text: the wording differs by language - masked)
    return hashlib.sha256(re.sub(r'<(\w+Property): [^>]*>', r'<\1: error>', json.dumps(v, sort_keys=False, default=str)).encode()).hexdigest()

args = sys.argv[2:]
cut = args.index('--')
paks, wrestlers, mfolders = args[0], args[1:cut], args[cut + 1:]
data = bytes(i * 37 % 251 for i in range(300))
out = {'city': ['%016x' % cityhash.cityhash64(data[:n]) for n in range(300)],
       'import': {p: '%016x' % cityhash.import_id(p) for p in ('/Game/WrestlerData/M0054_02_Adam_Cole/MI_Skin.MI_Skin',
                                                              '/Game/Common/Material/mt_Hands_L_01:x', '/game/a.b')}}
out['mtls'] = {}
for f in mfolders:
    for p in sorted(glob.glob(os.path.join(f, '**', '*.mtls'), recursive=True)):
        out['mtls'][p] = list(mtls.read(p).items())
    out['mtls']['colormaps:' + f] = list(mtls.color_maps(f).items())
cs = source_aew.containers(paks)
out['containers'] = [[c.toc.path, len(c.names), hb('\n'.join('%d=%s' % kv for kv in c.names.items()).encode('utf-8'))] for c in cs]
out['aew'] = []
tmp = tempfile.mkdtemp()
for w in wrestlers:
    name = source_aew.mesh_package(paks, w)
    c, idx, full = source_aew.find(paks, name)
    raw = c.read(idx)
    p = os.path.join(tmp, os.path.basename(full))
    open(p, 'wb').write(raw)
    m, r = aew_mesh.read(p)
    lod = m['lods'][0]
    rec = {'wrestler': w, 'utoc': c.toc.path, 'name': full, 'raw': hb(raw), 'props': js(m['props']), 'bounds': hf(m['bounds']),
           'materials': [[x['material'], x['slot'], x['imported'], hf(x['uv_density'])] for x in m['materials']],
           'bones': [[b[0], b[1], hf(b[2][0]), hf(b[2][1]), hf(b[2][2])] for b in m['bones']],
           'sections': [[s['flag'], s['material'], s['base_index'], s['triangles'], s['base_vertex'], s['vertices'], s['max_influences'],
                         hi(s['bone_map'])] for s in lod.get('sections', [])],
           'indices': hi(lod['indices']), 'positions': hf(lod['positions']), 'tx': hf(lod['tangent_x']), 'tz': hf(lod['tangent_z']),
           'uvs': hf(lod['uvs']), 'slots': hi(lod['skin_slots']), 'weights': hf(lod['skin_weights']),
           'global': hi(aew_mesh.global_bones(lod)), 'textures': []}
    # a few of its textures: every T_ package in its folder
    texs = [(cc, i, n) for cc in cs for i, n in cc.names.items()
            if '/WrestlerData/%s/' % w in n and os.path.basename(n).startswith('T_') and n.endswith('.uasset')][:4]
    for cc, i, n in texs:
        tp = source_aew.read_file(paks, n[-60:] if len(n) > 60 else n, tmp)
        t = aew_tex.read(tp)
        mm = t['mips'][0]
        mb = aew_tex.mip_bytes(t, 0)
        un = aew_tex.untile_ps4(t['fmt'], mm['w'], mm['h'], mb)
        try:
            im = aew_tex.to_image(t['fmt'], mm['w'], mm['h'], un).convert('RGBA')
            pix = hb(im.tobytes())
        except Exception as e:      # noqa: BLE001
            pix = 'error'
        rec['textures'].append([n, t['fmt'], t['w'], t['h'], [[x['flags'], x['size'], x['w'], x['h'], x['src'][0]] for x in t['mips']],
                                hb(mb), hb(un), pix])
    out['aew'].append(rec)
json.dump(out, open(sys.argv[1], 'w'))
print('reference written:', len(out['mtls']), 'mtls entries,', len(out['aew']), 'AEW wrestlers,', sum(len(r['textures']) for r in out['aew']), 'textures')
