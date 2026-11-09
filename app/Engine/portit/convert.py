"""PortIT converter: neutral source model -> SvR 2011 model.json (+ pngs) for the bundled svr11_build.py.

Steps (each from research/FINDINGS.md):
  1. re-pose the legs (2K / AEW thighs 5 deg out, SvR 15 deg) - and AEW's elbows (27.3 -> 20 deg)
  2. space: cm -> SvR 10 cm units, the hips to the origin, the game's axes to SvR's (-Y up, face to -Z)
  3. weights: source bones -> SvR bones (rig_2k: 2K / AEW names, women's breasts -> l_boin / r_boin);
     the head's face area takes the stock SvR head's face weights (face_transfer), so JUDE animates the face
  4. joints: the SvR nodes moved to the wrestler's proportions (and the face pivots to the warped stock ones)
  5. textures - the anti-glaze rule: normal alpha (shine mask) = specular x (1 - roughness)^2, colour alpha
     (sweat mask) = 47 on skin, 0 on clothes; cut-outs keep their opacity; never the source alpha as it is
"""
import json
import math
import os

import numpy as np
from PIL import Image

import face_transfer
import rig_2k

SPEC_SKIN, SPEC_GEAR = 0.37, 0.5        # specular (F0-like) when the source has no specular map
SWEAT_SKIN = 47                          # stock colour alpha mean on skin (131 attires)
SKIN_MASK_MEAN = 20.0                    # stock normal alpha mean on skin (18-22 / 255): skin is scaled to this
GEAR_MASK_CAP = 50.0                     # clothes: the formula, but a mean above this is scaled down (no glaze)
SKIN_KINDS = ('skin', 'head')
BASIS = {'2k': np.eye(3), 'aew': np.array([[1, 0, 0], [0, 0, -1], [0, -1, 0]], float)}


# ------------------------------------------------------------------ pose
def rot(axis, deg):
    a = np.radians(deg)
    x, y, z = np.asarray(axis, float) / np.linalg.norm(axis)
    c, s, C = math.cos(a), math.sin(a), 1 - math.cos(a)
    return np.array([[c + x * x * C, x * y * C - z * s, x * z * C + y * s],
                     [y * x * C + z * s, c + y * y * C, y * z * C - x * s],
                     [z * x * C - y * s, z * y * C + x * s, c + z * z * C]])


def subtree(bones, root_name):
    names = [b['name'] for b in bones]
    if root_name not in names:
        return set()
    r = names.index(root_name)
    out = {r}
    changed = True
    while changed:
        changed = False
        for i, b in enumerate(bones):
            if i not in out and b['parent'] in out:
                out.add(i)
                changed = True
    return out


def world_pos(bones):
    return {b['name']: np.asarray(b['world'])[3, :3].astype(float) for b in bones}


def pose_deltas(bones, space):
    """per source bone (by name): (pivot, 3x3 rotation) of the re-pose, ancestors' first"""
    W = world_pos(bones)
    up = -BASIS[space].T @ np.array([0, -1, 0.0])          # the game's up axis in its own space
    front = BASIS[space].T @ np.array([0, 0, -1.0])        # the face direction
    plan = []
    for side, sgn in (('L', 1), ('R', -1)):
        hip, knee = 'J_Leg_' + side, 'J_Knee_' + side
        if hip in W and knee in W:
            d = W[knee] - W[hip]
            out_deg = math.degrees(math.asin(abs(d[0]) / np.linalg.norm(d)))
            need = 15.0 - out_deg                          # SvR thighs are 15 deg out
            R = rot(front, sgn * need)
            if (R @ d)[0] * sgn < d[0] * sgn:              # (make sure it opens outward)
                R = rot(front, -sgn * need)
            plan.append((hip, W[hip], R))
        sh, el, wr = 'J_Shoulder_' + side, 'J_Elbow_' + side, 'J_Wrist_' + side
        if all(k in W for k in (sh, el, wr)):
            u, f = W[el] - W[sh], W[wr] - W[el]
            bend = math.degrees(math.acos(np.clip(np.dot(u, f) / np.linalg.norm(u) / np.linalg.norm(f), -1, 1)))
            if abs(bend - 20.0) > 2.0:                     # SvR elbows: 20 deg (AEW 27.3; 2K already 20)
                axis = np.cross(u, f)
                plan.append((el, W[el], rot(axis, 20.0 - bend)))
    per = {}
    for name, pivot, R in plan:
        for i in subtree(bones, name):
            per.setdefault(bones[i]['name'], []).append((pivot, R))
    return per, plan


