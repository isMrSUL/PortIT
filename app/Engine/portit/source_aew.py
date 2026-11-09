"""AEW Fight Forever (PS4, UE 4.27 IoStore) wrestler -> PortIT's neutral source model, read straight from the game's
containers (nothing is extracted to disk except a temp copy of the needed packages).

One SkeletalMesh per attire (WrestlerData/<M|F><id>_<attire>_<name>/...); its sections are the meshes; each
section's material instance (MI_*) names its textures: Base_color / BaseColor, Normal, ORMS (skin, hair:
aO / Roughness / Metallic / Specular) or ORM (gear). Textures are PS4-tiled (aew_tex.untile_ps4).
"""
import glob
import os
import re
import tempfile

import numpy as np
from PIL import Image

import aew_mesh
import aew_tex
from ioextract import Container
from zenpkg import Pkg, Reader

_containers = {}


def containers(paks):
    if paks not in _containers:
        cs = []
        for u in sorted(glob.glob(os.path.join(paks, '*.utoc'))):
            try:
                cs.append(Container(u))
            except Exception:
                pass
        _containers[paks] = cs
    return _containers[paks]


_lower = {}


def find(paks, suffix):
    """the package whose path ends with suffix (case ignored: materials say 'texture', containers 'Texture')"""
    if paks not in _lower:
        _lower[paks] = [(c, {n.lower(): (i, n) for i, n in c.names.items()}) for c in containers(paks)]
    suf = suffix.lower()
    for c, names in _lower[paks]:
        for low, (idx, name) in names.items():
            if low.endswith(suf):
                return c, idx, name
    return None


def read_file(paks, name_suffix, tmp):
    hit = find(paks, name_suffix)
    if not hit:
        return None
    c, idx, name = hit
    p = os.path.join(tmp, os.path.basename(name))
    open(p, 'wb').write(c.read(idx))
    ub = (name[:-7] + '.ubulk').lower()
    by = {n.lower(): i for i, n in c.names.items()}
    if name.endswith('.uasset') and ub in by:
        open(p[:-7] + '.ubulk', 'wb').write(c.read(by[ub]))
    return p


def roster(paks):
    """-> [{id: folder, name, female, attire}] every wrestler attire folder that has a SkeletalMesh"""
    out = {}
    for c in containers(paks):
        for name in c.names.values():
            m = re.search(r'WrestlerData/([MF]\d{4}_\d+_[^/]+)/', name)
            if m:
                f = m.group(1)
                if f not in out:
                    parts = f.split('_')
                    out[f] = {'id': f, 'name': ' '.join(parts[2:]).replace('_', ' '), 'female': f.startswith('F'),
                              'attire': parts[1]}
    return sorted(out.values(), key=lambda r: (r['name'], r['attire']))


def mesh_package(paks, folder):
    """the attire's SkeletalMesh package name (most are <folder>.uasset; a few differ - pick the biggest package
    in the folder that is not a material / texture / physics / PAL / SW one)"""
    best = None
    for c in containers(paks):
        for idx, name in c.names.items():
            if '/WrestlerData/%s/' % folder not in name or not name.endswith('.uasset'):
                continue
            tail = name.split('/WrestlerData/%s/' % folder)[1]
            if '/' in tail or re.search(r'(Physics|PAL_|_SW_)', tail):
                continue
            size = c.toc.offlen[idx][1] if hasattr(c.toc, 'offlen') else 0
            if best is None or size > best[0]:
                best = (size, name)
    return best[1] if best else None


def quat_mat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y)],
                     [2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x)],
                     [2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)]])


