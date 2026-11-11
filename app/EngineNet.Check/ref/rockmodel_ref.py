"""Reference for the C# rock_model check: rock_model.build with the tables port_2k.setup / targets fill for each
wrestler (the classic route; no face-paint folders, no card thinning - those are port_2k's, checked with it; unweighted
meshes on J_Head as port_2k's prepare does) -> its model.json (left in the work folder), the settings used, the
sheets' pixels as sha256, its report and the lines it printed.
    python rockmodel_ref.py <out.json> <work dir> <game folder> <char folder>|<attire>..."""
import contextlib, hashlib, io, json, os, sys, time
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import port_2k, full_port as FP, rock_fbx  # noqa: E402
from PIL import Image  # noqa: E402

out, work, game = {}, sys.argv[2], sys.argv[3]
template = os.path.join(game, 'pac', 'ch', 'ch125.pac')
os.environ['PORTIT_GAME'] = game
FP.install(port_2k, lambda *a: None)
for k, spec in enumerate(sys.argv[4:]):
    char, att = spec.split('|')
    d = os.path.join(work, 'py_%d' % k)
    os.makedirs(d, exist_ok=True)
    os.environ['PORTIT_WORK'] = d
    FP.QUICK_READ[0] = True
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            R, info = port_2k.setup(char, att)
    finally:
        FP.QUICK_READ[0] = False
    cards = port_2k.card_meshes(info)
    R.TARGET = {n: t for n, t in port_2k.targets(info, 1.0).items() if n not in cards}
    R.TEX_FIRST = []

    def prepare(meshes, bones):
        head = next(i for i, b in enumerate(bones) if b['src'] == 'J_Head')
        for m in meshes:
            if not m['weights']:
                m['weights'] = {head: np.ones(len(m['verts']))}
        return meshes
    R.PREPARE = prepare
    settings = {'cells': R.CELLS, 'sheet': R.SHEET, 'mouth_cell': R.TMPL_MOUTH_CELL, 'shine': R.SHINE, 'sweat': R.SWEAT,
                'solid': R.SOLID, 'kind': R.KIND, 'target': R.TARGET, 'drop': sorted(R.DROP_MESHES), 'cutout': sorted(R.CUTOUT),
                'tex_first': R.TEX_FIRST, 'flip_g': R.NORMAL_FLIP_G, 'corner_smooth': R.CORNER_SMOOTH,
                'template_mouth': R.TEMPLATE_MOUTH, 'not_gear': sorted(rock_fbx.NOT_GEAR)}
    buf = io.StringIO()
    t0 = time.time()
    with contextlib.redirect_stdout(buf):
        _, report = R.build(char, att, d, template)
    secs = time.time() - t0
    rec = {'char': char, 'attire': att, 'dir': d, 'settings': settings, 'report': report,
           'printed': buf.getvalue().splitlines(), 'secs': secs}
    for s in ('color', 'normal'):
        rec[s] = hashlib.sha256(Image.open(os.path.join(d, s + '.png')).convert('RGBA').tobytes()).hexdigest()
    out[str(k)] = rec
    json.dump(out, open(sys.argv[1], 'w'))
    print(os.path.basename(char), att, '%.0f s' % secs, flush=True)
