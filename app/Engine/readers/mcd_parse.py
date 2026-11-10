"""WWE 2K .mcd / .mdl model reader - pure Python + numpy, no DLL.
Reverse engineered against a reference model library's output (mcd_oracle.py) - every field below is checked byte-exact for 2K26.

All little-endian. "A4" = pad to a 4-byte boundary (relative to the file start).

  ... "MDL!" "MDL!" u32 version(0x2F) u32 1 f32 bbox[6]
  "TEXT" u32 size u32 count u32 offs[count] + NUL strings          (every name below is a u16/u32 index here)
  "BONE" u32 size f32 2.0 f32 0.1 u32 boneCount u32 jiggleCount
        bone x boneCount: u16 name i16 parent f32 t[3] f32 euler[3] (radians) u8 0 i32 jiggle(-1 = none)
                          [+ f32[8] when jiggle != -1]
        local = Rx(e0) Ry(e1) Rz(e2) then translate t (row vectors); world = local * parentWorld
  "GtPt" u32 size u32 count, count x 8 bytes (game points - see notes)
  "MTL!" u32 size u32 count, count x {u16 objectName, u32 hash}, A4
  "MBfD" u32 size u32 meshCount
     mesh: u32 material(name) u32 128 u32 0 i32 -1 f32 bbox[6] u32 vertexCount u32 streamCount
           stream x streamCount: 12-byte hash, u16 semantic u16 format u16 type u16 0, data[vertexCount], A4
              POSITION/NORMAL/TANGENT R32_G32_B32 float, BINORMAL R8 snorm (tangent sign), PackedTBN R32 uint,
              COLOR R8_G8_B8_A8 unorm, TEXCOORD R32_G32 float (V stored flipped: v' = 1 - v)
           skin: u16 boneCount(max used + 1) u16 influences, u16 bone[n*inf], A4, u8 weight[n*inf] (/255), A4
           u32 vertexRemap[n]
           u32 shapeCount, u16 shapeName[shapeCount], A4
           if shapeCount: u32 n, f32 scale[3], f32 min[3], u32 size, then u32 words:
                 table [shape][block of 32 vertices] = {u32 movedMask, u32 wordOffset}
                 per moved vertex 2 words: w0 = x:11 (bits 21-31) y:11 (10-20) z:10 (0-9) -> delta = min + q*scale,
                                           w1 = 0 in every file seen (normal delta?)
           u32 283 x(2, or 3 when no shapes) f32 a f32 b u32 lodCount {u32 c f32[4]} x lodCount  (stats, unused)
           "ENDM" u32 0
  "LODs" u32 size u32 lodCount, per LOD: u32 meshCount, per mesh: u16 mesh u32 indexCount idx[] (u16; u32 when the
         mesh has > 65535 vertices), A4,
         u32 subsetCount {u32 material u32 start u32 count u32 maxVertex u32 0}, "ENDM" u32 0
         (triangles are stored (b, a, c): swap the first two to get the DLL's / Blender's winding)

PortIT 2026-11-10 - every Visual Concepts MDL version is read. The differences were worked out per version from
the files themselves:
  0x1D 2K20 .mdl   0x20 2K22 .mdl   0x25 2K23 .mdl   0x28 2K24 .mdl   0x29 2K25 .mcd   0x2F 2K26 .mcd
  - a .mdl is the bare model; a .mcd wraps it ("MCD!" ... "MDL!" then the model's own "MDL!").
  - header: "MDL!" u32 version, u32 1 (not in 0x1D / 0x20), f32 bbox[6]; then chunks {tag, u32 size, body}, walked
    by their sizes (searching for a tag can hit vertex data).
  - BONE: 0x28 and newer as above; 0x25: u32 x3, then (size - 12) / 32 bones of {u16 name i16 parent f32[6] u32};
          0x1D / 0x20: u32 count, then bones of {u16 name i16 parent f32[6]}.
  - MTL!: before 0x2F a u32 name per material (no hash).
  - mesh header: 0x29 / 0x2F u32 name u32 flag u32 0 i32 motion; 0x20 - 0x28 u32 name u32 flag u16 0 i32 motion;
          0x1D u32 name u32 flag i32 motion; then bbox, vertex count, stream count. A stream's data starts at the
          4-byte boundary after its 20-byte record; its size is vertices x the format's byte size, then A4.
          2K25 stores normals and tangents as R8_G8_B8_A8 snorm: they are handed out as float xyz like 2K26's.
  - skin: 0x25 and newer the skin block above; 0x1D / 0x20 BLENDINDICES / BLENDWEIGHTS streams (4 per vertex).
  - shapes: 0x20 and newer the packed shapes above; 0x1D an older layout that is not read (no shapes).
  - LODs: subsets are 12 bytes (material, first index, index count) before 0x2F, with no u32 0 after them.
"""
import re
import struct
import sys

