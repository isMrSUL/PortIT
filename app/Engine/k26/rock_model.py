# (PortIT copy of tools/k26_porter/rock_model.py - the pipeline of the working Rock / Brock ports; only file paths changed, marked PortIT:)
"""A WWE 2K26 character (base model + attire) -> model.json for the Mod Maker's own model builder
(Mod Maker Tools\\svr11_build.py), skipping its FBX import, which put the skeleton root on the floor (every joint
~10 units above the pelvis, so the game turned him about his feet), packed the ten textures into a grid of
512 x 341 cells (the blurred face) and cut each mesh on its own.

    python -I rock_model.py <character folder> <attire> <out dir> [template ch.pac]

What it does (2K26 is already -Y up, -Z front, +X left in cm, as 2011, which is in 10 cm units):
  * pose: stock 2011 wrestlers all share one leg skeleton (hip -> ankle 8.70, ankle 1.0 above the floor) and the
    motions are made for it, so his thighs / shins are moved and scaled onto the template's, his feet follow
    rigidly (yaw to the template's toe-out); arms are turned onto the template's A-pose directions (his are within
    a degree or two), hands by wrist -> middle finger and the palm, fingers straightened onto the template's
    finger axes (his are curled; the game's hand shapes add their curl). Spine, neck, head stay his.
    Linear blend skinning with his own weights and bind matrices.
  * origin at his hip joints' height (2011's root and momo share a height), x 0.1.
  * joints: the template's node positions moved to his (upper body, arms, fingers); legs stay the template's.
  * weights renamed to 2011 bones (helpers folded into the bone they follow), weights under 2% dropped.
  * decimation (qem.py) per mesh to targets that fit the 192-page budget; the face counts 3x.
  * one colour sheet (2048 x 1024) and normal sheet with the face on half of it; the builder's own grid packer is
    swapped for one cell (rock_build.py), so the sheet goes in as drawn.
"""
import json
import math
import os
import re
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'svr')   # PortIT: the bundled tools
sys.path[:0] = [HERE, TOOLS]
import k26_face as FT  # noqa: E402  (PortIT: renamed, PortIT has its own face_transfer)
import qem  # noqa: E402
import rock_fbx  # noqa: E402
import w13_char as W  # noqa: E402

TEMPLATE = os.path.join(os.environ.get('PORTIT_GAME', ''), 'pac', 'ch', 'ch125.pac')   # PortIT: the user's game (base=125)
TURN = np.diag([1.0, -1.0, -1.0])
SHEET = (2048, 1024)
GUTTER = 6                       # pixels of edge colour around each cell (mips, DXT blocks)
# atlas cells (x, y, w, h) in sheet pixels; the source textures fill 0..1 in UV
CELLS = {
    'head': (0, 0, 1024, 1024),
    'body': (1024, 0, 1024, 512),
    'shoes': (1024, 512, 512, 512),
    'underwear': (1536, 512, 512, 256),
    'l_kneepad': (1536, 768, 64, 64),
    'r_kneepad': (1600, 768, 64, 64),
    'l_elbowpad': (1664, 768, 64, 64),
    'r_elbowpad': (1728, 768, 64, 64),
    'eye': (1792, 768, 256, 128),
    'eyelash': (1792, 896, 256, 128),
}
# The two masks the skin shader reads, stock-like (measured on ch100 / ch125): shine = the normal map's alpha
# (skin ~10-25, boots / logos bright), sweat = the colour alpha on skin (~77; 0 on the gear; the eyes' yReflect
# reads it as shine). (base, + k * brightness)
SHINE = {'head': (10, 1 / 16), 'body': (10, 1 / 16), 'shoes': (80, 1 / 3), 'underwear': (25, 1 / 2),
         'l_kneepad': (60, 1 / 3), 'r_kneepad': (60, 1 / 3), 'l_elbowpad': (60, 1 / 3), 'r_elbowpad': (60, 1 / 3),
         'eye': (52, 0), 'eyelash': (0, 0)}
SWEAT = {'head': 77, 'body': 77, 'eye': 103}     # (eye: its colour alpha is the yReflect reflection - 200 looked glassy)
# the template's mouth inside (face_transfer.template_mouth): its sheet's teeth / tongue block is copied here
TMPL_MOUTH_SRC = (128, 0, 256, 128)              # (x, y, w, h) in the template's 2048 x 1024 colour sheet
TMPL_MOUTH_CELL = (1536, 832)
CELLS['mouthbag'] = (TMPL_MOUTH_CELL[0] + 128 + 16, TMPL_MOUTH_CELL[1] + 16, 96, 96)   # (in the template's lining)
FACE_GAIN = 0.7      # the game's expressions on the full template weights read as cartoonish on a realistic face
# mouth parts whose 2K26 textures are shared game files (not in the mod): flat colours
SOLID = {'teeth': (226, 220, 204), 'mouthbag': (112, 38, 42)}
CORNER_RADIUS = 0.25     # (units: 2.5 cm about each corner)
CORNER_SMOOTH = 0       # (rounds of weight smoothing about the mouth's corners; AEW ports: port_2k sets it)
NORMAL_FLIP_G = True    # (2K26 normal maps' green flipped to SvR's; AEW's already match: port_2k turns it off)
TEMPLATE_MOUTH = True   # (the template's teeth / tongue put in; AEW ports bring their own mouth: port_2k turns it off)
KIND = {'M_EYE': 'eye', 'M_TransB_eyelash': 'lash'}
# vertices kept per mesh (sum ~11.5k: the 2011 Rock has 8,956 in 158 pages)
TARGET = {'M_Body': 3300, 'M_Head': 2900, 'M_Shoes': 1850, 'M_Underwear': 850, 'M_R_kneepad': 300,
          'M_L_kneepad': 300, 'M_R_elbowpad': 220, 'M_L_elbowpad': 220,
          'M_Teeth': 380, 'M_Tongue': 150, 'M_MouthBag': 220}
