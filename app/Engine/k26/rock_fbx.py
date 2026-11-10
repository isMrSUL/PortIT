"""PortIT's stand-in for tools/k26_porter/rock_fbx.py: the same build() the proven rock_model.py pipeline calls,
fed by PortIT's own .mcd reader (readers/mcd_parse.py, checked byte-exact against a reference model library on 28 files) instead
of the DLL. covered() and texture_for() and the hide-under-gear rule are the original's, unchanged.

build() returns what the original returned: meshes [{name (M_...), verts, normals (+Y up: TURN), uv (the DLL's
convention: 1 - stored v), tris (the DLL's winding), material (the object name: outfit, hair_ALPHA ...), texture,
weights {bone index: per-vertex weight}}] and bones [{name, src, parent, world (4x4, column convention, turned)}].
Attire-only bones (hair / cloth chains) are folded into their nearest ancestor the base skeleton has.
"""
import glob
import os
import re
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), 'readers'))
import rock_names as RN  # noqa: E402
from mcd_parse import Mcd  # noqa: E402

DROP_MESHES = {'M_Teeth', 'M_Tongue', 'M_MouthBag'}
TURN = np.diag([1.0, -1.0, -1.0])          # -Y up -> +Y up, a rotation (det +1)
TEX_MAX = 2048
# meshes that never hide the body under them (skin, face parts; a character's hair cards)
NOT_GEAR = {'M_Body', 'M_Head', 'M_EYE', 'M_TransB_eyelash', 'M_Teeth', 'M_Tongue', 'M_MouthBag'}
HIDE_OUT_CM, HIDE_IN_CM = 4.0, 8.0   # skin under the gear (out) or poking through it (in) is removed
SKIP_VARIANTS = True                 # PortIT: 2K's alternate looks (M_Outfit_strapsDown next to M_Outfit) left out


def texture_for(material, folders, outdir, extra=None):
    """extra: the colour texture the mod's .mtls names for this material (2K20 - 2K24 mods; tried last)"""
    base = material.split(':')[0]
    cands = [base, base.rsplit('_', 1)[0] if base.endswith(('_l', '_r')) else base]
    if extra and extra.lower().endswith('_color'):
        cands.append(extra[:-len('_color')])
    for folder in folders:
        for c in cands:
            hits = [p for p in glob.glob(os.path.join(folder, 'Textures', '*')) if
                    os.path.basename(p).lower().split('.')[0] == (c + '_color').lower()]
            if hits:
                name = c + '_color.png'
                dst = os.path.join(outdir, 'textures', name)
                if not os.path.exists(dst):
                    im = Image.open(hits[0])
                    im.load()
                    im = im.convert('RGBA')
                    if max(im.size) > TEX_MAX:
                        k = TEX_MAX / max(im.size)
                        im = im.resize((int(im.size[0] * k), int(im.size[1] * k)), Image.LANCZOS)
                    im.save(dst)
                return 'textures/' + name
    return None


def covered(verts, normals, att_tris_xyz, out_cm=6.0, in_cm=1.5, cell=8.0):
    """bool per body vertex: an attire surface lies just outside it along its normal (within out_cm), or just
    inside it (within in_cm: the skin already pokes through). 2K26 hides such skin with the attire's
    hidden-polygon list (HPL); this mod ships empty ones, so in SvR 2011 the body showed through the gear."""
    A, B, Cc = att_tris_xyz[:, 0], att_tris_xyz[:, 1], att_tris_xyz[:, 2]
    cen = (A + B + Cc) / 3
    grid = {}
    for i, k in enumerate(map(tuple, np.floor(cen / cell).astype(int))):
        grid.setdefault(k, []).append(i)
    e1, e2 = B - A, Cc - A
    res = np.zeros(len(verts), bool)
    for vi, (p, n) in enumerate(zip(verts, normals)):
        nn = np.linalg.norm(n)
        if nn < 1e-6:
            continue
        d = n / nn
        k0 = np.floor(p / cell).astype(int)
        cand = []
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    cand += grid.get((k0[0] + dx, k0[1] + dy, k0[2] + dz), [])
        if not cand:
            continue
        c = np.array(cand)
        h = np.cross(d, e2[c])
        a = np.einsum('ij,ij->i', e1[c], h)
        ok = np.abs(a) > 1e-9
        f = np.where(ok, 1.0 / np.where(ok, a, 1), 0)
        s = p - A[c]
        u = f * np.einsum('ij,ij->i', s, h)
        q = np.cross(s, e1[c])
        v = f * (q @ d)
        t = f * np.einsum('ij,ij->i', e2[c], q)
        hit = ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (t <= out_cm) & (t >= -in_cm)
        res[vi] = bool(hit.any())
    return res