def material_textures(paks, mi_name, tmp, mi_folder=None):
    """MI package -> {role: PIL image} (roles: color, normal, orm (+ 'has_spec' when ORMS))"""
    # mi_name: the full /Game/... path (every wrestler has an MI_Skin: never look one up by name alone)
    rel = mi_name.split('/Game/', 1)[1].split('/', 1)[1] if mi_name.startswith('/Game/') else mi_name
    p = read_file(paks, '/' + rel + '.uasset', tmp)
    if not p:
        return {}, {}
    pkg = Pkg(open(p, 'rb').read())
    props = {}
    for e, s, end in pkg.export_data():
        props = Reader(pkg, s, end).props()
        break
    # the MI's import list also names the parent material's placeholders (dummy_col, common_checker_*, engine
    # defaults) and shared textures: skip the placeholders, prefer the wrestler's own folder
    bad = ('dummy', 'checker', '/_concept/', '/engine/', 't_default', 'blood', 'bruise', 'wrinkle', 'pore_normal')
    imports = [n for n in pkg.names if n.startswith('/Game/') and '/texture/' in n.lower()
               and not any(b in n.lower() for b in bad)]
    own = '/' + mi_folder.lower() + '/' if mi_folder else None
    imports.sort(key=lambda n: (0 if own and own in n.lower() else 1, n))
    scal = {t['ParameterInfo']['Name']: t['ParameterValue'] for t in props.get('ScalarParameterValues', [])
            if isinstance(t.get('ParameterValue'), float)}
    out = {}
    for n in imports:
        stem = n.rsplit('/', 1)[1]
        low = stem.lower()
        role = ('pupil' if low.endswith('_pupil_col') else 'pupilmask' if low.endswith('_pupil_msk') else
                'color' if low.endswith('_col') else 'normal' if low.endswith('_n') else
                'orms' if low.endswith('_orms') else 'orm' if low.endswith('_orm') else
                'skinmask' if low.endswith('_smsk') else None)
        if not role or role in out:
            continue
        rel = n.split('/Game/', 1)[1]                      # (read the exact package, not any of that name)
        tp = read_file(paks, '/' + rel.split('/', 1)[1] + '.uasset', tmp) if '/' in rel else None
        tp = tp or read_file(paks, '/%s.uasset' % stem, tmp)
        if not tp:
            continue
        try:
            t = aew_tex.read(tp)
            m = t['mips'][0]
            data = aew_tex.untile_ps4(t['fmt'], m['w'], m['h'], aew_tex.mip_bytes(t, 0))
            out[role] = aew_tex.to_image(t['fmt'], m['w'], m['h'], data).convert('RGBA')
        except Exception:
            pass
    if 'pupil' in out and 'color' in out:          # AEW eyes: the white + the iris / pupil layer (by its mask)
        white = out['color'].convert('RGB')
        pupil = out['pupil'].convert('RGB').resize(white.size, Image.LANCZOS)
        mask = (out.get('pupilmask') or out['pupil']).convert('RGBA').resize(white.size, Image.LANCZOS)
        m = mask.split()[0] if 'pupilmask' in out else mask.split()[3]
        out['color'] = Image.composite(pupil, white, m).convert('RGBA')
    return out, scal


def hair_level(im, level):
    """the colour map x Level, in linear light (alpha kept)"""
    a = np.asarray(im.convert('RGBA')).astype(np.float64) / 255
    c = a[..., :3]
    lin = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4) * level
    lin = np.clip(lin, 0, 1)
    a[..., :3] = np.where(lin <= 0.0031308, 12.92 * lin, 1.055 * lin ** (1 / 2.4) - 0.055)
    return Image.fromarray((a * 255 + 0.5).astype(np.uint8), 'RGBA')


class SourceMesh:
    def __init__(self, **kw):
        self.__dict__.update(kw)


def kind_of(slot):
    s = slot.lower()
    if 'eye_ov' in s:
        return 'overlay'
    if 'eyelash' in s:
        return 'lash'
    if 'eye' in s:
        return 'eye'
    if 'hair' in s:
        return 'hair'
    if 'mouth' in s:
        return 'teeth'
    if 'skin' in s:
        return 'head'           # (AEW's skin section is head + body: the face area gets the face weights)
    return 'gear'


