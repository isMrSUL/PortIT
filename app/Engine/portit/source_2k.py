"""WWE 2K26 (also 2K25-era .mcd v0x2F) wrestler -> PortIT's neutral source model.

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
    vals = {struct.unpack_from('<Q', d, o)[0] for o in range(0, len(d) - 7, 4)}
    return shader, vals


def find_wrestler(folder):
    """-> (base .mcd path, [attire .mcd paths])"""
    mcds = [p for p in glob.glob(os.path.join(folder, '**', '*.mcd'), recursive=True)]
    base = [p for p in mcds if os.path.basename(p).lower() == 'basemodel.mcd']
    att = [p for p in mcds if p not in base and os.sep + 'textures' + os.sep not in p.lower()]
    return (base[0] if base else None), sorted(att)


class Textures:
    """the .dds files of a wrestler folder: by .jmtl hash and by stem"""

    def __init__(self, folder):
        self.files = [p for p in glob.glob(os.path.join(folder, '**', '*'), recursive=True) if p.lower().endswith('.dds')]
        self.by_hash = {}
        for p in self.files:
            parts = os.path.relpath(p, folder).replace(os.sep, '/').split('/')
            stem = os.path.splitext(parts[-1])[0]
            # every tail of the path counts (the mod root is unknown): a/b/Textures/x.tex, b/Textures/x.tex ...
            full = [os.path.basename(folder)] + parts[:-1] + [stem + '.tex']
            for i in range(len(full)):
                self.by_hash.setdefault(fnv('/'.join(full[i:])), p)

    def for_material(self, jmtl_path, stem, near, alt=None):
        """role -> dds path for one material (alt: a second name to try, e.g. 'eye' for eye_l)"""
        out = {}
        if jmtl_path and os.path.exists(jmtl_path):
            _, vals = jmtl_info(jmtl_path)
            for v in vals:
                if v in self.by_hash:
                    p = self.by_hash[v]
                    out.setdefault(role_of(p), p)
        if 'color' not in out:   # fallback: <stem>_<role>.dds next to the model
            for p in self.files:
                if os.path.dirname(os.path.dirname(p)) != near:
                    continue
                s = os.path.splitext(os.path.basename(p))[0].lower()
                if any(n and s.startswith(n.lower() + '_') for n in (stem, alt)):
                    out.setdefault(role_of(p), p)
        return out


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
        jstem = sorted(cand, key=len)[0] if cand else key
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
    bones = [{'name': b['name'], 'parent': b['parent'], 'world': b['world']} for b in m.bones]
    return bones, meshes


def load(folder, attire=None):
    """-> dict(game, name, bones (merged by name), meshes (base + attire, each with its own bone list))"""
    base, attires = find_wrestler(folder)
    if not base:
        raise ValueError('No BaseModel/baseModel.mcd under %s' % folder)
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
    return {'game': '2k26', 'name': os.path.basename(os.path.normpath(folder)), 'bones': bones, 'meshes': meshes,
            'attire': att, 'attires': attires, 'space': '2k'}