def apply_pose(points, deltas, normals=False):
    p = points.copy()
    for pivot, R in deltas:
        p = (p - (0 if normals else pivot)) @ R.T + (0 if normals else pivot)
    return p


def repose_mesh(me, bones, per):
    names = [b['name'] for b in bones]
    P, N = me.positions, me.normals
    outP, outN = np.zeros_like(P), np.zeros_like(N)
    cache = {}
    for i in range(me.bone_idx.shape[1]):
        w = me.bone_w[:, i][:, None]
        for b in np.unique(me.bone_idx[:, i]):
            sel = me.bone_idx[:, i] == b
            d = per.get(names[b], [])
            if b not in cache:
                cache[b] = d
            outP[sel] += w[sel] * apply_pose(P[sel], d)
            outN[sel] += w[sel] * apply_pose(N[sel], d, True)
    tot = me.bone_w.sum(1)[:, None]
    ok = tot[:, 0] > 0
    outP[ok] /= tot[ok]
    outP[~ok] = P[~ok]
    outN /= np.maximum(np.linalg.norm(outN, axis=1, keepdims=True), 1e-9)
    me.positions, me.normals = outP, outN


def hide_covered_skin(meshes, report, reach=2.0):
    """2K / AEW bodies are whole under the clothes (2K hides covered parts at run time). Body triangles whose three
    corners have clothing within `reach` cm on the OUTSIDE are removed: they never show, they would poke through
    the clothes once both are simplified, and they cost memory."""
    gear = [m for m in meshes if m.kind == 'gear' and not m.cutout and len(m.tris)]
    if not gear:
        return
    G = np.concatenate([m.positions for m in gear])
    for m in meshes:
        if m.kind != 'skin' or not len(m.tris):
            continue
        P, N = m.positions, m.normals
        near = np.zeros(len(P), bool)
        for i in range(0, len(P), 1024):
            d = G[None, :, :] - P[i:i + 1024, None, :]
            dist2 = (d ** 2).sum(2)
            j = dist2.argmin(1)
            dd = d[np.arange(len(j)), j]
            out = (dd * N[i:i + 1024]).sum(1)                   # clothing point along the skin normal
            near[i:i + 1024] = (np.sqrt(dist2[np.arange(len(j)), j]) < reach) & (out > -0.3)
        keep = ~near[m.tris].all(1)
        # one ring of the removed skin stays (the clothing's edge must always lie over skin, never over a hole)
        ring = np.zeros(len(P), bool)
        ring[m.tris[keep].ravel()] = True
        keep |= ring[m.tris].any(1)
        if (~keep).any():
            report.append('%s: %d of %d triangles under clothing removed' % (m.name, int((~keep).sum()), len(m.tris)))
            m.tris = m.tris[keep]


# ------------------------------------------------------------------ space
class Space:
    def __init__(self, bones, space):
        self.B = BASIS[space]
        self.hips = world_pos(bones)['J_Hips']

    def p(self, x):
        return ((np.asarray(x, float) - self.hips) @ self.B.T) * 0.1

    def n(self, x):
        return np.asarray(x, float) @ self.B.T


# ------------------------------------------------------------------ textures
def img(path):
    im = Image.open(path)
    im.load()
    return im


def normal_png(nrm, rough=None, metal=None, spec=SPEC_GEAR, size=None, skin=False):
    """source normal (R,G = X,Y) -> SvR normal png: RGB normal, A = shine mask"""
    a = np.asarray(nrm.convert('RGBA')).astype(np.float32) / 255.0
    x, y = a[..., 0] * 2 - 1, a[..., 1] * 2 - 1
    z = np.sqrt(np.clip(1 - x * x - y * y, 0, 1))
    r = a[..., 2] if rough is None else rough
    mask = np.clip(np.asarray(spec) * (1 - r) ** 2, 0, 1)
    m = float(mask.mean() * 255)
    if skin and m > 0.5:                   # skin: the stock level, the source's own pattern
        mask = np.clip(mask * SKIN_MASK_MEAN / m, 0, 1)
    elif m > GEAR_MASK_CAP:
        mask = mask * GEAR_MASK_CAP / m
    out = np.dstack([a[..., 0], a[..., 1], z * 0.5 + 0.5, mask])
    im = Image.fromarray((out * 255 + 0.5).astype(np.uint8), 'RGBA')
    return im.resize(size, Image.LANCZOS) if size else im, float(mask.mean() * 255)


