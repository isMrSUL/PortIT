# (PortIT copy of tools/k26_porter/brock_model.py - the pipeline of the working Rock / Brock ports; only file paths changed, marked PortIT:)
"""Brock Lesnar (WWE 2K24 mod files: BaseModel + 343_Default_Attire) -> model.json + ch.pac for SvR 2011, with
rock_model.py's pipeline (2K24 .mcd reads like 2K26's). What differs from The Rock:
  * his hair and beard are 2K hair cards (materials hair_ALPHA / combinations_ALPHA): no colour map, only a flow
    map and a "rai" map, the colour comes from the hair shader. The strands are the rai map's green channel, so
    the colour sheet gets a flat hair colour with that as its cut-out alpha (rock_model.CUTOUT: see-through,
    double sided, as stock hair cards).
  * the hair meshes carry no skin weights (2K moves them with its hair physics): bound to J_Head.
  * two meshes are both named M_Hair: renamed by material (M_HairCards, M_HeadAcce); the beard is M_Beard.
  * the cards are thinned by dropping whole cards (a card is all border: qem can't shorten it).
  * dropped: eye shadow overlay, two helper meshes (lambert1), 2K teeth / tongue (the template's mouth).

    python -I brock_model.py <source folder: BaseModel + Default> <out dir> [--scale 1.0] [--template chNNN.pac] [--full [--max]]
The source folder is a copy of the download with the attire folder named Default and its files Attire.*.
"""
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rock_fbx  # noqa: E402
import rock_model as R  # noqa: E402

HAIR = (150, 121, 84)        # dirty blond (his 2K24 look: short blond hair)
BEARD = (128, 100, 68)       # a bit darker
CARD_TARGET = {'M_HairCards': 1300, 'M_Beard': 1300}

R.DROP_MESHES = {'M_Teeth', 'M_Tongue', 'M_transB_eyeshadow', 'polySurface1', 'S_Hair'}
R.SOLID = dict(R.SOLID, eye_caruncle=(176, 104, 98))   # (the pink inner eye corner: no texture of its own)
rock_fbx.NOT_GEAR = {'M_Body', 'M_Head', 'M_EYE', 'M_Head_Caruncle', 'M_transB_eyelash', 'M_Teeth', 'M_Tongue', 'M_MouthBag',
                     'M_Hair', 'M_Combinations'}         # (hair cards never hide the skin under them)
R.KIND = {'M_EYE': 'eye', 'M_transB_eyelash': 'lash'}
R.TARGET = {'M_Body': 3000, 'M_Head': 2700, 'M_Bottoms': 900, 'M_L_A_Acce': 350, 'M_R_A_Acce': 350,
            'M_Shoes': 1200, 'M_MouthBag': 220, 'M_Head_Caruncle': 160}
R.CUTOUT = {'hair_ALPHA', 'combinations_ALPHA'}
R.CELLS = {
    'head': (0, 0, 1024, 1024),
    'body': (1024, 0, 1024, 512),
    'bottoms': (1024, 512, 256, 256),
    'shoes': (1280, 512, 256, 128),
    'l_a_acce': (1280, 640, 128, 128),
    'r_a_acce': (1408, 640, 128, 128),
    'hair_ALPHA': (1536, 512, 256, 256),
    'combinations_ALPHA': (1792, 512, 256, 256),
    'h_acce': (1024, 768, 64, 64),
    'eye_caruncle': (1088, 768, 32, 32),
    'eye': (1792, 768, 256, 128),
    'eyelash': (1792, 896, 256, 128),
    'mouthbag': R.CELLS['mouthbag'],                       # (inside the template's mouth block, 1536, 832)
}
R.SHINE = {'head': (10, 1 / 16), 'body': (10, 1 / 16), 'shoes': (80, 1 / 3), 'bottoms': (25, 1 / 2),
           'l_a_acce': (40, 1 / 3), 'r_a_acce': (40, 1 / 3), 'h_acce': (40, 1 / 3),
           'hair_ALPHA': (20, 0), 'combinations_ALPHA': (20, 0), 'eye_caruncle': (40, 0), 'eye': (52, 0), 'eyelash': (0, 0)}


def hair_colour(src, dst, rgb):
    """a hair card colour map: flat colour, a little lighter on the strands, alpha = the rai map's green"""
    a = np.asarray(Image.open(src).convert('RGBA')).astype(np.float64)
    g = a[..., 1] / 255
    alpha = np.clip((g - 0.12) / 0.5, 0, 1)
    shade = 0.8 + 0.35 * g
    out = np.zeros(a.shape, np.uint8)
    out[..., :3] = np.clip(np.array(rgb)[None, None] * shade[..., None], 0, 255)
    out[..., 3] = np.round(alpha * 255)
    Image.fromarray(out, 'RGBA').save(dst)


