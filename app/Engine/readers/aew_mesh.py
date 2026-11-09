"""AEW Fight Forever (UE 4.27, PS4) cooked USkeletalMesh reader - research version, built step by step.
    aew_mesh.py <SkeletalMesh .uasset>
"""
import os
import struct
import sys

import numpy as np

from zenpkg import Pkg, Reader  # noqa: E402


class R:
    def __init__(self, pkg, d, o):
        self.p, self.d, self.o = pkg, d, o

    def u8(self):
        self.o += 1
        return self.d[self.o - 1]

    def un(self, fmt):
        v = struct.unpack_from('<' + fmt, self.d, self.o)
        self.o += struct.calcsize('<' + fmt)
        return v if len(v) > 1 else v[0]

    def name(self):
        i, n = self.un('II')
        return self.p.fname(i, n)

    def arr(self, dtype, count=None):
        n = self.un('i') if count is None else count
        a = np.frombuffer(self.d, dtype, n, self.o)
        self.o += a.nbytes
        return a


def read(path):
    pkg = Pkg(open(path, 'rb').read())
    base = os.path.splitext(os.path.basename(path))[0]
    for e, s, end in pkg.export_data():
        if e['name'] == base:
            break
    pr = Reader(pkg, s, end)
    props = pr.props()
    r = R(pkg, pkg.d, pr.o)
    out = {'props': props, 'end': end}
    has_guid = r.un('i')
    if has_guid:
        r.o += 16
    out['strip'] = (r.u8(), r.u8())
    out['bounds'] = r.un('7f')
    mats = []
    for _ in range(r.un('i')):
        mi = r.un('i')
        slot = r.name()
        ser_imp = r.un('i')
        imp = r.name() if ser_imp else None
        init, over = r.un('ii')
        dens = r.un('4f')
        mats.append({'material': mi, 'slot': slot, 'imported': imp, 'uv_density': dens})
    out['materials'] = mats
    # FReferenceSkeleton
    bones = []
    for _ in range(r.un('i')):
        nm = r.name()
        par = r.un('i')
        bones.append([nm, par])
    pose = []
    for _ in range(r.un('i')):
        q = r.un('4f')
        t = r.un('3f')
        sc = r.un('3f')
        pose.append((q, t, sc))
    for b, ps in zip(bones, pose):
        b.append(ps)
    nmap = r.un('i')
    r.o += nmap * 12          # TMap<FName, int32> NameToIndex
    out['bones'] = bones
    out['after_skeleton'] = r.o
    # FSkeletalMeshRenderData
    out['cooked'] = r.un('i')
    nlod = r.un('i')
    out['lods'] = []
    for li in range(nlod):
        lod = {'strip': (r.u8(), r.u8())}
        lod['cooked_out'], lod['inlined'] = r.un('ii')
        lod['required_bones'] = r.arr('<u2')
        if lod['cooked_out']:
            out['lods'].append(lod)
            continue
        secs = []
        for _ in range(r.un('i')):
            s = {}
            s['flag'], s['material'], s['base_index'], s['triangles'] = r.un('HHII')
            s['recompute_tangent'] = r.un('i')
            s['tangent_mask'] = r.u8()
            s['cast_shadow'], s['base_vertex'] = r.un('ii')
            s['cloth_mapping'] = r.arr('u1', r.un('i') * 64)       # FMeshToMeshVertData, 64 bytes each
            s['bone_map'] = r.arr('<u2')
            s['vertices'], s['max_influences'] = r.un('ii')
            s['cloth_asset'] = r.un('h')
            r.o += 16                                               # clothing asset guid
            s['cloth_lod'] = r.un('i')
            s['dup_vert_data'] = r.arr('<u4')
            s['dup_vert_index'] = r.arr('<u4', r.un('i') * 2)
            s['disabled'] = r.un('i')
            secs.append(s)
        lod['sections'] = secs
        lod['active_bones'] = r.arr('<u2')
        lod['buffers_size'] = r.un('I')
        if lod['inlined']:
            streamables(r, lod, out)
        out['lods'].append(lod)
        break   # (LOD 0 is all a port needs; later LODs follow the same layout)
    return out, r


def bulk(r, dtype=None):
    es, n = r.un('ii')
    a = np.frombuffer(r.d, 'u1', es * n, r.o)
    r.o += es * n
    return (a.view(dtype) if dtype else a), es, n


def streamables(r, lod, out):
    start = r.o
    lod['s_strip'] = (r.u8(), r.u8())
    isz = r.u8()
    a, es, n = bulk(r)
    lod['indices'] = a.view('<u%d' % isz)
    stride, nv = r.un('II')
    a, es, n = bulk(r, '<f4')
    lod['positions'] = a.reshape(-1, 3)
    r.u8(), r.u8()
    ntc, nv2 = r.un('II')
    full_uv, high_tan = r.un('ii')
    a, es, n = bulk(r)
    t = a.reshape(n, es).view('i1' if not high_tan else '<i2').astype(np.float32) / (127.0 if not high_tan else 32767.0)
    lod['tangent_x'] = t[:, 0:4]      # TangentX (w unused)
    lod['tangent_z'] = t[:, 4:8]      # normal, w = binormal sign
    a, es, n = bulk(r)
    lod['uvs'] = a.view('<f4' if full_uv else '<f2').reshape(-1, ntc, 2).astype(np.float32)   # (count = verts x ntc)
    # skin weights
    r.u8(), r.u8()
    variable, maxinf, nbones, nverts = r.un('iiII')
    use16 = r.un('i')
    a, es, n = bulk(r)
    bsz = 2 if use16 else 1
    rec = a.reshape(nverts, maxinf * (bsz + 1))
    lod['skin_slots'] = rec[:, :maxinf * bsz].copy().view('<u2' if use16 else 'u1').reshape(nverts, maxinf)
    lod['skin_weights'] = rec[:, maxinf * bsz:].astype(np.float32) / 255.0
    lod['skin_meta'] = dict(variable=variable, max_influences=maxinf, use16=use16)
    lod['after_skin'] = r.o
    lod['streamables_start'] = start


def global_bones(lod):
    """per-vertex skin slots -> skeleton bone indices through each section's bone map"""
    nv = len(lod['positions'])
    out = np.zeros_like(lod['skin_slots'], dtype=np.int32)
    for s in lod['sections']:
        a, b = s['base_vertex'], s['base_vertex'] + s['vertices']
        bm = np.asarray(s['bone_map'], np.int32)
        out[a:b] = bm[np.minimum(lod['skin_slots'][a:b], len(bm) - 1)]
    return out


if __name__ == '__main__':
    out, r = read(sys.argv[1])
    print('bounds', [round(x, 1) for x in out['bounds']], 'strip', out['strip'])
    for m in out['materials']:
        print('  mat', m['material'], m['slot'], m['imported'], [round(x, 1) for x in m['uv_density']])
    print('%d bones' % len(out['bones']))
    for b in out['bones'][:8]:
        print('  ', b[0], b[1], [round(x, 3) for x in b[2][0]], [round(x, 2) for x in b[2][1]], [round(x, 2) for x in b[2][2]])
    print('after skeleton @%d' % r.o, r.d[r.o:r.o + 96].hex(' ', 4))