def bake_vertex_colors(me, size=512):
    """2K hair: the colour is per vertex -> paint it into the UV layout"""
    W = H = size
    out = np.zeros((H, W, 3), np.float32)
    cnt = np.zeros((H, W, 1), np.float32)
    uv = me.uvs * [W - 1, H - 1]
    col = me.colors[:, :3]
    for t in me.tris:
        p = uv[t]
        x0, y0 = np.floor(p.min(0)).astype(int)
        x1, y1 = np.ceil(p.max(0)).astype(int)
        x0, y0, x1, y1 = max(x0, 0), max(y0, 0), min(x1, W - 1), min(y1, H - 1)
        if x1 < x0 or y1 < y0:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
        v0, v1 = p[1] - p[0], p[2] - p[0]
        den = v0[0] * v1[1] - v1[0] * v0[1]
        if abs(den) < 1e-12:
            continue
        dx, dy = gx - p[0][0], gy - p[0][1]
        b1 = (dx * v1[1] - v1[0] * dy) / den
        b2 = (v0[0] * dy - dx * v0[1]) / den
        b0 = 1 - b1 - b2
        inside = (b0 >= -0.02) & (b1 >= -0.02) & (b2 >= -0.02)
        c = b0[..., None] * col[t[0]] + b1[..., None] * col[t[1]] + b2[..., None] * col[t[2]]
        out[gy[inside], gx[inside]] += c[inside]
        cnt[gy[inside], gx[inside]] += 1
    fill = col.mean(0)
    res = np.where(cnt > 0, out / np.maximum(cnt, 1), fill)
    return Image.fromarray((np.clip(res, 0, 1) * 255).astype(np.uint8), 'RGB')


FLAT = {'teeth': (225, 215, 195), 'tongue': (150, 60, 60), 'mouthbag': (95, 35, 35), 'caruncle': (200, 120, 110)}


