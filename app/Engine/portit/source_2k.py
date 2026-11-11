"""WWE 2K26 wrestler -> PortIT's neutral source model (also 2K20 - 2K25: .mdl / .mcd, readers/mcd_parse.py).

A wrestler folder holds BaseModel/baseModel.mcd and one or more attire folders (<x>/Attire.mcd or <id>_Attire.mcd),
each with Materials/*.jmtl and Textures/*.dds. Textures are linked to meshes through the .jmtl texture hashes
(FNV-1a 64 of the lower-case relative '.tex' path), with the material name as the fallback.
"""
import glob
import os
import re
import struct

import numpy as np
from PIL import Image

from mcd_parse import Mcd

FNV_OFF, FNV_PRIME, M64 = 0xCBF29CE484222325, 0x100000001B3, (1 << 64) - 1
# jmtl shader byte -> what the mesh is
SHADER_KIND = {0x12: 'eye', 0x13: 'caruncle', 0x14: 'lash', 0x15: 'gear', 0x16: 'hair', 0x17: 'head', 0x18: 'skin',
               0x19: 'skin', 0x1A: 'teeth', 0x1B: 'hair', 0x1C: 'hair', 0x1D: 'hair'}


def fnv(s):
    h = FNV_OFF
    for ch in s.lower():
        h = ((h ^ (ord(ch) & 0xFF)) * FNV_PRIME) & M64
    return h


def jmtl_info(path):
    """-> (shader byte or None, set of u64 values found in the file)"""
    d = open(path, 'rb').read()
    shader = None
    try:
        root = struct.unpack_from('<I', d, 0)[0]
        vt = root - struct.unpack_from('<i', d, root)[0]
        vlen = struct.unpack_from('<H', d, vt)[0]
        if vlen >= 8:
            off = struct.unpack_from('<H', d, vt + 6)[0]       # field 1: the shader byte
            if off:
                shader = d[root + off]
    except Exception:
        pass
    # (in file order: a material can point at two textures of one kind - a painted head at head_color and
    #  facepaint_color - and a set's order chose between them by chance, PortIT 2026-11-11)
    vals = list(dict.fromkeys(struct.unpack_from('<Q', d, o)[0] for o in range(0, len(d) - 7, 4)))
    return shader, vals


MODEL_EXT = ('.mcd', '.mdl')


def is_base_model(name):
    """baseModel.mcd (2K25 / 2K26) or baseModel.mdl (2K20 - 2K24)"""
    return name.lower() in ('basemodel.mcd', 'basemodel.mdl')


def find_wrestler(folder):
    """-> (base model path, [attire model paths]); .mcd (2K25 / 2K26) or .mdl (2K20 - 2K24, PortIT 2026-11-10)
    (hair_<n>.mdl: a 2K19-era hair slot model, not an attire)"""
    mcds = [p for p in glob.glob(os.path.join(folder, '**', '*'), recursive=True)
            if p.lower().endswith(MODEL_EXT) and os.path.isfile(p)]
    base = [p for p in mcds if is_base_model(os.path.basename(p))]
    att = [p for p in mcds if p not in base and os.sep + 'textures' + os.sep not in p.lower()
           and not os.path.basename(p).lower().startswith('hair_')]
    return (base[0] if base else None), sorted(att)