def object_names(m, mdir):
    """per mesh: its object / material file name (the original took it from the DLL's groups: 'outfit:skinned')"""
    jm = {os.path.splitext(os.path.basename(p))[0].lower(): os.path.splitext(os.path.basename(p))[0]
          for p in glob.glob(os.path.join(mdir, 'Materials', '*.jmtl'))}
    objs = {re.sub(r'(:skinned)?$', '', o[0], flags=re.I).lower(): re.sub(r'(:skinned)?$', '', o[0], flags=re.I)
            for o in m.objects}
    names = dict(objs)
    names.update(jm)
    out = []
    for mb in m.meshes:
        key = re.sub(r'^m_', '', mb['material'].lower())
        cand = [s for s in names if re.sub(r'_alpha$', '', s) == key] or \
               [s for s in names if s.startswith(key) or key.endswith(re.sub(r'_alpha$', '', s))] or \
               [s for s in names if re.sub(r'_alpha$', '', s) in key]
        # (no name match: the material the mesh's triangles use - older mods name meshes freely, PortIT 2026-11-10)
        out.append(names[sorted(cand, key=len)[0]] if cand else mb.get('object') or key)
    return out


def attire_mcd(char, attire):
    d = os.path.join(char, attire)
    if os.path.isfile(d):
        return d
    # (.mdl: 2K20 - 2K24 models, PortIT 2026-11-10)
    c = [p for p in glob.glob(os.path.join(d, '*')) if p.lower().endswith(('.mcd', '.mdl'))]
    if not c:
        raise ValueError('no .mcd / .mdl in %s' % d)
    return sorted(c, key=lambda p: (os.path.basename(p).lower() not in ('attire.mcd', 'attire.mdl'), p))[0]


def base_mcd(char):
    """BaseModel/baseModel.mcd (2K25 / 2K26) or baseModel.mdl (2K20 - 2K24)"""
    d = os.path.join(char, 'BaseModel')
    for f in sorted(os.listdir(d)) if os.path.isdir(d) else []:
        if f.lower() in ('basemodel.mcd', 'basemodel.mdl'):
            return os.path.join(d, f)
    return os.path.join(d, 'baseModel.mcd')