MIN_WEIGHT = 0.02
DROP_MESHES = {'M_Teeth', 'M_Tongue'}   # (rock_fbx leaves these out)
CUTOUT = set()       # texture cells drawn as see-through cards (their colour alpha is the opacity): hair cards
SEAM_WELD = 0.03     # units (3 mm): a head vertex this close to a body vertex is on the seam
PREPARE = None       # optional f(meshes, bones) -> meshes, run on the 2K meshes before posing
# Stock 2011 meshes (every body / hair / lash mesh of the stock and working mod superstars) are wound the other
# way: as jboy reads them, their triangles and stored normals face inward. The game culls by that, so an
# outward-wound model (2K26, an FBX - and what the Mod Maker's FBX import writes) shows its inside ("the textures
# are inside his body"). Triangles are flipped and normals negated to match.
STOCK_WINDING = True
FINGERS = {'Thumb': 0, 'Index': 1, 'Middle': 2, 'Ring': 3, 'Pinky': 4}


# ---------------------------------------------------------------- small maths
def T4(R=None, t=None):
    M = np.eye(4)
    if R is not None:
        M[:3, :3] = R
    if t is not None:
        M[:3, 3] = t
    return M


def align(a, b):
    """the smallest rotation taking direction a onto b"""
    a = a / np.linalg.norm(a)
    b = b / np.linalg.norm(b)
    v = np.cross(a, b)
    c = float(a @ b)
    if c < -0.999999:
        ax = np.cross(a, [1, 0, 0] if abs(a[0]) < 0.9 else [0, 1, 0])
        ax /= np.linalg.norm(ax)
        return 2 * np.outer(ax, ax) - np.eye(3)
    K = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + K + K @ K / (1 + c)


def triad(a1, a2, b1, b2):
    """rotation with R a1 along b1 and R a2 in the (b1, b2) plane"""
    def frame(x, y):
        x = x / np.linalg.norm(x)
        z = np.cross(x, y)
        z /= np.linalg.norm(z)
        return np.stack([x, np.cross(z, x), z], axis=1)
    return frame(b1, b2) @ frame(a1, a2).T


# ---------------------------------------------------------------- 2K26 bone -> 2011 bone(s)
def to2011(name):
    if name in ('J_Root', 'J_Hips'):
        return [('root', 1.0)]
    if name in ('J_Spine1', 'J_Spine2') or name.startswith('H_Gut_Jig'):
        return [('koshi', 1.0)]
    if name == 'J_Chest' or name.startswith(('H_Pec', 'H_Lat', 'J_Collar', 'H_Collar')):
        return [('mune', 1.0)]
    if name == 'J_Neck' or re.match(r'H_Neck_[LR]_1$', name):
        return [('kubi', 1.0)]
    if name == 'H_Neck_tw' or re.match(r'H_Neck_[LR]_2$', name):
        return [('kubi_x', 1.0)]
    if re.match(r'H_Trap_[LR]$', name):
        return [('mune', 0.5), ('kubi', 0.5)]
    if name in ('J_Head', 'J_upperTeeth'):
        return [('atama', 1.0)]
    if name in ('J_Jaw', 'J_lowerTeeth') or name.startswith('J_Tongue'):     # (2011's jaw: d_kuchi)
        return [('d_kuchi', 1.0)]
    m = re.search(r'_([LR])(?:_|$)', name)
    if not m:
        return None
    s = m.group(1).lower() + '_'
    if name.startswith('J_Eye_'):
        return [(s + 'eye', 1.0)]
    if name.startswith(('J_Clavicle', 'H_Shld_Pad')):
        return [(s + 'sakotsu', 1.0)]
    if re.match(r'H_Delt_[LR]_1$', name):
        return [(s + 'sakotsu', 0.5), (s + 'ninoude', 0.5)]
    if name.startswith(('J_Shoulder', 'H_Delt', 'H_Bicep', 'H_Ebw_Out')):
        return [(s + 'ninoude', 1.0)]
    if re.match(r'H_Elbow_[LR]_tw01', name):
        return [(s + 'kote_x', 1.0)]
    if re.match(r'H_Elbow_[LR]_tw02', name):
        return [(s + 'kote_x2', 1.0)]
    if name.startswith('J_Elbow'):
        return [(s + 'kote', 1.0)]
    if name.startswith('J_Wrist'):
        return [(s + 'te', 1.0)]
    fm = re.match(r'J_(Thumb|Index|Middle|Ring|Pinky)F(\d)_[LR]', name)
    if fm:
        k = int(fm.group(2))
        if k == 0:
            return [(s + 'te', 1.0)]
        return [(s + 'yubi%d%d' % (FINGERS[fm.group(1)], min(k, 3) - 1), 1.0)]
    if name.startswith('H_Leg_Vol'):
        return [('root', 0.5), (s + 'momo', 0.5)]
    if name.startswith(('J_Leg', 'H_Hip', 'H_Kn_', 'H_Quad', 'H_Knee_Upper')):
        return [(s + 'momo', 1.0)]
    if name.startswith(('J_Knee', 'H_Knee_Lower')):
        return [(s + 'sune', 1.0)]
    if name.startswith(('J_Foot', 'H_Foot')):
        return [(s + 'ashi', 1.0)]
    if name.startswith(('J_Toe', 'H_Toe')):
        return [(s + 'tsumasaki', 1.0)]
    return None