import numpy as np

FMT = {('R32_G32_B32', 'float'): ('<f4', 3), ('R32_G32', 'float'): ('<f4', 2), ('R32', 'uint'): ('<u4', 1),
       ('R8', 'snorm'): ('i1', 1), ('R8_G8_B8_A8', 'unorm'): ('u1', 4), ('R32_G32_B32_A32', 'float'): ('<f4', 4),
       ('R16_G16', 'float'): ('<f2', 2), ('R16_G16_B16_A16', 'float'): ('<f2', 4)}
# (formats of the older games; integer data is turned into floats for POSITION / NORMAL / TANGENT / TEXCOORD)
INT = {('R8_G8_B8_A8', 'snorm'): ('i1', 4), ('R8_G8_B8_A8', 'uint'): ('u1', 4), ('R8_G8_B8_A8', 'sint'): ('i1', 4),
       ('R16_G16_B16_A16', 'uint'): ('<u2', 4), ('R16_G16_B16_A16', 'snorm'): ('<i2', 4),
       ('R16_G16_B16_A16', 'unorm'): ('<u2', 4), ('R16_G16', 'snorm'): ('<i2', 2), ('R16_G16', 'unorm'): ('<u2', 2),
       ('R8', 'unorm'): ('u1', 1), ('R8', 'uint'): ('u1', 1)}
BYTES = {'R32_G32_B32_A32': 16, 'R32_G32_B32': 12, 'R32_G32': 8, 'R16_G16_B16_A16': 8, 'R32': 4, 'R8_G8_B8_A8': 4,
         'R16_G16': 4, 'R10_G10_B10_A2': 4, 'R11_G11_B10': 4, 'R8_G8': 2, 'R16': 2, 'R8': 1, 'A8': 1}
VECTOR = ('POSITION', 'NORMAL', 'TANGENT')
VERSIONS = {0x1D: '2K20', 0x20: '2K22', 0x25: '2K23', 0x28: '2K24', 0x29: '2K25', 0x2F: '2K26'}


def a4(x):
    return (x + 3) & ~3


def _inside(a, b):
    """bounding box a (min xyz, max xyz) inside box b"""
    return all(a[i] >= b[i] - 1e-3 for i in range(3)) and all(a[i + 3] <= b[i + 3] + 1e-3 for i in range(3))


def _box(mb):
    """a mesh's box from its own positions (older files' stored boxes do not always match them)"""
    P = next((a for sem, _, a in mb['streams'] if sem == 'POSITION'), None)
    return tuple(P.min(0)) + tuple(P.max(0)) if P is not None and len(P) else mb['bbox']


def _centre(b):
    """a bounding box's centre as a zero-size box"""
    c = tuple((b[i] + b[i + 3]) / 2 for i in range(3))
    return c + c


def euler_matrix(e):
    out = np.eye(3)
    for ax, ang in enumerate(e):
        c, s = np.cos(ang), np.sin(ang)
        R = [np.array([[1, 0, 0], [0, c, s], [0, -s, c]]), np.array([[c, 0, -s], [0, 1, 0], [s, 0, c]]),
             np.array([[c, s, 0], [-s, c, 0], [0, 0, 1]])][ax]
        out = out @ R
    return out


