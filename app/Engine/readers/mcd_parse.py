"""WWE 2K26 .mcd model reader (MDL version 0x2F) - pure Python + numpy, no DLL.
Reverse engineered against VCModel.dll's output (mcd_oracle.py) - every field below is checked byte-exact.

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
"""
import struct
import sys

import numpy as np

FMT = {('R32_G32_B32', 'float'): ('<f4', 3), ('R32_G32', 'float'): ('<f4', 2), ('R32', 'uint'): ('<u4', 1),
       ('R8', 'snorm'): ('i1', 1), ('R8_G8_B8_A8', 'unorm'): ('u1', 4), ('R32_G32_B32_A32', 'float'): ('<f4', 4),
       ('R16_G16', 'float'): ('<f2', 2), ('R16_G16_B16_A16', 'float'): ('<f2', 4)}


def a4(x):
    return (x + 3) & ~3


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
        self.mdl = raw.find(b'MDL!MDL!')
        if self.mdl < 0:
            raise ValueError('no MDL! block')
        o = self.mdl
        self.version, _ = struct.unpack_from('<II', raw, o + 8)
        self.bbox = struct.unpack_from('<6f', raw, o + 16)
        t = raw.find(b'TEXT', o)
        size, count = struct.unpack_from('<II', raw, t + 4)
        offs = struct.unpack_from('<%dI' % count, raw, t + 12)
        base = t + 12 + 4 * count
        self.strings = [raw[base + q:raw.index(b'\0', base + q)].decode('latin1') for q in offs]
        self._bones(raw.find(b'BONE', o))
        self._objects(raw.find(b'MTL!', o))
        self._gtpt(raw.find(b'GtPt', o))
        self._meshes(raw.find(b'MBfD', o))
        # (searched after the mesh data: the 4 bytes 'LODs' can turn up inside vertex data - 2K26 Nikki Bella's
        #  Default_Attire has them at 280681, the block itself at 2811924; PortIT 2026-11-07)
        self._lods(raw.find(b'LODs', getattr(self, 'mesh_end', o)))

    def s(self, i):
        return self.strings[i] if 0 <= i < len(self.strings) else '#%d' % i

    # ---- skeleton
    def _bones(self, p):
        raw = self.raw
        size, self.bone_f0, self.bone_f1, n, nj = struct.unpack_from('<IffII', raw, p + 4)
        q = p + 24
        self.bones = []
        for i in range(n):
            nm, par = struct.unpack_from('<Hh', raw, q)
            t = struct.unpack_from('<3f', raw, q + 4)
            e = struct.unpack_from('<3f', raw, q + 16)
            flag, jig = struct.unpack_from('<Bi', raw, q + 28)
            q += 33
            extra = None
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
            nm, h = struct.unpack_from('<HI', self.raw, p + 12 + 6 * i)
            self.objects.append((self.s(nm), h))

    def _gtpt(self, p):
        self.gtpt = []
        if p < 0:
            return
        size, n = struct.unpack_from('<II', self.raw, p + 4)
        for i in range(n):
            self.gtpt.append(struct.unpack_from('<4H', self.raw, p + 12 + 8 * i))

    # ---- mesh buffers
    def _meshes(self, p):
        raw = self.raw
        size, nmesh = struct.unpack_from('<II', raw, p + 4)
        q = p + 12
        self.meshes = []
        for k in range(nmesh):
            mat, c128, z, m1 = struct.unpack_from('<IIIi', raw, q)
            bbox = struct.unpack_from('<6f', raw, q + 16)
            n, ns = struct.unpack_from('<II', raw, q + 40)
            q += 48
            mesh = {'material': self.s(mat), 'bbox': bbox, 'vertices': n, 'streams': []}
            for j in range(ns):
                sem, fmt, typ = struct.unpack_from('<3H', raw, q + 12)
                key = (self.s(fmt), self.s(typ))
                if key not in FMT:
                    raise ValueError('mesh %d: unknown stream format %s' % (k, key))
                dt, c = FMT[key]
                a = np.frombuffer(raw, dt, n * c, q + 20).reshape(n, c)
                mesh['streams'].append((self.s(sem), key, a))
                q = a4(q + 20 + a.nbytes)
            nb, inf = struct.unpack_from('<HH', raw, q)
            mesh['skin_bones'] = np.frombuffer(raw, '<u2', n * inf, q + 4).reshape(n, inf)
            q = a4(q + 4 + 2 * n * inf)
            mesh['skin_weights'] = np.frombuffer(raw, 'u1', n * inf, q).reshape(n, inf) / 255.0
            q = a4(q + n * inf)
            mesh['remap'] = np.frombuffer(raw, '<u4', n, q)
            q += 4 * n
            nsh = struct.unpack_from('<I', raw, q)[0]
            names = [self.s(x) for x in struct.unpack_from('<%dH' % nsh, raw, q + 4)]
            q = a4(q + 4 + 2 * nsh)
            mesh['shapes'] = []
            if nsh:
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
                q += 32 + dsize
            e = raw.find(b'ENDM', q)
            mesh['stats'] = raw[q:e]
            q = e + 8
            self.meshes.append(mesh)
        self.mesh_end = q

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
                nsub = struct.unpack_from('<I', raw, q)[0]
                subs = [struct.unpack_from('<4I', raw, q + 4 + 16 * i) for i in range(nsub)]
                q += 4 + 16 * nsub + 4
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


if __name__ == '__main__':
    m = Mcd(sys.argv[1])
    print('MDL v0x%x  %d strings  %d bones (%d jiggle)  %d meshes  %d LODs  objects %s' % (
        m.version, len(m.strings), len(m.bones), sum(b['jiggle'] != -1 for b in m.bones), len(m.meshes),
        len(m.lods), [o[0] for o in m.objects]))
    for k, mb in enumerate(m.meshes):
        tris = m.lods[0][k]['triangles'] if m.lods else []
        print('  mesh %d %-18s %6d verts %6d tris  infl %d  shapes %3d  streams %s' % (
            k, mb['material'], mb['vertices'], len(tris), mb['skin_bones'].shape[1], len(mb['shapes']),
            [s[0] for s in mb['streams']]))
