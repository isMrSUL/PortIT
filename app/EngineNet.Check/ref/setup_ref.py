"""Reference for the C# port_2k setup check, in port_2k.build's order: face_paint (portit/face_paint.prepare) ->
check_green -> setup (the probe read) -> its tables, targets, the card meshes, brock_model.thin_cards on them,
strand_pieces, full_port.flat_cards; the painted head and the hair colour maps as sha256 of their pixels; the lines
logged / printed.
    python setup_ref.py <out.json> <work dir> <game folder> <char folder>|<attire>..."""
import contextlib, copy, hashlib, io, json, os, sys
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import port_2k, full_port as FP, rock_model as R, brock_model as BM  # noqa: E402
from PIL import Image  # noqa: E402


def px(p):
    return hashlib.sha256(Image.open(p).convert('RGBA').tobytes()).hexdigest() if p and os.path.isfile(p) else None


def hf(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.float64)).tobytes()).hexdigest()
def hi(a): return hashlib.sha256(np.ascontiguousarray(np.asarray(a, np.int64)).tobytes()).hexdigest()


work, game = sys.argv[2], sys.argv[3]
os.environ['PORTIT_GAME'] = game
FP.install(port_2k, lambda *a: None)
out = {}
for k, spec in enumerate(sys.argv[4:]):
    char, att = spec.split('|')
    d = os.path.join(work, 'py_%d' % k)
    os.makedirs(d, exist_ok=True)
    os.environ['PORTIT_WORK'] = d
    log = []
    R.NORMAL_FLIP_G = not port_2k.yukes_pack(char)
    flip0 = R.NORMAL_FLIP_G
    R.TEX_FIRST = []
    port_2k.face_paint(R, char, att, d, log.append)
    tex_first = list(R.TEX_FIRST)
    port_2k.check_green(R, char, att, log.append)
    buf = io.StringIO()
    FP.QUICK_READ[0] = True
    try:
        with contextlib.redirect_stdout(buf):
            _, info = port_2k.setup(char, att)
    finally:
        FP.QUICK_READ[0] = False
    cards = port_2k.card_meshes(info)
    rec = {'char': char, 'attire': att, 'dir': d, 'log': log, 'printed': buf.getvalue().splitlines(), 'flip0': flip0,
           'tex_first': tex_first, 'flip_g': R.NORMAL_FLIP_G,
           'painted': {f: px(os.path.join(d, '_facepaint', 'Textures', f)) for f in ('head_color.png', 'head_nrm.png')},
           'cells': R.CELLS, 'shine': R.SHINE, 'solid': R.SOLID, 'kind': R.KIND, 'drop': sorted(R.DROP_MESHES),
           'cutout': sorted(R.CUTOUT), 'not_gear': sorted(port_2k.rock_fbx.NOT_GEAR) if hasattr(port_2k, 'rock_fbx') else None,
           'solid_alpha': sorted(port_2k.SOLID_ALPHA), 'meshes': list(info['meshes'].items()), 'cards': cards,
           'card_rgb': {t: list(v) for t, v in info['cards'].items()},
           'targets': port_2k.targets(info, 1.0), 'targets73': port_2k.targets(info, 0.73),
           'pieces': {m['name']: port_2k.strand_pieces(m) for m in info['_meshes']}}
    import rock_fbx
    rec['not_gear'] = sorted(rock_fbx.NOT_GEAR)
    hair = {}
    for t in R.CUTOUT:
        ad = port_2k.attire_dir(char, att)
        import re
        rai = R.find_tex(char, ad, re.sub(r'(?i)_alpha$', '', t), '_rai') or R.find_tex(char, ad, t, '_rai')
        hair[t] = px(os.path.join(os.path.dirname(rai), t + '_color.png')) if rai else None
    rec['hair_maps'] = hair
    thin = {}
    for m in info['_meshes']:
        if m['name'] in cards or len(m['verts']) > 3000:
            mm = copy.deepcopy(m)
            nc, kc = BM.thin_cards(mm, 900)
            thin[m['name']] = [nc, kc, hf(mm['verts']), hi(mm['tris']), hf(mm['uv']),
                               [[int(b), hf(w)] for b, w in mm['weights'].items()]]
    rec['thin'] = thin
    rec['flat'] = sorted(FP.flat_cards(info['_meshes'], set(cards), lambda *a: None))
    out[str(k)] = rec
    json.dump(out, open(sys.argv[1], 'w'))
    print(os.path.basename(char), att, len(cards), 'cards', flush=True)