class Textures:
    """the .dds files of a wrestler folder: by .jmtl hash and by stem"""

    def __init__(self, folder):
        self.files = [p for p in glob.glob(os.path.join(folder, '**', '*'), recursive=True) if p.lower().endswith('.dds')]
        self.by_hash = {}
        self.shared = []
        for p in self.files:
            parts = os.path.relpath(p, folder).replace(os.sep, '/').split('/')
            stem = os.path.splitext(parts[-1])[0]
            # every tail of the path counts (the mod root is unknown): a/b/Textures/x.tex, b/Textures/x.tex ...
            full = [os.path.basename(folder)] + parts[:-1] + [stem + '.tex']
            for i in range(len(full)):
                self.by_hash.setdefault(fnv('/'.join(full[i:])), p)
            # (2K26's materials hash the archive's whole path, Characters/<wrestler>/...: without it no game wrestler's
            #  material found its texture by hash - only names that matched did; Batista's u_acce wears pierce_color)
            self.by_hash.setdefault(fnv('/'.join(['Characters'] + full)), p)
        # (2K26's shared textures, Characters/_Shared - the 2K26 extractor puts them beside the wrestlers: Umaga's and
        #  Tama Tonga's ula necklace wears _Shared/Textures/ula_color; found by hash only, never by name)
        shared = os.path.join(os.path.dirname(os.path.normpath(folder)), '_Shared', 'Textures')
        if os.path.isdir(shared):
            for f in os.listdir(shared):
                if f.lower().endswith('.dds'):
                    p = os.path.join(shared, f)
                    self.by_hash.setdefault(fnv('Characters/_Shared/Textures/' + os.path.splitext(f)[0] + '.tex'), p)
                    self.shared.append(p)

    def material_textures(self, jmtl_path):
        """role -> dds path, from a material's own hashes only (no name guessing); two of one role: the one named after
        the material (head.jmtl: head_color, not facepaint_color), else the first in the file"""
        out = {}
        if jmtl_path and os.path.exists(jmtl_path):
            _, vals = jmtl_info(jmtl_path)
            pick_by_role(out, [self.by_hash[v] for v in vals if v in self.by_hash], jmtl_path)
        return out

    def for_material(self, jmtl_path, stem, near, alt=None):
        """role -> dds path for one material (alt: a second name to try, e.g. 'eye' for eye_l)"""
        out = {}
        if jmtl_path and os.path.exists(jmtl_path):
            _, vals = jmtl_info(jmtl_path)
            pick_by_role(out, [self.by_hash[v] for v in vals if v in self.by_hash], jmtl_path)
        if 'color' not in out:   # fallback: <stem>_<role>.dds next to the model
            for p in self.files:
                if os.path.dirname(os.path.dirname(p)) != near:
                    continue
                s = os.path.splitext(os.path.basename(p))[0].lower()
                if any(n and s.startswith(n.lower() + '_') for n in (stem, alt)):
                    out.setdefault(role_of(p), p)
        return out


def pick_by_role(out, paths, jmtl_path):
    """out[role] = the texture for each role among paths (in the material file's order): the one named after the
    material (<material>_<role>) when there are several, else the first"""
    mat = os.path.splitext(os.path.basename(jmtl_path))[0].lower()
    for p in paths:
        r = role_of(p)
        if r not in out:
            out[r] = p
        elif os.path.splitext(os.path.basename(p))[0].lower().startswith(mat + '_') and                 not os.path.splitext(os.path.basename(out[r]))[0].lower().startswith(mat + '_'):
            out[r] = p


def role_of(path):
    s = os.path.splitext(os.path.basename(path))[0].lower()
    for suf, role in (('_color', 'color'), ('_nrm', 'nrm'), ('_rai', 'rai'), ('_flow', 'flow'), ('_mask', 'mask'),
                      ('_flush', 'flush'), ('_normal', 'normal2')):
        if s.endswith(suf):
            return role
    return 'other'


def load_image(path):
    im = Image.open(path)
    im.load()
    return im


class SourceMesh:
    def __init__(self, **kw):
        self.__dict__.update(kw)