def mesh_textures(me, outdir, key, report):
    """-> (textures dict for model.json, material dict); writes pngs.
    2K: me.textures = role -> dds path (color, nrm = RG normal + B rough + A metal, rai = hair cut-out ...).
    AEW: me.images = role -> image (color, normal = BC5 RG, orms / orm = R ao, G rough, B metal, A spec) and
    me.scalars (RoughnessMin / Max)."""
    t = me.textures or {}
    ims = getattr(me, 'images', None) or {}
    sc = getattr(me, 'scalars', None) or {}
    mat, tex = {}, {}
    cut = me.cutout
    if 'color' in ims:
        col = ims['color'].convert('RGBA')
    elif 'color' in t:
        col = img(t['color']).convert('RGBA')
    elif me.colors is not None and len(me.tris):
        col = bake_vertex_colors(me).convert('RGBA')
    else:
        col = Image.new('RGBA', (64, 64), FLAT.get(me.name, FLAT.get(me.kind, (128, 128, 128))) + (255,))
    if cut:
        if 'rai' in t:                     # 2K hair: rai G = strand coverage
            col.putalpha(img(t['rai']).convert('RGBA').split()[1].resize(col.size, Image.LANCZOS))
        mat['cutout'] = True               # (AEW hair / lash: the colour's own alpha is the cut-out)
        mat['double_sided'] = True
    p = os.path.join(outdir, key + '_color.png')
    if not cut:
        col = col.convert('RGB')
    col.save(p)
    tex['color'] = os.path.basename(p)
    if me.kind in ('eye', 'lash'):
        return tex, mat
    skin = me.kind in SKIN_KINDS
    spec = SPEC_SKIN if skin else SPEC_GEAR
    nim = None
    if 'nrm' in t:                          # 2K: B = roughness, A = metal
        n_im = img(t['nrm'])
        nim, mean = normal_png(n_im, spec=spec, skin=skin)
        metal = np.asarray(n_im.convert('RGBA'))[..., 3]
    elif 'normal' in ims:                   # AEW: BC5 normal + ORM(S)
        n_im = ims['normal']
        orm = ims.get('orms') or ims.get('orm')
        rough = metal = None
        if orm is not None:
            o = np.asarray(orm.convert('RGBA').resize(n_im.size, Image.BILINEAR)).astype(np.float32) / 255.0
            lo, hi = float(sc.get('RoughnessMin', 0.0)), float(sc.get('RoughnessMax', 1.0))
            rough = np.clip(lo + o[..., 1] * (hi - lo), 0, 1)
            metal = (o[..., 2] * 255).astype(np.uint8)
            if 'orms' in ims:
                spec = o[..., 3]
        if rough is None:                   # (no ORM found: a BC5 normal has no roughness - assume matte)
            rough = np.full(n_im.size[::-1], 0.75 if me.kind != 'hair' else 0.85, np.float32)
        nim, mean = normal_png(n_im, rough=rough, spec=spec, skin=skin)
    if nim is not None:
        np_path = os.path.join(outdir, key + '_normal.png')
        nim.save(np_path)
        mat['normal'] = os.path.basename(np_path)
        report.append('%s: shine mask mean %.1f / 255 (stock skin 18-22)' % (me.name, mean))
        if metal is not None and (np.asarray(metal) > 230).mean() > 0.2 and not skin:
            report.append('%s: %.0f%% metal - a reflective (yReflect) look would suit it'
                          % (me.name, 100 * (np.asarray(metal) > 230).mean()))
    else:
        flat = Image.new('RGBA', (64, 64), (128, 128, 255, int(255 * (SPEC_GEAR * 0.5 ** 2))))
        np_path = os.path.join(outdir, key + '_normal.png')
        flat.save(np_path)
        mat['normal'] = os.path.basename(np_path)
    mat['sweat'] = SWEAT_SKIN if skin else 0
    return tex, mat


# ------------------------------------------------------------------ weights
def mapped_weights(me, template, female):
    bones = [(b['name'], me.bones[b['parent']]['name'] if b['parent'] >= 0 else None) for b in me.bones]
    res = rig_2k.match(bones, template, female)
    names = [b['name'] for b in me.bones]
    out, unknown = [], 0.0
    for bi, bw in zip(me.bone_idx, me.bone_w):
        d = {}
        for b, w in zip(bi, bw):
            if w > 0:
                n = names[b]
                if n in res['unknown']:
                    unknown += w
                d[res['map'][n]] = d.get(res['map'][n], 0.0) + float(w)
        out.append(d)
    return out, unknown / max(len(out), 1), res


# ------------------------------------------------------------------ main
def template_nodes(pac):
    import ch_tool
    import jboy
    import svrfmt
    h, groups = ch_tool.epk8_read(open(pac, 'rb').read())
    blob = groups[0][1][0][1]
    raw = svrfmt.unbpe([x for i, x in svrfmt.pach_read(blob) if i == 0][0])
    return [n['name'] for n in jboy.read(raw).nodes]