# ---------------------------------------------------------------- pose
def pose(bones, tmpl_world, origin):
    """-> per-bone 4x4 deformation (bind-space point -> posed point), native cm"""
    P = np.array([b['world'][:3, 3] for b in bones])
    idx = {b['src']: i for i, b in enumerate(bones)}
    tw = {k: origin + 10.0 * v for k, v in tmpl_world.items()}       # template node positions in his space
    M = [None] * len(bones)
    for i, b in enumerate(bones):
        p = b['parent']
        assert p is None or p < i, 'parents first'
        Mp = M[p] if p is not None else np.eye(4)
        M[i] = Mp
        name = b['src']
        m = re.search(r'_([LR])(?:_|$)', name)
        s = m.group(1) if m else ''
        x = s.lower() + '_'

        def turn_to(child, target):      # rotate about the posed joint so the posed child direction = target
            pn = Mp @ np.append(P[i], 1)
            dcur = Mp[:3, :3] @ (P[child] - P[i])
            R = align(dcur, target)
            return T4(t=pn[:3]) @ T4(R) @ T4(t=-pn[:3]) @ Mp

        if name in ('J_Leg_' + s, 'J_Knee_' + s) and s:
            a, c = (('J_Leg_', 'J_Knee_'), ('momo', 'sune')) if name.startswith('J_Leg') else \
                (('J_Knee_', 'J_Foot_'), ('sune', 'ashi'))
            H, K = P[i], P[idx[a[1] + s]]
            tH, tK = tw[x + c[0]], tw[x + c[1]]
            d, dt = K - H, tK - tH
            dn = d / np.linalg.norm(d)
            sc = np.linalg.norm(dt) / np.linalg.norm(d)
            S = np.eye(3) + (sc - 1) * np.outer(dn, dn)
            M[i] = T4(t=tH) @ T4(align(d, dt)) @ T4(S) @ T4(t=-H)
        elif name in ('J_Foot_' + s, 'H_Foot_' + s) and s:
            F, Tq = P[idx['J_Foot_' + s]], P[idx['J_Toe_' + s]]
            tF, tT = tw[x + 'ashi'], tw[x + 'tsumasaki']
            h1, h2 = (Tq - F) * [1, 0, 1], (tT - tF) * [1, 0, 1]
            M[i] = T4(t=tF) @ T4(align(h1, h2)) @ T4(t=-F)
        elif name == 'J_Shoulder_' + s and s:
            M[i] = turn_to(idx['J_Elbow_' + s], tw[x + 'kote'] - tw[x + 'ninoude'])
        elif name == 'J_Elbow_' + s and s:
            M[i] = turn_to(idx['J_Wrist_' + s], tw[x + 'te'] - tw[x + 'kote'])
        elif name == 'J_Wrist_' + s and s:
            pn = Mp @ np.append(P[i], 1)
            L = Mp[:3, :3]
            a1 = L @ (P[idx['J_MiddleF1_' + s]] - P[i])
            a2 = L @ (P[idx['J_IndexF1_' + s]] - P[idx['J_PinkyF1_' + s]])
            b1 = tw[x + 'yubi20'] - tw[x + 'te']
            b2 = tw[x + 'yubi10'] - tw[x + 'yubi40']
            M[i] = T4(t=pn[:3]) @ T4(triad(a1, a2, b1, b2)) @ T4(t=-pn[:3]) @ Mp
        else:
            fm = re.match(r'J_(Thumb|Index|Middle|Ring|Pinky)F([123])_[LR]$', name)
            if fm:
                f, k = FINGERS[fm.group(1)], int(fm.group(2))
                nxt = 'J_%sF%d_%s' % (fm.group(1), k + 1, s) if k < 3 else 'J_%sF3_%s_stub' % (fm.group(1), s)
                j = k - 1
                a = tw[x + 'yubi%d%d' % (f, min(j, 1))]
                c = tw[x + 'yubi%d%d' % (f, min(j, 1) + 1)]
                M[i] = turn_to(idx[nxt], c - a)
    return M


