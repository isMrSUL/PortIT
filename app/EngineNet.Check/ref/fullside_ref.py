"""Reference for the C# full_port build-side check: pac_limits / heavy_meshes on built pacs (also with low limits so
every message shows), checks on fixed model.json files, layout (the full route's sheet) after setup, make_pack (the
pictures handed to _dds_levels and its cells, the file names, the log), window_pages, enabled_pack / enable_pack on
toml variants, default_rng(1).choice.
    python fullside_ref.py <out.json> <work dir> <game folder> <fix_ref.json> <pac>... -- <char>|<attire>..."""
import contextlib, hashlib, io, json, os, shutil, sys
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import port_2k, full_port as FP, rock_model as R  # noqa: E402

out_path, work, game, fixref = sys.argv[1:5]
rest = sys.argv[5:]
cut = rest.index('--')
pacs, chars = rest[:cut], rest[cut + 1:]
template = os.path.join(game, 'pac', 'ch', 'ch125.pac')
os.environ['PORTIT_GAME'] = game
FP.install(port_2k, lambda *a: None)
out = {'choice': {}, 'limits': {}, 'checks': {}, 'layout': {}, 'pack': {}, 'window': {}, 'toml': {}}
for n, k in ((5000, 1500), (12000, 1500), (80000, 1500), (1500, 1500), (900, 900), (30000, 1500)):
    out['choice']['%d_%d' % (n, k)] = np.random.default_rng(1).choice(n, k, replace=False).tolist()
for p in pacs:
    rec = {'limits': FP.pac_limits(p), 'heavy': [list(x) for x in FP.heavy_meshes(p)[0]], 'most': FP.heavy_meshes(p)[1]}
    pp, si = FP.PART_PAGES, FP.SAFE_INDICES
    FP.PART_PAGES, FP.SAFE_INDICES = 20, 1000
    rec['limits_low'] = FP.pac_limits(p, 50)
    rec['heavy_low'] = [list(x) for x in FP.heavy_meshes(p)[0]]
    FP.PART_PAGES, FP.SAFE_INDICES = pp, si
    out['limits'][p] = rec
for k, r in json.load(open(fixref)).items():
    f = os.path.join(r['dir'], 'fix_py.json')
    fo, go, notes = FP.checks(f, template)
    out['checks'][f] = [fo, go, notes]
for k, spec in enumerate(chars):
    char, att = spec.split('|')
    d = os.path.join(work, 'lay_%d' % k)
    os.makedirs(d, exist_ok=True)
    os.environ['PORTIT_WORK'] = d
    R.TEX_FIRST = []
    FP.QUICK_READ[0] = True
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            _, info = port_2k.setup(char, att, full=True)
    finally:
        FP.QUICK_READ[0] = False
    lines = []
    cells = FP.layout(R, char, port_2k.attire_dir(char, att), set(), lines.append)
    out['layout'][spec] = {'setup_cells': None, 'cells': R.CELLS, 'mouth': R.TMPL_MOUTH_CELL, 'sheet': R.SHEET, 'log': lines}
# make_pack: the classic sheets (2048 x 1024) at PACK_SIZE 1024 and 4096 (no resize), with cut cells
rk = json.load(open(fixref))
seen = []
plain = FP._dds_levels


def spy(im, cells):
    seen.append([hashlib.sha256(im.convert('RGBA').tobytes()).hexdigest(), im.size[0], im.size[1], [list(c) for c in cells]])
    return plain(im, cells)


FP._dds_levels = spy
for size in (1024, 4096):
    FP.PACK_SIZE[0] = size
    for k, r in rk.items():
        if not pacs:
            break
        d = r['dir']
        od = os.path.join(work, 'pack_%s_%d' % (k, size))
        shutil.rmtree(od, ignore_errors=True)
        seen.clear()
        lines = []
        files = FP.make_pack(d, pacs[0], od, 'portit_hd_t', [(1536, 768, 64, 64), (1024, 512, 333, 257)], lines.append)
        out['pack']['%s_%d' % (k, size)] = {'work': d, 'out': od, 'files': [os.path.basename(f) for f in files], 'log': lines, 'seen': [list(x) for x in seen]}
        break
FP._dds_levels = plain
FP.PACK_SIZE[0] = 8192
# window_pages
for name, text in (('none', None), ('one', 'x\nvertex windows (port 2.2.1): 12 -> 2048 KB\n'), ('two', 'vertex windows a: 3 -> 1024 KB\nvertex windows b: 4 -> 3072 KB\n')):
    g = os.path.join(work, 'win_' + name)
    shutil.rmtree(g, ignore_errors=True)
    os.makedirs(g)
    if text is not None:
        open(os.path.join(g, 'bigmodel.log'), 'w', encoding='utf-8').write(text)
    lines = []
    out['window'][name] = {'game': g, 'pages': FP.window_pages(g, lines.append), 'log': lines, 'text': text}
# enable_pack
variants = {'missing': None, 'empty': '', 'noline': 'a = 1\nb = "x"\n\n\n[section]\nc = 2\n',
            'other': 'native_texture_packs = "Other Pack"\n[s]\n', 'already': 'native_texture_packs = "Other;portit hd"\n',
            'crlf': 'a = 1\r\n\r\n[s]\r\nz = 1\r\n', 'nosection': 'a = 1\nb = 2'}
for name, text in variants.items():
    g = os.path.join(work, 'toml_' + name)
    shutil.rmtree(g, ignore_errors=True)
    os.makedirs(g)
    if text is not None:
        open(os.path.join(g, 'svr2011.toml'), 'w', encoding='utf-8', newline='').write(text)
    on = FP.enable_pack(g, 'PortIT HD')
    res = open(os.path.join(g, 'svr2011.toml'), encoding='utf-8', newline='').read()
    keep = os.path.join(g, 'svr2011.toml.before_portit')
    out['toml'][name] = {'text': text, 'on': on, 'result': res, 'kept': open(keep, encoding='utf-8', newline='').read() if os.path.exists(keep) else None,
                         'enabled': FP.enabled_pack(g)}
json.dump(out, open(out_path, 'w'))
print('ok')