def convert(src, game_dir, outdir, female=False, template=None, face=True, skip_variants=True, log=print):
    os.makedirs(outdir, exist_ok=True)
    template = template or ('ch109' if female else 'ch104')
    pac = os.path.join(game_dir, 'pac', 'ch', template + '.pac')
    tnames = template_nodes(pac)
    report = []
    meshes = [m for m in src['meshes'] if not (skip_variants and getattr(m, 'variant', False))]
    skipped = [m.material for m in src['meshes'] if m not in meshes]
    if skipped:
        report.append('left out (alternate looks): ' + ', '.join(skipped))
    # 1. pose
    per, plan = pose_deltas(src['bones'], src['space'])
    for name, pivot, R in plan:
        report.append('re-posed %s by %.1f deg' % (name, math.degrees(math.acos(np.clip((np.trace(R) - 1) / 2, -1, 1)))))
    for m in meshes:
        repose_mesh(m, m.bones, pose_deltas(m.bones, src['space'])[0])
    hide_covered_skin(meshes, report)
    posed_bones = []
    W = world_pos(src['bones'])
    for b in src['bones']:
        p = apply_pose(W[b['name']][None], per.get(b['name'], []))[0]
        posed_bones.append(dict(b, pos=p))
    sp = Space(src['bones'], src['space'])
    # 3. weights (+ face)
    face_w = face_piv = None
    head = next((m for m in meshes if m.kind == 'head'), None)
    if face and head is not None:
        Wp = {b['name']: b['pos'] for b in posed_bones}
        lm = {'l_eye': Wp['J_Eye_L'], 'r_eye': Wp['J_Eye_R'], 'atama': Wp['J_Head'], 'd_kuchi': Wp['J_Jaw']}
        stock = face_transfer.stock_head('ch104', game_dir)
        if getattr(head, 'shapes', None):
            face_w, dist, face_piv, keys = face_transfer.transfer_tps(head.positions, lm, head.shapes, stock, src['space'])
        else:
            face_w, dist, face_piv, _, _ = face_transfer.transfer(head.positions, lm, stock, src['space'])
        report.append('face: stock head fitted, median gap %.2f cm' % float(np.median(dist)))
    out_meshes = []
    unknown_total = 0.0
    for k, m in enumerate(meshes):
        w, unk, res = mapped_weights(m, tnames, female)
        unknown_total += unk
        if m is head and face_w is not None:
            n = 0
            for v in range(len(w)):
                if set(w[v]) <= {'atama', 'd_kuchi'}:
                    w[v] = {b: x for b, x in face_w[v].items() if b in tnames}
                    n += 1
            report.append('face: %d head vertices take the stock face weights' % n)
        bones = sorted({b for d in w for b in d})
        bix = {b: i for i, b in enumerate(bones)}
        key = '%02d_%s' % (k, m.name)
        tex, mat = mesh_textures(m, outdir, key, report)
        kind = {'eye': 'eye', 'lash': 'lash', 'hair': 'hair'}.get(m.kind, 'skin')
        out_meshes.append({
            '_main': [max(d, key=d.get) if d else 'root' for d in w],
            'name': key, 'kind': kind,
            'positions': sp.p(m.positions).round(6).tolist(), 'normals': sp.n(m.normals).round(6).tolist(),
            'uvs': np.asarray(m.uvs, float).round(6).tolist(), 'bones': bones,
            'weights': [[[bix[b], round(x, 6)] for b, x in d.items()] for d in w],
            'triangles': np.asarray(m.tris, int).tolist(), 'textures': tex, 'material': mat})
    # 4. joints: the mapped source joints (nearest the root wins), face pivots
    joints = {}
    for b in posed_bones:
        t = rig_2k.direct_map().get(b['name'])
        if t and t in tnames and t not in joints:
            joints[t] = sp.p(b['pos']).round(6).tolist()
    if face_piv:
        for b, p in face_piv.items():
            if b in tnames and b not in ('atama',):
                joints[b] = sp.p(p).round(6).tolist()
    model = {'version': 1, 'template': pac, 'joints': joints,
             'attires': [{'meshes': out_meshes}]}
    report.append('weights on unknown bones: %.2f%% (they go to the hips)' % (100 * unknown_total / max(len(meshes), 1)))
    return model, report


# ------------------------------------------------------------------ budget
KEEP_WHOLE = ('eye', 'lash')            # never simplified
SMALL = 600                             # meshes this small stay whole


HAIR_SHARE = 0.18          # hair / beard cards: at most this share of the vertex budget
MOUTH_VERTS = 600          # teeth + tongue + mouth bag together (rarely seen)