def smooth_corners(U, tris, wl, L, tidy, radius=CORNER_RADIUS, rounds=None):
    """the weights about the mouth's two corners (within radius, units) blended with their neighbours' - fading
    out to the radius. AEW faces came out with a hard edge at the corners: half-jaw corner points beside upper-lip
    points on the skull, the upper lip kinked when the jaw opened (2026-11-09)"""
    rounds = rounds or CORNER_SMOOTH
    rep = FT._weld(U, np.arange(len(U))[:, None].repeat(3, 1))[:, 0]
    nb = {}
    for t in tris:
        for a in t:
            for b in t:
                if a != b:
                    nb.setdefault(rep[a], set()).add(rep[b])
    d = np.minimum(np.linalg.norm(U - L['mouth_l'], axis=1), np.linalg.norm(U - L['mouth_r'], axis=1))
    k = np.clip(1 - d / radius, 0, 1)
    region = np.nonzero(k > 0)[0]
    cur = {i: dict(wl[i]) for i in range(len(wl))}
    for _ in range(rounds):
        nxt = {}
        for v in region:
            ns = [x for x in nb.get(rep[v], ()) if x in cur]
            if not ns:
                continue
            avg = {}
            for x in ns:
                for b, w in cur[x].items():
                    avg[b] = avg.get(b, 0.0) + w / len(ns)
            mix = {b: (1 - k[v]) * cur[v].get(b, 0.0) + k[v] * avg.get(b, 0.0) for b in set(avg) | set(cur[v])}
            nxt[v] = mix
        for v, e in nxt.items():
            cur[v] = e
        for i in range(len(wl)):       # (UV-seam twins: the welded one's weights)
            if rep[i] != i and rep[i] in nxt:
                cur[i] = dict(cur[rep[i]])
    touched = set(region.tolist()) | {i for i in range(len(wl)) if rep[i] in set(region.tolist())}
    return [tidy(list(cur[i].items())) if i in touched else wl[i] for i in range(len(wl))]


JAW_BONES = ('d_kuchi', 'd_kuchi_00', 'd_kuchi_01', 'l_kuchi', 'r_kuchi')


def jaw_from_source(wl, jaw, b11, names11, tidy):
    """each head vertex's share on the jaw (d_kuchi and the bones under it) set to how far the source's own jaw-open
    shape moves it (2K's AU27, 0 - 1) x FACE_GAIN; the template's lip / corner bones keep their split of it, the rest
    of the vertex's weights fill the remainder. For heads with touching lips (fit_face lips_touch): their inner lips
    took mixed jaw weights from the template and tore into strips when the jaw opened (2K26 Bray Wyatt #861,
    2026-11-09) - 2K's own shape opens them cleanly"""
    out = []
    dk = b11('d_kuchi')
    for e, j in zip(wl, np.asarray(jaw)):
        t = FACE_GAIN * float(j)
        jw = [(b, w) for b, w in e if names11[b] in JAW_BONES]
        rest = [(b, w) for b, w in e if names11[b] not in JAW_BONES]
        sj, sr = sum(w for _, w in jw), sum(w for _, w in rest)
        if t < 0.01 and sj < 0.01:
            out.append(e)
            continue
        new = [(b, w / sj * t) for b, w in jw] if sj > 1e-6 else ([(dk, t)] if t > 0 else [])
        new += [(b, w / sr * (1 - t)) for b, w in rest] if sr > 1e-6 else [(b11('atama'), 1 - t)]
        out.append(tidy(new))
    return out


def skin(verts, normals, weights, M):
    out = np.zeros_like(verts)
    nout = np.zeros_like(normals)
    tot = np.zeros(len(verts))
    for b, w in weights.items():
        A = M[b]
        L = A[:3, :3]
        out += w[:, None] * (verts @ L.T + A[:3, 3])
        nout += w[:, None] * (normals @ np.linalg.inv(L))      # (inverse transpose, for the scaled legs)
        tot += w
    tot[tot == 0] = 1
    out /= tot[:, None]
    nn = np.linalg.norm(nout, axis=1)
    nn[nn == 0] = 1
    return out, nout / nn[:, None]


# ---------------------------------------------------------------- textures
GREEN_TURN_AT = 1.05


def green_curl_ratio(r, g, side=512):
    """a normal map's curl with green as it is / with green turned (x, y in 0 - 255). A normal map is the slope of a
    surface, so its x / y only add up (little curl) with green the right way: under 1, green points the way SvR's does;
    over 1, the other way; ~1, too little relief to tell. Used on the face map only (port_2k.check_green): small gear
    maps with strong patterns can mislead it - 2K26 CM Punk's arm pieces read 1.64 / 1.75 though right in the game."""
    from PIL import Image as _I
    def small(c):
        im = _I.fromarray(np.clip(c, 0, 255).astype(np.uint8))
        return np.asarray(im.resize((min(side, im.size[0]), min(side, im.size[1])), _I.BOX)).astype(np.float64) / 255 - 0.5
    x, y = small(r), small(g)
    if x.shape[0] < 8 or x.shape[1] < 8:
        return 1.0
    a = np.abs(np.gradient(y, axis=1) - np.gradient(x, axis=0)).mean()
    b = np.abs(np.gradient(-y, axis=1) - np.gradient(x, axis=0)).mean()
    return float(a / b) if b > 1e-9 else 1.0