def load_mcd(path, tex, female=False):
    m = Mcd(path)
    mdir = os.path.dirname(path)
    # (the object list is NOT in mesh order: link each mesh by its own material name, M_<Stem>)
    objects = [re.sub(r'(:skinned)?$', '', o[0], flags=re.I) for o in m.objects]
    jmtls = {os.path.splitext(os.path.basename(p))[0].lower(): p
             for p in glob.glob(os.path.join(mdir, 'Materials', '*.jmtl'))}
    meshes = []
    for k, mb in enumerate(m.meshes):
        key = re.sub(r'^m_', '', mb['material'].lower())
        cand = [s for s in jmtls if re.sub(r'_alpha$', '', s) == key] or \
               [s for s in jmtls if s.startswith(key) or key.endswith(re.sub(r'_alpha$', '', s))] or \
               [s for s in jmtls if re.sub(r'_alpha$', '', s) in key]
        # (no name match: the material the mesh's triangles use - older mods name meshes freely, PortIT 2026-11-10)
        jstem = sorted(cand, key=len)[0] if cand else (mb.get('object') or key).lower()
        obj = next((o for o in objects if o.lower() == jstem), jstem)
        stem = re.sub(r'_alpha$', '', jstem)
        jm = jmtls.get(jstem, os.path.join(mdir, 'Materials', jstem + '.jmtl'))
        shader, _ = jmtl_info(jm) if os.path.exists(jm) else (None, set())
        kind = SHADER_KIND.get(shader)
        mat = mb['material'].lower()
        if kind is None:
            kind = ('eye' if 'eye' in mat and 'lash' not in mat else 'lash' if 'lash' in mat else
                    'hair' if 'hair' in mat else 'head' if mat == 'm_head' else 'skin' if mat == 'm_body' else 'gear')
        alpha_obj = obj.lower().split(':')[0].endswith('_alpha')
        lod = m.lods[0][k]['triangles'] if m.lods else np.zeros((0, 3), int)
        col = m.stream(k, 'COLOR')
        meshes.append(SourceMesh(
            name=stem, material=mb['material'], kind=kind, cutout=alpha_obj or kind in ('hair', 'lash'),
            positions=m.stream(k, 'POSITION').astype(np.float64), normals=m.stream(k, 'NORMAL').astype(np.float64),
            uvs=np.array([a for s, key, a in mb['streams'] if s == 'TEXCOORD'][0], np.float64) if any(
                s == 'TEXCOORD' for s, _, _ in mb['streams']) else np.zeros((mb['vertices'], 2)),
            tris=np.asarray(lod, np.int64), bone_idx=mb['skin_bones'].astype(np.int64), bone_w=mb['skin_weights'],
            colors=None if col is None else col.astype(np.float64) / 255.0,
            shapes=mb['shapes'], textures=tex.for_material(jm, stem, mdir, key), source=os.path.basename(path),
            source_path=path, index=k,   # (PortIT: render_source picks this mesh's lower LOD from its .mcd)
            variant=False))
    # alternate looks 2K switches between (M_Outfit_strapsDown next to M_Outfit): off by default
    names = {me.material.lower() for me in meshes}
    for me in meshes:
        n = me.material.lower()
        me.variant = any(n != o and n.startswith(o + '_') for o in names)
        # (S_<x> beside M_<x>: 2K's cloth-physics copy, never drawn - shown as left out, as rock_fbx leaves it out)
        if n.startswith('s_') and 'm_' + n[2:] in names:
            me.variant = True
        # (the see-through eye layer and empty placeholders - the port leaves them out (port_2k: R.DROP_MESHES); drawn
        #  untextured they covered the eyes in white: 2K23 Alundra Blayze, 2K24 Brooks Jensen / Sting, 2026-11-10)
        if 'eyeshadow' in n or n.startswith(('m_lambert', 'm_polysurface', 'lambert', 'polysurface')):
            me.variant = True
    bones = [{'name': b['name'], 'parent': b['parent'], 'world': b['world']} for b in m.bones]
    return bones, meshes


def load(folder, attire=None):
    """-> dict(game, name, bones (merged by name), meshes (base + attire, each with its own bone list))"""
    base, attires = find_wrestler(folder)
    if not base:
        raise ValueError('No BaseModel/baseModel.mcd (or .mdl) under %s' % folder)
    tex = Textures(folder)
    bones, meshes = load_mcd(base, tex)
    for me in meshes:
        me.bones = bones
    if attires:
        att = attire if attire else attires[0]
        ab, am = load_mcd(att, tex)
        for me in am:
            me.bones = ab
        meshes += am
    else:
        att = None
    # (2K26 face paint and an attire's own base textures, as the port lays them on - portit/face_paint.py; the
    #  preview showed Ultimate Warrior 2017 without his paint, 2026-11-10)
    if att:
        try:
            import face_paint
            out = os.path.join(os.environ.get('LOCALAPPDATA', folder), 'PortIT', 'previews', '_facepaint',
                               re.sub(r'[^A-Za-z0-9_.-]+', '_', os.path.basename(os.path.normpath(folder))))
            first = face_paint.prepare(folder, os.path.relpath(os.path.dirname(att), folder), out, log=lambda *a: None,
                                       normals=False)
        except (OSError, ValueError):
            first = []
        if first:
            def swap(path_or_stem):
                stem = os.path.splitext(os.path.basename(path_or_stem))[0].lower()
                for d in first:
                    for f in os.listdir(d):
                        if os.path.splitext(f)[0].lower() == stem:
                            return os.path.join(d, f)
                return None
            for me in meshes:
                tx = dict(me.textures or {})
                for role, p in list(tx.items()):
                    tx[role] = swap(p) or p
                if 'color' not in tx and swap(me.name + '_color'):
                    tx['color'] = swap(me.name + '_color')
                me.textures = tx
    import mcd_parse
    return {'game': '2k26', 'name': os.path.basename(os.path.normpath(folder)), 'bones': bones, 'meshes': meshes,
            'attire': att, 'attires': attires, 'space': '2k',
            'female': 'female' in folder.lower() or mcd_parse.looks_female(base)}