def plan_targets(model, vtarget):
    """vertex target per mesh: whole parts first (eyes, lashes, small), then the mouth, then hair (capped), and
    the rest - body, head, clothes - shares what is left (the head x1.4)"""
    ms = model['attires'][0]['meshes']
    n = [len(np.unique(np.asarray(m['triangles']))) if m['triangles'] else 0 for m in ms]
    tgt = [None] * len(ms)
    left = vtarget
    for i, m in enumerate(ms):
        if m['kind'] in KEEP_WHOLE or n[i] <= SMALL:
            tgt[i] = n[i]
            left -= n[i]
    mouth = [i for i, m in enumerate(ms) if tgt[i] is None and any(x in m['name'] for x in ('teeth', 'tongue', 'mouthbag'))]
    mt = sum(n[i] for i in mouth)
    for i in mouth:
        tgt[i] = min(n[i], int(MOUTH_VERTS * n[i] / max(mt, 1)))
        left -= tgt[i]
    hair = [i for i, m in enumerate(ms) if tgt[i] is None and m['kind'] == 'hair']
    ht = sum(n[i] for i in hair)
    others = sum(n[i] for i, m in enumerate(ms) if tgt[i] is None and m['kind'] != 'hair')
    # hair: its share - or all the room the rest leaves when the rest fits whole
    hcap = min(ht, max(int(HAIR_SHARE * vtarget), left - others))
    for i in hair:
        tgt[i] = int(hcap * n[i] / max(ht, 1))
        left -= tgt[i]
    rest = [i for i in range(len(ms)) if tgt[i] is None]
    w = {i: n[i] * (1.4 if 'head' in ms[i]['name'] else 1.0) for i in rest}
    tw = sum(w.values()) or 1
    for i in rest:
        tgt[i] = min(n[i], max(SMALL // 2, int(max(left, 0) * w[i] / tw)))
    return tgt, n


def reduce_model(model, vtarget):
    """a copy of the model simplified to about vtarget vertices in all (plan_targets)"""
    import simplify
    out = dict(model)
    meshes = []
    tgt, n = plan_targets(model, vtarget)
    for m, keep, nm in zip(model['attires'][0]['meshes'], tgt, n):
        if keep >= nm:
            meshes.append(m)
            continue
        main = m['_main']
        bid = {b: i for i, b in enumerate(sorted(set(main)))}
        cards = m['kind'] == 'hair'
        P = np.asarray(m['positions'])
        T = np.asarray(m['triangles'])
        if cards:                                  # cards: drop the least visible ones first, then shorten the rest
            T = simplify.cull_cards(P, T, int(keep * 1.4))
        to, T = simplify.simplify(P, T, keep, np.array([bid[b] for b in main]), cards=cards)
        if cards and len(np.unique(T)) > keep:
            T = simplify.cull_cards(P, T, keep)
        used, T2 = simplify.compact(to, T)
        mm = dict(m)
        for k in ('positions', 'normals', 'uvs', 'weights', '_main'):
            mm[k] = [m[k][i] for i in used]
        mm['triangles'] = T2.tolist()
        meshes.append(mm)
    out['attires'] = [dict(model['attires'][0], meshes=meshes)]
    return out


def write_model(model, outdir):
    clean = dict(model)
    clean['attires'] = [dict(a, meshes=[{k: v for k, v in m.items() if not k.startswith('_')} for m in a['meshes']])
                        for a in model['attires']]
    path = os.path.join(outdir, 'model.json')
    with open(path, 'w') as f:
        json.dump(clean, f)
    return path


def build(model, outdir, budget=190, out_name='ch.pac', log=print, python=None):
    """simplify until the bundled builder's page count fits the budget; -> (pac path, report dict, kept share)"""
    import subprocess
    import sys
    builder = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'svr', 'svr11_build.py')
    nv = sum(len(np.unique(np.asarray(m['triangles']))) for m in model['attires'][0]['meshes'] if m['triangles'])
    # (pages: 44 bytes a vertex + indices; the template's blood overlays ~21 pages; ~8% for page rounding)
    vt = min(nv, int((budget - 22) * 4096 / 1.08 / 50.0))
    pac = os.path.join(outdir, out_name)
    for attempt in range(6):
        red = reduce_model(model, vt)
        path = write_model(red, outdir)
        r = subprocess.run([python or sys.executable, builder, path, pac], capture_output=True, text=True)
        try:
            rep = json.loads(r.stdout)
        except ValueError:
            raise RuntimeError('builder failed: ' + (r.stderr or r.stdout)[-1500:])
        if not rep.get('ok'):
            raise RuntimeError(rep.get('error', 'build failed'))
        pages = rep['attires'][0]['pages']
        nv2 = sum(len(m['positions']) for m in red['attires'][0]['meshes'])
        log('build %d: %d of %d vertices -> %d pages (budget %d)' % (attempt + 1, nv2, nv, pages, budget))
        if pages <= budget:
            return pac, rep, nv2 / max(nv, 1)
        vt = int(vt * budget / pages * 0.97)
    raise RuntimeError('could not fit %d pages' % budget)