def thin_cards(m, target, seed=7):
    """keep whole cards (connected triangle groups), evenly, until about `target` vertices"""
    tris = m['tris']
    n = len(m['verts'])
    parent = np.arange(n)

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    for a, b, c in tris:
        ra, rb, rc = find(a), find(b), find(c)
        parent[rb] = ra
        parent[find(rc)] = ra
    root = np.array([find(i) for i in range(n)])
    cards = {}
    for t, (a, _, _) in enumerate(tris):
        cards.setdefault(root[a], []).append(t)
    ids = list(cards)
    rng = np.random.default_rng(seed)
    rng.shuffle(ids)
    keep_t, nv = [], 0
    for c in ids:
        vs = len(np.unique(tris[cards[c]]))
        if nv + vs > target and keep_t:
            continue
        keep_t += cards[c]
        nv += vs
    keep_t = np.array(sorted(keep_t))
    used = np.zeros(n, bool)
    used[tris[keep_t].ravel()] = True
    remap = -np.ones(n, int)
    remap[used] = np.arange(used.sum())
    m['tris'] = remap[tris[keep_t]]
    m['verts'], m['normals'], m['uv'] = m['verts'][used], m['normals'][used], m['uv'][used]
    m['weights'] = {b: w[used] for b, w in m['weights'].items()}
    return len(cards), len({root[tris[t][0]] for t in keep_t})


def prepare(meshes, bones):
    head = next(i for i, b in enumerate(bones) if b['src'] == 'J_Head')
    for m in meshes:
        mat = m['material']
        if m['name'] == 'M_Hair':
            m['name'] = 'M_HairCards' if mat == 'hair_ALPHA' else 'M_HeadAcce'
        elif m['name'] == 'M_Combinations':
            m['name'] = 'M_Beard'
        if not m['weights']:                       # (2K's hair physics moved these: here they ride the head)
            m['weights'] = {head: np.ones(len(m['verts']))}
        if m['name'] in CARD_TARGET:
            n0 = len(m['verts'])
            nc, kc = thin_cards(m, CARD_TARGET[m['name']])
            print('%-12s cards %d -> %d, vertices %d -> %d' % (m['name'], nc, kc, n0, len(m['verts'])))
    return meshes


R.PREPARE = prepare


def main(argv):
    src, out = argv[0], argv[1]
    scale = float(argv[argv.index('--scale') + 1]) if '--scale' in argv else 1.0
    # (the template = the "look" the Mod Maker gave the mod: its ch.pac entry name, e.g. 00017902 - the game looks that up)
    template = argv[argv.index("--template") + 1] if "--template" in argv else R.TEMPLATE
    if '--full' in argv:     # (the big-model route, over the 192-page budget: needs the DLL's model memory hook)
        # every hair / beard card, in the hair model (child 0x2710); the body model (child 0x0) kept under ~1 MB of
        # JBOY data - 890 KB loaded, 1.97 MB crashed the match load (a null 1 MB buffer: 2026-10-22, brockbig1)
        CARD_TARGET.update({k: 10 ** 9 for k in CARD_TARGET})
        R.KIND.update({'M_HairCards': 'hair', 'M_Beard': 'hair', 'M_HeadAcce': 'hair'})
        # (with ControllerRemapigmodel.c: wrestler model heaps 2560 KB, vertex windows 2048 KB - room for all of it)
        if '--max' in argv:
            scale = 1000.0                      # (no decimation at all: every 2K24 vertex)
        else:
            R.TARGET.update({'M_Body': 3800, 'M_Head': 3400, 'M_Shoes': 1300, 'M_Bottoms': 1100, 'M_L_A_Acce': 450,
                             'M_R_A_Acce': 450, 'M_MouthBag': 352, 'M_Head_Caruncle': 225})
    tex = os.path.join(src, 'Default', 'Textures')
    hair_colour(os.path.join(tex, 'hair_rai.dds'), os.path.join(tex, 'hair_ALPHA_color.png'), HAIR)
    hair_colour(os.path.join(tex, 'combinations_rai.dds'), os.path.join(tex, 'combinations_ALPHA_color.png'), BEARD)
    spec, report = R.build(src, "Default", out, template, target_scale=scale)
    print('\n'.join(report))


if __name__ == '__main__':
    main(sys.argv[1:])