def load(paks, folder):
    tmp = tempfile.mkdtemp(prefix='portit_aew_')
    pkgname = mesh_package(paks, folder)
    if not pkgname:
        raise ValueError('No SkeletalMesh for %s' % folder)
    p = read_file(paks, pkgname[len(pkgname) - len(pkgname.split('/WrestlerData/')[1]) - len('WrestlerData/'):], tmp)
    out, r = aew_mesh.read(p)
    L = out['lods'][0]
    names = [b[0] for b in out['bones']]
    world = []
    for nm, par, (q, t, s) in out['bones']:
        M = np.eye(4)
        M[:3, :3] = quat_mat(q)
        M[3, :3] = t
        world.append(M if par < 0 else M @ world[par])
    bones = [{'name': nm, 'parent': par, 'world': world[i]} for i, (nm, par, _) in enumerate(out['bones'])]
    gb = aew_mesh.global_bones(L)
    idx = L['indices'].astype(np.int64)
    pos = L['positions'].astype(np.float64)
    nrm = L['tangent_z'][:, :3].astype(np.float64)
    uvs = L['uvs'][:, 0, :].astype(np.float64)
    pkg = aew_mesh.Pkg(open(p, 'rb').read())
    mi_names = {}
    for i, mat in enumerate(out['materials']):
        imp = [n for n in pkg.names if n.startswith('/Game/') and '/material/' in n.lower()]
        mi_names[i] = mat['slot']
    meshes = []
    tex_cache = {}
    mats_pkgs = [n for n in pkg.names if n.startswith('/Game/') and '/material/' in n.lower()]
    # each slot's material instance as the mesh links it: import index -> the /Game/ path whose UE import hash
    # (cityhash.import_id) is that import (PortIT 2026-11-09: the name guess below missed mt_Hands_L_01 ->
    # MI_Wrists_L_01, mt_Kneepads_R_01 -> MI_Kneepad_R_01 - those parts came out untextured, grey)
    import struct
    import cityhash
    linked = {}
    try:
        nimp = (pkg.exports_off - pkg.imports_off) // 8
        ids = {struct.unpack_from('<Q', pkg.d, pkg.imports_off + 8 * i)[0] & ~(3 << 62): i for i in range(nimp)}
        for nm in pkg.names:
            if nm.startswith('/Game/'):
                i = ids.get(cityhash.import_id(nm + '/' + nm.rsplit('/', 1)[1]))
                if i is not None:
                    linked[i] = nm
    except Exception:   # noqa: BLE001  (an unusual package: the name guess alone)
        linked = {}
    for s in L['sections']:
        if s['disabled']:
            continue
        slot = out['materials'][s['material']]['slot'] if s['material'] < len(out['materials']) else 'mat%d' % s['material']
        kind = kind_of(slot)
        if kind == 'overlay':
            continue
        a, b = s['base_vertex'], s['base_vertex'] + s['vertices']
        tri = idx[s['base_index']:s['base_index'] + 3 * s['triangles']].reshape(-1, 3) - a
        # the material instance: the import whose name matches the slot (mt_Skin_01 -> MI_Skin ...)
        key = re.sub(r'^(mt|s)_', '', slot, flags=re.I)
        key = re.sub(r'_\d+$', '', key).lower()
        cand = [m for m in mats_pkgs if key in m.rsplit('/', 1)[1].lower()]
        if not cand:                     # (mt_Eye_L_01 <-> MI_l_eye: the same words in another order)
            words = set(key.split('_'))
            cand = [m for m in mats_pkgs if words <= set(m.rsplit('/', 1)[1].lower().split('_'))]
        mi = sorted(cand, key=len)[0] if cand else None
        if s['material'] < len(out['materials']):
            ref = out['materials'][s['material']]['material']
            if ref < 0 and (-ref - 1) in linked and '/material/' in linked[-ref - 1].lower():
                mi = linked[-ref - 1]
        if mi not in tex_cache:
            tex_cache[mi] = material_textures(paks, mi, tmp, folder) if mi else ({}, {})
        tex, scal = tex_cache[mi]
        lvl = scal.get('Level', 1.0)
        if kind == 'hair' and 'color' in tex and abs(lvl - 1.0) > 1e-3:
            # (AEW's hair shader scales the colour map by the material's Level: Bryan Danielson's hair map is near
            #  black at Level 4, his beard's near white at 0.5 - read as they are, black hair and a white beard)
            key = (mi, 'level')
            if key not in tex_cache:
                tex_cache[key] = (dict(tex, color=hair_level(tex['color'], lvl)), scal)
            tex, scal = tex_cache[key]
        meshes.append(SourceMesh(
            name=key, material=slot, kind=kind, cutout=kind in ('hair', 'lash'), positions=pos[a:b], normals=nrm[a:b],
            uvs=uvs[a:b], tris=tri, bone_idx=gb[a:b].astype(np.int64), bone_w=L['skin_weights'][a:b].astype(np.float64),
            colors=None, shapes=None, images=tex, scalars=scal, textures={}, bones=bones, variant=False,
            source=folder))
    female = folder.startswith('F')
    return {'game': 'aew', 'name': folder, 'bones': bones, 'meshes': meshes, 'attire': folder, 'attires': [folder],
            'space': 'aew', 'female': female}