def cell_image(path, cell, mode='RGB', normal=False):
    im = Image.open(path)
    im.load()
    x, y, w, h = cell
    iw, ih = w - 2 * GUTTER, h - 2 * GUTTER
    im = im.convert('RGBA')
    # (RGB and alpha resized apart: Pillow premultiplies RGBA, and the 2K26 normal maps' alpha is ~0 - resized
    #  together their x / y went to ~0, which the game drew as black holes all over the skin)
    rgb = np.asarray(im.convert('RGB').resize((iw, ih), Image.LANCZOS)).astype(np.float64)
    al = np.asarray(im.getchannel('A').resize((iw, ih), Image.LANCZOS)).astype(np.float64)
    a = np.dstack([rgb, al])
    if normal:      # 2K26 normals: x, y in R, G (B / A hold other data) -> z rebuilt
        if NORMAL_FLIP_G:
            # (2K26's green points the other way from SvR's: measured 2026-11-09 - the slopes of 2K's maps only add up
            #  to a surface with green flipped, the stock ch125 sheet's and AEW's as they are; unflipped, a 2K port's
            #  skin / cloth detail was lit inside out)
            a[..., 1] = 255.0 - a[..., 1]
        nx, ny = a[..., 0] / 127.5 - 1, a[..., 1] / 127.5 - 1
        nz = np.sqrt(np.clip(1 - nx * nx - ny * ny, 0, 1))
        a[..., 2] = (nz + 1) * 127.5
        a[..., 3] = 255
    a = np.pad(a, ((GUTTER, GUTTER), (GUTTER, GUTTER), (0, 0)), mode='edge')
    return np.clip(a, 0, 255).astype(np.uint8)


def cell_uv(cell, uv):
    """2K26 uv (0..1, v up) -> sheet uv (v down)"""
    x, y, w, h = cell
    u = (x + GUTTER + np.clip(uv[:, 0], 0, 1) * (w - 2 * GUTTER)) / SHEET[0]
    v = (y + GUTTER + (1 - np.clip(uv[:, 1], 0, 1)) * (h - 2 * GUTTER)) / SHEET[1]
    return np.stack([u, v], axis=1)


TEX_FIRST = []     # (folders looked in before the attire's / base model's: port_2k.face_paint - the head with its face
#                     paint laid on, an attire's own BaseModel\Textures_<look>)


def find_tex(char, attire, base, suffix):
    for folder in TEX_FIRST + [os.path.join(char, attire, 'Textures'), os.path.join(char, 'BaseModel', 'Textures')]:
        if not os.path.isdir(folder):
            continue
        for f in os.listdir(folder):
            if f.lower().split('.')[0] == (base + suffix).lower():
                return os.path.join(folder, f)
    return None


# ---------------------------------------------------------------- build
def split_rigid(tris, wl, min_verts=300):
    """triangles whose three vertices hang on one same bone only -> their own group (the game stores such a mesh
    with one weight block: 32 bytes a vertex instead of 44); -> [tri index arrays]"""
    key = []
    for t in tris:
        bs = [wl[v][0][0] if len(wl[v]) == 1 else None for v in t]
        key.append(bs[0] if bs[0] is not None and bs[0] == bs[1] == bs[2] else None)
    groups = {}
    for i, k in enumerate(key):
        groups.setdefault(k, []).append(i)
    out, rest = [], groups.pop(None, [])
    for k, g in groups.items():
        if len({v for i in g for v in tris[i]}) >= min_verts:
            out.append(np.array(g))
        else:
            rest += g
    if rest:
        out.append(np.array(sorted(rest)))
    return out