def build(char, attire, out, want_bones=False):
    os.makedirs(os.path.join(out, 'textures'), exist_ok=True)
    base = Mcd(base_mcd(char))
    apath = attire_mcd(char, attire)
    att = Mcd(apath)
    adir = os.path.dirname(apath)
    src = base.bones
    keep = [i for i, b in enumerate(src) if b['name'] not in RN.LOCATORS]
    new = {i: k for k, i in enumerate(keep)}
    C4 = np.eye(4)
    C4[:3, :3] = TURN
    bones = []
    for i in keep:
        b = src[i]
        p = b['parent'] if b['parent'] >= 0 else None
        while p is not None and p not in new:
            p = src[p]['parent'] if src[p]['parent'] >= 0 else None
        world = C4 @ np.asarray(b['world']).T            # row-vector bind matrix -> column convention, turned
        bones.append(dict(name=RN.rename(b['name']), src=b['name'], parent=new.get(p) if p is not None else None,
                          world=world))
    by_name = {src[i]['name']: new[i] for i in keep}
    meshes, report = [], []
    import mtls
    colors = mtls.color_maps(char)
    for model, folder, mpath in ((base, os.path.join(char, 'BaseModel'), None), (att, adir, apath)):
        objs = object_names(model, folder)
        mats = [mb['material'] for mb in model.meshes]
        for k, mb in enumerate(model.meshes):
            name = mb['material']
            if name in DROP_MESHES or not model.lods:
                continue
            if SKIP_VARIANTS and any(name.lower() != o.lower() and name.lower().startswith(o.lower() + '_') for o in mats):
                report.append('%-18s left out (an alternate look)' % name)
                continue
            # (PortIT 2026-11-09: S_<x> beside M_<x> is 2K's low-detail cloth-physics copy of that piece, never drawn in
            #  2K - kept, it sat inside the real piece as a grey shell: AJ Styles 2016's S_Jacket)
            if name[:2].lower() == 's_' and ('m_' + name[2:]).lower() in {o.lower() for o in mats}:
                report.append('%-18s left out (2K cloth-physics copy of M_%s)' % (name, name[2:]))
                continue
            # (PortIT 2026-11-07: a mesh without positions, normals or texture coordinates - Kevin Owens / LA Knight
            #  2K26 have one - is left out with a line, not a crash)
            sp, sn, su = model.stream(k, 'POSITION'), model.stream(k, 'NORMAL'), model.uvs(k)
            if sp is None or sn is None or su is None:
                report.append('%-18s left out (no %s in the 2K model)' % (name, 'positions' if sp is None else
                                                                          'normals' if sn is None else 'texture coordinates'))
                continue
            verts = sp.astype(np.float64)
            nrm = sn.astype(np.float64)
            uv = su.astype(np.float64)
            tris = np.asarray(model.lods[0][k]['triangles'], np.int64)
            # weights by the base skeleton's bones; a bone only the attire has folds into its nearest base ancestor
            w = {}
            names = [b['name'] for b in model.bones]
            for col in range(mb['skin_bones'].shape[1]):
                bi, bw = mb['skin_bones'][:, col], mb['skin_weights'][:, col]
                for b in np.unique(bi[bw > 0]):
                    j = int(b)
                    while j >= 0 and names[j] not in by_name:
                        j = model.bones[j]['parent']
                    if j < 0:
                        continue
                    t = by_name[names[j]]
                    w[t] = w.get(t, 0) + np.where(bi == b, bw, 0.0)
            mat = objs[k]
            tex = texture_for(mat, [folder, os.path.join(char, 'BaseModel'), adir], out,
                              extra=colors.get(re.sub(r'(?i):skinned$', '', mat).lower()))
            meshes.append(dict(name=name, verts=verts @ TURN.T, normals=nrm @ TURN.T, uv=uv, tris=tris,
                               material=mat, texture=tex, weights=w))
            if name == 'M_Head':
                side = lip_side_au27(mb, len(verts))
                if side is not None:
                    meshes[-1]['lip_src'] = side
                    meshes[-1]['jaw_src'] = lip_side_au27(mb, len(verts), amount=True)
                else:
                    # (no usable jaw-open shape - 2K26 Bray Wyatt #1205's face shapes are empty: the touching lips'
                    #  own point pairs tell the lips apart)
                    eyes = [b['world'][:3, 3] for b in bones if b['src'] in ('J_Eye_L', 'J_Eye_R')]
                    if len(eyes) == 2:
                        side = lip_side_geometric(meshes[-1]['verts'], tris, eyes[0], eyes[1])
                        if side is not None:
                            meshes[-1]['lip_src'] = side
            report.append('%-18s %6d verts %6d tris  %-12s %s  %d bones' % (name, len(verts), len(tris), mat, tex, len(w)))
    hide_body(meshes, report)
    print('\n'.join(report))
    return (meshes, bones) if want_bones else meshes


def lip_side_au27(mb, n, amount=False):
    """per head vertex: +1 on the jaw / lower lip, -1 off it - read from 2K's own jaw-open blend shape (FACS AU27,
    'au027_u': the skin it moves down (native +Y) is the jaw's), or None without one. The face transfer takes each
    lip vertex's side from this, not from walking the lips' edge (2K26 CM Punk: the walk changed lips where they
    touch, half his upper lip went with the jaw, 2026-11-09)"""
    # (jaw_dn: the WWE 2K19 - 2K22 packs' own jaw-open shape - it moves the chin down 4 - 5 cm like AU27)
    s = next((s for s in (mb.get('shapes') or []) if s['name'].lower() in ('au027_u', 'au026_u', 'au027', 'jaw_dn')), None)
    if s is None:
        return None
    d = np.zeros(n)
    idx = np.asarray(s['index'])
    d[idx] = np.maximum(np.asarray(s['delta']).reshape(-1, 3)[:, 1], 0)
    if d.max() < AU27_MIN_CM:      # (an empty shape: Bray Wyatt #1205's moves 0.06 cm - no data)
        return None
    j = d / d.max()
    if amount:      # (how far each vertex goes with the jaw in 2K's own jaw-open shape, 0 - 1)
        return j
    return np.where(j > 0.3, 1, -1)     # (the upper lip's edge moves a little in AU27 (10 - 30%): upper)