class Mcd:
    def __init__(self, path):
        self.path = path
        self.raw = raw = open(path, 'rb').read()
        if raw[:4] == b'PJBY':      # (a WWE 2K19 - 2K22 pack's body or attire, unpacked by readers/jboy_2k.py)
            import jboy_2k
            jboy_2k.fill(self, path, raw)
            return
        self.mdl = raw.find(b'MDL!MDL!')
        m = self.mdl + 4 if self.mdl >= 0 else (0 if raw[:4] == b'MDL!' else -1)
        if m < 0:
            raise ValueError('no MDL! block')
        self.version = v = struct.unpack_from('<I', raw, m + 4)[0]
        if v not in VERSIONS:
            raise ValueError('model version 0x%X is not known (2K20 0x1D ... 2K26 0x2F)' % v)
        self.game = VERSIONS[v]
        old = v in (0x1D, 0x20)
        self.bbox = struct.unpack_from('<6f', raw, m + (8 if old else 12))
        chunks, q = {}, m + (32 if old else 36)
        while q + 8 <= len(raw):
            tag, size = raw[q:q + 4], struct.unpack_from('<I', raw, q + 4)[0]
            if tag == b'END!':
                break
            chunks.setdefault(tag, q)
            q += 8 + size
        self.chunks = chunks
        t = chunks[b'TEXT']
        size, count = struct.unpack_from('<II', raw, t + 4)
        offs = struct.unpack_from('<%dI' % count, raw, t + 12)
        base = t + 12 + 4 * count
        self.strings = [raw[base + q:raw.index(b'\0', base + q)].decode('latin1') for q in offs]
        self._bones(chunks.get(b'BONE', -1))
        self._objects(chunks.get(b'MTL!', -1))
        self._gtpt(chunks.get(b'GtPt', -1))
        self._meshes(chunks[b'MBfD'])
        # (found by the chunk walk: the 4 bytes 'LODs' can turn up inside vertex data - 2K26 Nikki Bella's
        #  Default_Attire has them at 280681, the block itself at 2811924; PortIT 2026-11-07)
        self._lods(chunks.get(b'LODs', -1))
        if v < 0x2F:
            self._older()

    def _older(self):
        """2K20 - 2K25 files, made right for the 2K26 pipeline (2K26 files are left exactly as read):
        - weights: mod tools often save them a little under 1 in all (0.73 - 0.98 seen); scaled back to 1 (a vertex
          with no weight at all is left at 0 - PortIT's nearest-skin fallback weights it)
        - 2K19-era models moved to 2K23 call their meshes Object0, Object1 ...: named the 2K26 way from the material
          their triangles use (head:skinned -> M_Head ...)"""
        for mb in self.meshes:
            w = mb['skin_weights']
            s = w.sum(1, keepdims=True)
            mb['skin_weights'] = np.where(s > 0, w / np.where(s > 0, s, 1), 0.0)
            # - texture coordinates: 2K19-era models (in 2K23 files) store V one whole tile up (1 ... 2); the game repeats
            #   the texture so it looks the same, PortIT's texture sheets do not: a mesh that sits inside one tile is
            #   moved back to 0 ... 1 by whole tiles (same picture)
            for j, (sem, key, a) in enumerate(mb['streams']):
                if sem != 'TEXCOORD' or not len(a) or a.dtype.kind != 'f':
                    continue
                lo, hi = np.floor(a.min(0) + 0.01), np.floor(a.max(0) - 0.01)   # (edges bleed a little past a tile)
                shift = np.where(lo == hi, lo, 0.0)
                if shift.any():
                    mb['streams'][j] = (sem, key, (a - shift).astype(a.dtype))
        if not self.lods:
            return
        # - each mesh's own material, from the material its triangles use (mb['object']): the mesh name can be anything
        #   in a mod (2K25 Sable: M_Bra uses 'outfit', whose texture is outfit_color)
        for k, mb in enumerate(self.meshes):
            subs = [s for s in (self.lods[0][k]['subsets'] if k < len(self.lods[0]) else []) if s[0] < len(self.objects)]
            obj = self.objects[max(subs, key=lambda s: s[2])[0]][0] if subs else ''   # (the one with most triangles)
            mb['object'] = re.sub(r'(:skinned)?$', '', obj, flags=re.I)
        # - 2K25 base models carry a second, 112-vertex M_Head: a strip lying 0.13 cm on the head's lower edge at the
        #   neck. Two meshes named M_Head would confuse the face fit: the strip becomes M_Head_Seam (left out, as the
        #   other extra M_Head_ pieces are)
        heads = sorted([mb for mb in self.meshes if mb['material'] == 'M_Head'], key=lambda mb: -mb['vertices'])
        for mb in heads[1:]:
            mb['material'] = 'M_Head_Seam'
        names = {'head': 'M_Head', 'body': 'M_Body', 'eye': 'M_EYE', 'eye_caruncle': 'M_Head_Caruncle',
                 'eyelash': 'M_transB_eyelash', 'eye_overlay': 'M_transB_eyeshadow'}
        generic = [k for k, mb in enumerate(self.meshes) if re.fullmatch(r'Object\d*', mb['material'])]
        head = heads[0] if heads else None
        for k in generic:
            obj = self.meshes[k]['object']
            new = names.get(obj.lower(), 'M_' + obj[:1].upper() + obj[1:] if obj else 'M_Object%d' % k)
            self.meshes[k]['material'] = new
            if new == 'M_Head' and head is None:
                head = self.meshes[k]
        # several meshes on the body material: the biggest is the body; tongue (its shapes) and teeth (inside the head)
        body = [k for k in generic if self.meshes[k]['material'] == 'M_Body']
        if len(body) > 1:
            body.sort(key=lambda k: -self.meshes[k]['vertices'])
            for k in body[1:]:
                mb = self.meshes[k]
                if any(s['name'].lower().startswith('tongue') for s in mb['shapes']):
                    mb['material'] = 'M_Tongue'
                elif head is not None and _inside(_centre(_box(mb)), _box(head)):
                    # (in the mouth: the mouth bag moves with the face shapes, the teeth do not - 2K22 Ken Shamrock)
                    mb['material'] = 'M_MouthBag' if mb['shapes'] else 'M_Teeth'
                else:
                    mb['material'] = 'M_Body_%d' % k

    def s(self, i):
        return self.strings[i] if 0 <= i < len(self.strings) else '#%d' % i

    # ---- skeleton
    def _bones(self, p):
        raw, v = self.raw, self.version
        self.bones = []
        self.bone_f0 = self.bone_f1 = 0.0
        if p < 0:
            return
        size = struct.unpack_from('<I', raw, p + 4)[0]
        if v >= 0x28:
            size, self.bone_f0, self.bone_f1, n, nj = struct.unpack_from('<IffII', raw, p + 4)
            q, rec = p + 24, 33
        elif v == 0x25:
            n, q, rec = (size - 12) // 32, p + 20, 32
        else:
            n, q, rec = struct.unpack_from('<I', raw, p + 8)[0], p + 12, 28
        for i in range(n):
            nm, par = struct.unpack_from('<Hh', raw, q)
            t = struct.unpack_from('<3f', raw, q + 4)
            e = struct.unpack_from('<3f', raw, q + 16)
            flag, jig, extra = 0, -1, None
            if rec == 33:
                flag, jig = struct.unpack_from('<Bi', raw, q + 28)
            q += rec
            if jig != -1:
                extra = struct.unpack_from('<8f', raw, q)
                q += 32
            self.bones.append({'name': self.s(nm), 'parent': par, 't': t, 'euler': e, 'flag': flag, 'jiggle': jig,
                               'jiggle_data': extra})
        assert q <= p + 8 + size, 'BONE overrun'
        for b in self.bones:
            M = np.eye(4)
            M[:3, :3] = euler_matrix(b['euler'])
            M[3, :3] = b['t']
            b['local'] = M
            b['world'] = M if b['parent'] < 0 else M @ self.bones[b['parent']]['world']

    def _objects(self, p):
        self.objects = []
        if p < 0:
            return
        size, n = struct.unpack_from('<II', self.raw, p + 4)
        for i in range(n):
            if self.version >= 0x2F:
                nm, h = struct.unpack_from('<HI', self.raw, p + 12 + 6 * i)
            else:
                nm, h = struct.unpack_from('<I', self.raw, p + 12 + 4 * i)[0], 0
            self.objects.append((self.s(nm), h))

    def _gtpt(self, p):
        self.gtpt = []
        if p < 0:
            return
        size, n = struct.unpack_from('<II', self.raw, p + 4)
        for i in range(n):
            self.gtpt.append(struct.unpack_from('<4H', self.raw, p + 12 + 8 * i))

    # ---- mesh buffers
    def _stream(self, q, n, k):
        """the stream record at q -> (semantic, (format, type), array, offset after the data)"""
        raw = self.raw
        sem, fmt, typ = (self.s(x) for x in struct.unpack_from('<3H', raw, q + 12))
        key = (fmt, typ)
        if fmt not in BYTES:
            raise ValueError('mesh %d: unknown stream format %s' % (k, key))
        d = a4(q + 20)
        if key in FMT:
            dt, c = FMT[key]
            a = np.frombuffer(raw, dt, n * c, d).reshape(n, c)
        elif key in INT:
            dt, c = INT[key]
            a = np.frombuffer(raw, dt, n * c, d).reshape(n, c)
            if sem in VECTOR or sem == 'TEXCOORD':
                scale = {'i1': 127.0, '<i2': 32767.0, 'u1': 255.0, '<u2': 65535.0}[dt]
                a = (a.astype(np.float32) / scale)[:, :3 if sem in VECTOR else 2]
                key = ('R32_G32_B32', 'float') if sem in VECTOR else ('R32_G32', 'float')
        else:
            a = np.frombuffer(raw, 'u1', n * BYTES[fmt], d).reshape(n, BYTES[fmt])
        return sem, key, a, a4(d + n * BYTES[fmt])

    def _meshes(self, p):
        raw, v = self.raw, self.version
        size, nmesh = struct.unpack_from('<II', raw, p + 4)
        q = p + 12
        self.meshes = []
        head = 48 if v >= 0x29 else (44 if v == 0x1D else 46)
        for k in range(nmesh):
            mat = struct.unpack_from('<I', raw, q)[0]
            bbox = struct.unpack_from('<6f', raw, q + head - 32)
            n, ns = struct.unpack_from('<II', raw, q + head - 8)
            q += head
            mesh = {'material': self.s(mat), 'bbox': bbox, 'vertices': n, 'streams': []}
            for j in range(ns):
                sem, key, a, q = self._stream(q, n, k)
                mesh['streams'].append((sem, key, a))
            if v >= 0x25:
                nb, inf = struct.unpack_from('<HH', raw, q)
                mesh['skin_bones'] = np.frombuffer(raw, '<u2', n * inf, q + 4).reshape(n, inf)
                q = a4(q + 4 + 2 * n * inf)
                mesh['skin_weights'] = np.frombuffer(raw, 'u1', n * inf, q).reshape(n, inf) / 255.0
                q = a4(q + n * inf)
            else:
                # (2K20 / 2K22: the skin is two of the streams, 4 bones per vertex)
                st = {sm: a for sm, _, a in mesh['streams']}
                bi, bw = st.get('BLENDINDICES'), st.get('BLENDWEIGHTS')
                if bi is None or bw is None:
                    mesh['skin_bones'] = np.zeros((n, 1), np.uint16)
                    mesh['skin_weights'] = np.ones((n, 1))
                else:
                    mesh['skin_bones'] = bi.astype(np.uint16)
                    w = bw.astype(np.float64)
                    mesh['skin_weights'] = w / 255.0 if bw.dtype == np.uint8 else w
            mesh['remap'] = np.frombuffer(raw, '<u4', n, q)
            q += 4 * n
            mesh['shapes'] = []
            if v >= 0x20:
                nsh = struct.unpack_from('<I', raw, q)[0]
                names = [self.s(x) for x in struct.unpack_from('<%dH' % nsh, raw, q + 4)]
                q = a4(q + 4 + 2 * nsh)
                if nsh:
                    q = self._shapes(mesh, names, q)
            e = raw.find(b'ENDM', q)
            mesh['stats'] = raw[q:e]
            q = e + 8
            self.meshes.append(mesh)
        self.mesh_end = q

    def _shapes(self, mesh, names, q):
        raw = self.raw
        nv = struct.unpack_from('<I', raw, q)[0]
        scale = np.array(struct.unpack_from('<3f', raw, q + 4))
        mn = np.array(struct.unpack_from('<3f', raw, q + 16))
        dsize = struct.unpack_from('<I', raw, q + 28)[0]
        W = np.frombuffer(raw, '<u4', dsize // 4, q + 32)
        nblk = (nv + 31) // 32
        for j, name in enumerate(names):
            tab = W[2 * nblk * j:2 * nblk * (j + 1)].reshape(nblk, 2)
            idx, words = [], []
            for b, (mask, off) in enumerate(tab):
                bits = [i for i in range(32) if mask >> i & 1]
                idx += [b * 32 + i for i in bits]
                words.append(W[off:off + 2 * len(bits)].reshape(-1, 2))
            w = np.concatenate(words) if words else np.zeros((0, 2), np.uint32)
            w0 = w[:, 0].astype(np.int64)
            qz = np.stack([(w0 >> 21) & 2047, (w0 >> 10) & 2047, w0 & 1023], 1)
            mesh['shapes'].append({'name': name, 'index': np.array(idx, np.int64), 'delta': mn + qz * scale,
                                   'word1': w[:, 1]})
        return q + 32 + dsize

    def _lods(self, p):
        raw = self.raw
        self.lods = []
        if p < 0:
            return
        size, nl = struct.unpack_from('<II', raw, p + 4)
        q = p + 12
        for lod in range(nl):
            nm = struct.unpack_from('<I', raw, q)[0]
            q += 4
            row = []
            for k in range(nm):
                mesh, n = struct.unpack_from('<HI', raw, q)
                # indices are u16, or u32 when the mesh has more than 65535 vertices
                isz = 4 if mesh < len(self.meshes) and self.meshes[mesh]['vertices'] > 65535 else 2
                idx = np.frombuffer(raw, '<u%d' % isz, n, q + 6).reshape(-1, 3)[:, [1, 0, 2]]
                q = a4(q + 6 + isz * n)
                # subsets: a count, 16 bytes each (material, first index, index count, vertex count), then a u32 (0).
                # (PortIT 2026-11-07: read as 20 bytes each before - right only for one subset, as every mod file
                #  has; the retail 2K26 files have more where a mesh carries two materials, e.g. the eyes)
                # (before 2K26: 12 bytes each - material, first index, index count - and no u32 after them)
                nsub = struct.unpack_from('<I', raw, q)[0]
                if self.version >= 0x2F:
                    subs = [struct.unpack_from('<4I', raw, q + 4 + 16 * i) for i in range(nsub)]
                    q += 4 + 16 * nsub + 4
                else:
                    subs = [struct.unpack_from('<3I', raw, q + 4 + 12 * i) + (0,) for i in range(nsub)]
                    q += 4 + 12 * nsub
                assert raw[q:q + 4] == b'ENDM', 'LOD record'
                q += 8
                row.append({'mesh': mesh, 'triangles': idx, 'subsets': subs})
            self.lods.append(row)
        self.trailer = raw[q:]

    # ---- helpers
    def stream(self, k, sem):
        for s, key, a in self.meshes[k]['streams']:
            if s == sem:
                return a
        return None

    def uvs(self, k):
        a = self.stream(k, 'TEXCOORD')
        return None if a is None else np.stack([a[:, 0], 1.0 - a[:, 1]], 1)


FEMALE_BODY_VERTS = 12699   # (2K's women's body template: 2K23 Alundra Blayze, 2K24 Rosa Mendes, 2K26 AJ Lee and Ava;
#                             the men's are 12725 - 12763)


def looks_female(path):
    """a quick look at a base model (no data decoded, a few ms): a woman's model by 2K's own data - the women's body
    template (its biggest mesh has 12699 vertices: 2K22 Kayla Braxton, 2K23 Alundra Blayze, 2K24 Rosa Mendes, 2K26
    AJ Lee / Ava; the men's 12725 - 12763). (Not breast bones: 2K22 Hulk Hogan has J_breast_L / R too.)
    A mod with a reworked body is not caught (2K25 Sable: 13235) - the Male / Female choice in the app stays the
    user's."""
    try:
        raw = open(path, 'rb').read()
        if raw[:4] == b'PJBY':
            import jboy_2k
            return jboy_2k.looks_female(path)
        i = raw.find(b'MDL!MDL!')
        m = i + 4 if i >= 0 else (0 if raw[:4] == b'MDL!' else -1)
        v = struct.unpack_from('<I', raw, m + 4)[0]
        if m < 0 or v not in VERSIONS:
            return False
        q, chunks = m + (32 if v in (0x1D, 0x20) else 36), {}
        while q + 8 <= len(raw) and raw[q:q + 4] != b'END!':
            chunks.setdefault(raw[q:q + 4], q)
            q += 8 + struct.unpack_from('<I', raw, q + 4)[0]
        t = chunks[b'TEXT']
        count = struct.unpack_from('<I', raw, t + 8)[0]
        offs = struct.unpack_from('<%dI' % count, raw, t + 12)
        base = t + 12 + 4 * count
        strings = [raw[base + o:raw.index(b'\0', base + o)].decode('latin1') for o in offs]
        p = chunks[b'MBfD']
        nmesh = struct.unpack_from('<I', raw, p + 8)[0]
        q, head, biggest = p + 12, (48 if v >= 0x29 else 44 if v == 0x1D else 46), 0
        for k in range(nmesh):
            n, ns = struct.unpack_from('<II', raw, q + head - 8)
            biggest = max(biggest, n)
            q += head
            for j in range(ns):
                fmt = strings[struct.unpack_from('<H', raw, q + 14)[0]]
                q = a4(a4(q + 20) + n * BYTES[fmt])
            if v >= 0x25:
                inf = struct.unpack_from('<H', raw, q + 2)[0]
                q = a4(a4(q + 4 + 2 * n * inf) + n * inf)
            q += 4 * n
            if v >= 0x20:
                nsh = struct.unpack_from('<I', raw, q)[0]
                q = a4(q + 4 + 2 * nsh)
                if nsh:
                    q += 32 + struct.unpack_from('<I', raw, q + 28)[0]
            q = raw.find(b'ENDM', q) + 8
        return biggest == FEMALE_BODY_VERTS
    except (OSError, KeyError, IndexError, ValueError, struct.error):
        return False


if __name__ == '__main__':
    m = Mcd(sys.argv[1])
    print('MDL v0x%x (%s)  %d strings  %d bones (%d jiggle)  %d meshes  %d LODs  objects %s' % (
        m.version, m.game, len(m.strings), len(m.bones), sum(b['jiggle'] != -1 for b in m.bones), len(m.meshes),
        len(m.lods), [o[0] for o in m.objects]))
    for k, mb in enumerate(m.meshes):
        tris = m.lods[0][k]['triangles'] if m.lods else []
        print('  mesh %d %-18s %6d verts %6d tris  infl %d  shapes %3d  streams %s' % (
            k, mb['material'], mb['vertices'], len(tris), mb['skin_bones'].shape[1], len(mb['shapes']),
            [(s[0],) + s[1] for s in mb['streams']]))