def build(char, attire, out, template=TEMPLATE, target_scale=1.0):
    os.makedirs(out, exist_ok=True)
    # (2K26 teeth / tongue stay out - the template's mouth inside goes in, textured and made for its rig; the 2K26
    #  mouth bag stays: the template's own pocket is part of its face mesh, and his mouth is wider than its teeth)
    rock_fbx.DROP_MESHES = set(DROP_MESHES)
    meshes, bones = rock_fbx.build(char, attire, os.path.join(out, '_fbx_tmp'), want_bones=True)
    if PREPARE:                      # (a character's own fixes: renames, weights for unweighted hair cards...)
        meshes = PREPARE(meshes, bones)
    C4 = T4(TURN)
    for b in bones:                                   # back to 2K26 / 2011 axes (rock_fbx turned them to +Y up)
        b['world'] = C4 @ b['world']
    for m in meshes:
        m['verts'] = m['verts'] @ TURN.T
        m['normals'] = m['normals'] @ TURN.T
    idx = {b['src']: i for i, b in enumerate(bones)}
    P = np.array([b['world'][:3, 3] for b in bones])
    origin = (P[idx['J_Leg_L']] + P[idx['J_Leg_R']]) / 2 * [0, 1, 1]
    conv = W.Conv(template)
    Wt = conv.world11(conv.tnodes)
    tmpl_world = {nd['name']: Wt[i][:3, 3] for i, nd in enumerate(conv.tnodes)}
    M = pose(bones, tmpl_world, origin)
    Pp = np.array([(M[i] @ np.append(P[i], 1))[:3] for i in range(len(bones))])

    def unit(p):
        return (np.asarray(p) - origin) * 0.1

    # joints: his upper body; legs stay the template's
    J = {}
    pairs = {'koshi': 'J_Spine1', 'mune': 'J_Chest', 'kubi': 'J_Neck', 'kubi_x': 'H_Neck_tw', 'atama': 'J_Head'}
    for s in 'LR':
        x = s.lower() + '_'
        pairs.update({x + 'eye': 'J_Eye_' + s, x + 'sakotsu': 'J_Clavicle_' + s, x + 'ninoude': 'J_Shoulder_' + s,
                      x + 'kote': 'J_Elbow_' + s, x + 'kote_x': 'H_Elbow_%s_tw01' % s,
                      x + 'kote_x2': 'H_Elbow_%s_tw02' % s, x + 'te': 'J_Wrist_' + s})
        for fname, f in FINGERS.items():
            for k in (1, 2, 3):
                pairs[x + 'yubi%d%d' % (f, k - 1)] = 'J_%sF%d_%s' % (fname, k, s)
    for n11, n26 in pairs.items():
        J[n11] = unit(Pp[idx[n26]])
    for s in 'lr':
        J[s + '_ninoude_x'] = (J[s + '_ninoude'] + J[s + '_kote']) / 2

    # eye pivots: the eyeballs' own centres where the 2K eye bones sit elsewhere (PortIT 2026-11-07: retail 2K26 The
    # Rock / Undertaker have J_Eye near the middle of the head - the face fit, measured in eye distances, found no
    # mouth, and the game would turn his eyes about the wrong point). Each eye's posed vertices (+x left), box centre.
    eyes = [m for m in meshes if m['name'] == 'M_EYE']
    if eyes:
        EP = np.concatenate([unit(skin(m['verts'], m['normals'], m['weights'], M)[0]) for m in eyes])
        for s, side in (('l', EP[:, 0] > 0), ('r', EP[:, 0] < 0)):
            if side.sum() < 20:
                continue
            c = (EP[side].min(0) + EP[side].max(0)) / 2
            if np.linalg.norm(J[s + '_eye'] - c) > 0.1:
                print('%s_eye: the 2K eye bone is %.1f cm from the eyeball - its centre used' % (s, np.linalg.norm(J[s + '_eye'] - c) * 10))
                J[s + '_eye'] = c
    # face rig: the template's face skinning and face bones fitted onto his face (face_transfer.py)
    hm = next(m for m in meshes if m['name'] == 'M_Head')
    Vh, _ = skin(hm['verts'], hm['normals'], hm['weights'], M)
    fit = FT.fit_face(template, unit(Vh), hm['tris'], J['l_eye'], J['r_eye'], src_side=hm.get('lip_src'))
    J.update({k: np.array(v) for k, v in fit['joints'].items()})
    mouth_y = fit['landmarks_dst']['mouth'][1]

    # weights -> 2011 names
    unmapped = {}
    names11 = []
    pos11 = {}

    def b11(n):
        if n not in pos11:
            pos11[n] = len(names11)
            names11.append(n)
        return pos11[n]

    sheet_c = np.zeros((SHEET[1], SHEET[0], 4), np.uint8)
    sheet_c[..., 3] = 255
    sheet_n = np.zeros((SHEET[1], SHEET[0], 4), np.uint8) + np.array([128, 128, 255, 0], np.uint8)
    lash_cell = None
    seam_body = None
    out_meshes, report = [], []
    for m in meshes:
        V, N = skin(m['verts'], m['normals'], m['weights'], M)
        n = len(V)
        # weights per vertex in 2011 bones
        dense = {}
        for b, w in m['weights'].items():
            src = bones[b]['src']
            tgt = None
            j = b
            while tgt is None and j is not None:
                tgt = to2011(bones[j]['src'])
                if tgt is None:
                    j = bones[j]['parent']
            if j != b:
                unmapped[src] = unmapped.get(src, 0) + float(w.sum())
            for nm, frac in tgt or [('root', 1.0)]:
                k = b11(nm)
                dense[k] = dense.get(k, 0) + frac * w
        keys = sorted(dense)
        Wd = np.stack([dense[k] for k in keys], axis=1)           # (n, nb)
        # top 4, drop small, renormalise
        order = np.argsort(-Wd, axis=1)[:, :4]
        wl = []
        for v in range(n):
            e = [(keys[c], float(Wd[v, c])) for c in order[v] if Wd[v, c] > 0]
            t = sum(w for _, w in e) or 1.0
            e = [(b, w / t) for b, w in e]
            e2 = [(b, w) for b, w in e if w >= MIN_WEIGHT] or e[:1]
            t2 = sum(w for _, w in e2)
            wl.append([(b, w / t2) for b, w in e2])

        def tidy(e):          # [(2011 index, w)] -> top 4, small dropped, sum 1
            acc = {}
            for b, w in e:
                acc[b] = acc.get(b, 0.0) + w
            e = sorted(acc.items(), key=lambda x: -x[1])[:4]
            t = sum(w for _, w in e) or 1.0
            e2 = [(b, w / t) for b, w in e if w / t >= MIN_WEIGHT] or [(e[0][0], 1.0)]
            t2 = sum(w for _, w in e2)
            return [(b, w / t2) for b, w in e2]
        if m['name'] == 'M_Head':
            own = [[(names11[b], w) for b, w in e] for e in wl]
            new = FT.transfer(fit, unit(V), own, tris=m['tris'], gain=FACE_GAIN, src_side=m.get('lip_src'))
            wl = [tidy([(b11(b), w) for b, w in e]) for e in new]
            if CORNER_SMOOTH:
                wl = smooth_corners(unit(V), m['tris'], wl, fit['landmarks_dst'], tidy)
            if fit.get('lips_touch') and m.get('lip_src') is not None:
                m['tris'] = FT.cut_lips(unit(V), m['tris'], m['lip_src'], fit['landmarks_dst'])
                if m.get('jaw_src') is not None:
                    wl = jaw_from_source(wl, m['jaw_src'], b11, names11, tidy)
        elif m['name'] == 'M_MouthBag':   # (top on the skull, below the lips on the jaw, a blend between)
            yd = FACE_GAIN * np.clip((unit(V)[:, 1] - mouth_y + 0.03) / 0.15, 0, 1)
            wl = [tidy([(b11('d_kuchi'), float(a)), (b11('atama'), 1 - float(a))]) for a in yd]
        elif m['name'] == 'M_AEWMouth':   # (AEW's own teeth / tongue: the jaw toned down as the lips are - as
            # FT.template_mouth does for the template's; at full jaw the lower teeth dropped behind the lower lip)
            dk = b11('d_kuchi')
            wl = [tidy([(b, w * (FACE_GAIN if b == dk else 1.0)) for b, w in e] +
                       [(b11('atama'), sum(w for b, w in e if b == dk) * (1 - FACE_GAIN))]) for e in wl]
        if m['name'] in ('M_Head', 'M_MouthBag', 'M_AEWMouth'):
            keys = sorted({b for e in wl for b, _ in e})
            col = {b: k for k, b in enumerate(keys)}
            Wd = np.zeros((n, len(keys)))
            for v, e in enumerate(wl):
                for b, w in e:
                    Wd[v, col[b]] = w
        # decimate
        tris = m['tris']
        tgt_n = TARGET.get(m['name'])
        if tgt_n:
            tgt_n = int(tgt_n * target_scale)
            imp = np.ones(n)
            if m['name'] == 'M_Head':
                imp[:] = 3.0
            hand = np.zeros(n)
            for k, nm in enumerate(keys):
                if re.search(r'_(te|yubi\d\d)$', names11[nm]):
                    hand += Wd[:, k]
            imp[hand > 0.5] *= 2.0
            Wn = Wd / np.maximum(Wd.sum(1, keepdims=True), 1e-9)
            keep, tris = qem.simplify(V, tris, tgt_n, importance=imp, attr=Wn, attr_weight=2.0,
                                      lock_borders=m['name'] not in ('M_Teeth',))
            remap = -np.ones(n, int)
            remap[keep] = np.arange(len(keep))
            tris = remap[tris]
            V, N, uv, wl = V[keep], N[keep], m['uv'][keep], [wl[i] for i in keep]
        else:
            used = np.unique(tris)
            remap = -np.ones(n, int)
            remap[used] = np.arange(len(used))
            tris = remap[tris]
            V, N, uv, wl = V[used], N[used], m['uv'][used], [wl[i] for i in used]
        # the head / body seam (the neck piece of M_Head meets M_Body on the upper chest): the head's weights come from
        # the face transfer, the body's are its own - two touching edges on different weights pull apart in motion
        # (2026-10-22, Brock in game: a gap across the chest). Head vertices on a body vertex take its weights.
        if m['name'] == 'M_Body':
            seam_body = (unit(V), wl)
        elif m['name'] == 'M_Head' and seam_body is not None:
            bp, bw = seam_body
            hp = unit(V)
            welded = 0
            for i0 in range(0, len(hp), 512):
                d2 = ((hp[i0:i0 + 512, None, :] - bp[None, :, :]) ** 2).sum(-1)
                j = d2.argmin(1)
                for k in np.nonzero(d2[np.arange(len(j)), j] < SEAM_WELD ** 2)[0]:
                    wl[i0 + k] = list(bw[j[k]])
                    welded += 1
            report.append('M_Head seam: %d vertices take the body\'s weights' % welded)
        # texture cell
        base = m['material'].split(':')[0]
        tname = {'eye_l': 'eye', 'eye_r': 'eye'}.get(base, base)
        cell = CELLS[tname]
        cpath = find_tex(char, attire, tname, '_color')
        npath = find_tex(char, attire, tname, '_nrm')
        x, y, w, h = cell
        if not sheet_c[y:y + h, x:x + w, :3].any():
            if cpath:
                ci = cell_image(cpath, cell)
            else:
                ci = np.zeros((h, w, 4), np.uint8) + np.array(SOLID.get(tname, (128, 128, 128)) + (255,), np.uint8)
            if tname != 'eyelash' and tname not in CUTOUT:   # (lashes / hair cards keep their own alpha: the cut-out)
                ci[..., 3] = SWEAT.get(tname, 0)
            sheet_c[y:y + h, x:x + w] = ci
            if npath:
                sheet_n[y:y + h, x:x + w, :3] = cell_image(npath, cell, normal=True)[..., :3]
            base_s, k_s = SHINE.get(tname, (20, 0))
            luma = ci[..., :3].astype(np.float64) @ [0.299, 0.587, 0.114]
            sheet_n[y:y + h, x:x + w, 3] = np.clip(base_s + k_s * luma, 0, 254).astype(np.uint8)
        if KIND.get(m['name']) == 'lash':
            lash_cell = cell
        suv = cell_uv(cell, uv)
        kind = KIND.get(m['name'], 'skin')
        groups = split_rigid(tris, wl) if kind == 'skin' else [np.arange(len(tris))]
        for gi, g in enumerate(groups):
            gt = tris[g]
            used = np.unique(gt)
            rm = -np.ones(len(V), int)
            rm[used] = np.arange(len(used))
            gb = sorted({b for v in used for b, _ in wl[v]})
            gpos = {b: k for k, b in enumerate(gb)}
            out_meshes.append(dict(
                name=m['name'] + ('' if len(groups) == 1 else '_%d' % gi), kind=kind,
                positions=np.round(unit(V[used]), 5).tolist(),
                normals=np.round(-N[used] if STOCK_WINDING else N[used], 5).tolist(),
                uvs=np.round(suv[used], 6).tolist(), bones=[names11[b] for b in gb],
                weights=[[[gpos[b], round(wt, 5)] for b, wt in wl[v]] for v in used],
                triangles=(rm[gt][:, [0, 2, 1]] if STOCK_WINDING else rm[gt]).tolist(),
                **({'material': {'cutout': True, 'double_sided': True}} if tname in CUTOUT else {})))
        report.append('%-18s %6d -> %5d verts %5d tris  %d group(s)' % (m['name'], n, len(V), len(tris), len(groups)))
    # the template's mouth inside: meshes warped into his mouth, its teeth / tongue block of the sheets copied
    import io
    tc = {n: d for n, d in W.tex_bundle_read(conv.t[0xc])}
    ta = {n: d for n, d in W.tex_bundle_read(conv.t[0xa])}
    tcol = np.asarray(Image.open(io.BytesIO(tc['color'])).convert('RGBA'))
    tnrm = Image.open(io.BytesIO(ta['normal'])).convert('RGBA')
    tnrm = np.asarray(tnrm.resize((tcol.shape[1], tcol.shape[0]), Image.BILINEAR))
    sx, sy, sw, sh = TMPL_MOUTH_SRC
    cx, cy = TMPL_MOUTH_CELL
    sheet_c[cy:cy + sh, cx:cx + sw] = tcol[sy:sy + sh, sx:sx + sw]
    sheet_n[cy:cy + sh, cx:cx + sw] = tnrm[sy:sy + sh, sx:sx + sw]
    for k, tm in enumerate(FT.template_mouth(template, fit, gain=FACE_GAIN) if TEMPLATE_MOUTH else []):
        uv = tm['uvs'] * [tcol.shape[1], tcol.shape[0]]
        uv = (uv - [sx, sy] + [cx, cy]) / [SHEET[0], SHEET[1]]
        bl = sorted({b for e in tm['weights'] for b, _ in e})
        bp = {b: i for i, b in enumerate(bl)}
        tri = tm['triangles']
        out_meshes.append(dict(
            name='mouth_%d' % k, kind='skin',
            positions=np.round(tm['positions'], 5).tolist(),
            normals=np.round(tm['normals'], 5).tolist(),         # (stock data: already stock-wound)
            uvs=np.round(uv, 6).tolist(), bones=bl,
            weights=[[[bp[b], round(w, 5)] for b, w in e if w > 0] for e in tm['weights']],
            triangles=tri.tolist(),
            material={'specular': tm['specular']} if tm['specular'] is not None else {}))
        report.append('mouth_%d (template)       %5d verts %5d tris' % (k, len(tm['positions']), len(tri)))
    Image.fromarray(sheet_c, 'RGBA').save(os.path.join(out, 'color.png'))
    Image.fromarray(sheet_n, 'RGBA').save(os.path.join(out, 'normal.png'))
    spec = {'version': 1, 'template': template,
            'joints': {k: [round(float(c), 5) for c in v] for k, v in J.items()},
            'attires': [{'textures': {'color': 'color.png', 'normal': 'normal.png'}, 'meshes': out_meshes}],
            '_rock': {'lash_cell': lash_cell, 'origin_cm': origin.tolist()}}
    with open(os.path.join(out, 'model.json'), 'w') as f:     # (one write: the same text, much faster than json.dump)
        f.write(json.dumps(spec))
    if unmapped:
        report.append('folded into a parent bone: ' + ', '.join('%s %.0f' % kv for kv in sorted(unmapped.items())))
    report.append('face fit: landmark error %.3f units, scale %s; %d face bones placed, jaw pivot %s'
                  % (fit['residual'], np.round(fit['scale'], 3), len(fit['joints']), np.round(fit['joints']['d_kuchi'], 2)))
    report.append('vertices %d, meshes %d' % (sum(len(m['positions']) for m in out_meshes), len(out_meshes)))
    return spec, report


def main(argv):
    char, attire, out = argv[:3]
    template = argv[3] if len(argv) > 3 else TEMPLATE
    spec, report = build(char, attire, out, template)
    print('\n'.join(report))


if __name__ == '__main__':
    main(sys.argv[1:])