AU27_MIN_CM = 0.5


def lip_side_geometric(V, tris, eye_l, eye_r, radius=4.0):
    """per head vertex -1 upper lip / +1 lower lip, for heads with touching lips and no jaw-open shape: where the
    lips touch the mesh has two points at one place, one on each lip's surface - the one whose own neighbours lie
    above it is the upper lip's. Those pairs (below the eyes, near the middle, in front) seed the two lips; every
    vertex within radius (cm, along the surface) takes the nearer lip, the rest above / below the seeds' height.
    rock_fbx space: +Y up, the face toward +Z, cm. -> None without such pairs"""
    import heapq
    V = np.asarray(V, float)
    mid = (np.asarray(eye_l) + np.asarray(eye_r)) / 2
    iod = float(np.linalg.norm(np.asarray(eye_l) - np.asarray(eye_r)))
    nb = {}
    for t in tris:
        for a in t:
            for b in t:
                if a != b:
                    nb.setdefault(int(a), set()).add(int(b))
    groups = {}
    for i, p in enumerate(map(tuple, np.round(V / 1e-3).astype(np.int64))):
        groups.setdefault(p, []).append(i)
    seeds = {}
    for g in groups.values():
        if len(g) != 2:
            continue
        p = V[g[0]]
        if not (0.6 * iod < mid[1] - p[1] < 2.2 * iod and abs(p[0] - mid[0]) < 0.8 * iod and p[2] > mid[2] - 0.3 * iod):
            continue
        dy = [np.mean([V[x][1] for x in nb.get(i, ())]) - V[i][1] if nb.get(i) else 0.0 for i in g]
        if dy[0] * dy[1] < 0:
            for i, d in zip(g, dy):
                seeds[i] = -1 if d > 0 else 1
    if sum(1 for s in seeds.values() if s < 0) < 3 or sum(1 for s in seeds.values() if s > 0) < 3:
        return None
    dist, lab, heap = {}, {}, []
    for i, s in seeds.items():
        dist[i], lab[i] = 0.0, s
        heapq.heappush(heap, (0.0, i))
    while heap:
        d, a = heapq.heappop(heap)
        if d > dist.get(a, 1e18) or d > radius:
            continue
        for b in nb.get(a, ()):
            nd = d + float(np.linalg.norm(V[a] - V[b]))
            if nd < dist.get(b, 1e18) and nd <= radius:
                dist[b], lab[b] = nd, lab[a]
                heapq.heappush(heap, (nd, b))
    y0 = float(np.mean([V[i][1] for i in seeds]))
    return np.array([lab.get(i, -1 if V[i][1] > y0 else 1) for i in range(len(V))])


def hide_body(meshes, report):
    """the body skin under the gear removed (PortIT 2026-11-09: a function of its own - the AEW bridge,
    aew_bridge.py, runs it on its meshes too)"""
    # hide the body under the gear (the mod's hidden-polygon lists are empty)
    gear = [m for m in meshes if m['name'] not in NOT_GEAR | DROP_MESHES]
    if gear:
        att = np.concatenate([m['verts'][m['tris']] for m in gear])
        for m in meshes:
            if m['name'] != 'M_Body':
                continue
            hid = covered(m['verts'], m['normals'], att, HIDE_OUT_CM, HIDE_IN_CM)
            # a triangle goes only when its middle is covered too (kneepad straps leave gaps at the back)
            cen = m['verts'][m['tris']].mean(1)
            cn = m['normals'][m['tris']].mean(1)
            mid = covered(cen, cn, att, HIDE_OUT_CM, HIDE_IN_CM)
            keep_t = ~(hid[m['tris']].all(axis=1) & mid)
            used = np.zeros(len(m['verts']), bool)
            used[m['tris'][keep_t].ravel()] = True
            remap = -np.ones(len(m['verts']), int)
            remap[used] = np.arange(used.sum())
            n0, t0 = len(m['verts']), len(m['tris'])
            m['tris'] = remap[m['tris'][keep_t]]
            m['verts'], m['normals'], m['uv'] = m['verts'][used], m['normals'][used], m['uv'][used]
            m['weights'] = {b: w[used] for b, w in m['weights'].items() if w[used].max() > 0}
            report.append('M_Body under the gear removed: %d of %d vertices, %d of %d triangles'
                          % (n0 - len(m['verts']), n0, t0 - len(m['tris']), t0))
