"""Renders a built SvR 2011 ch.pac (what the game will load): the body / hair / lash JBOY models with the attire's
colour sheet, lit, front and side views -> png. Used as PortIT's preview of the result.
    preview.py <ch.pac> <out.png> [attire index]"""
import io
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), 'svr'))
import ch_tool  # noqa: E402
import jboy  # noqa: E402
import svrfmt  # noqa: E402


def bundle_textures(raw):
    c = struct.unpack_from('<I', raw, 0)[0]
    out = {}
    for k in range(c):
        rec = raw[16 + 32 * k:48 + 32 * k]
        nm = rec[:16].split(b'\0')[0].decode('latin1')
        size, off = struct.unpack_from('<II', rec, 20)
        dds = raw[off:off + size]
        if dds[:4] == b'DDS ':
            try:
                im = Image.open(io.BytesIO(dds))
                im.load()
                out[nm] = im.convert('RGBA')
            except Exception:
                pass
    return out


def load(pac, attire=0):
    h, groups = ch_tool.epk8_read(open(pac, 'rb').read())
    ents = groups[0][1]
    name, blob = ents[min(attire, len(ents) - 1)]
    models, tex = {}, {}
    for i, data in svrfmt.pach_read(blob):
        raw = svrfmt.unbpe(data)
        if raw[:4] == b'JBOY' and i in (0, 0x2710, 0x4e20):
            models[i] = jboy.read(raw)
        elif ch_tool.is_bundle(raw):
            tex.update(bundle_textures(raw))
    return name, models, tex


def tris_of(mesh):
    out = []
    for prim, idx in mesh.strips:
        for k in range(len(idx) - 2):
            a, b, c = idx[k], idx[k + 1], idx[k + 2]
            if a == b or b == c or a == c:
                continue
            out.append((a, b, c) if k % 2 == 0 else (b, a, c))
    return np.array(out, np.int64).reshape(-1, 3)


def render(pac, out, attire=0, size=(1000, 760)):
    """z-buffer render, per-pixel texture (bilinear-free nearest at the full sheet size), smooth vertex normals"""
    name, models, tex = load(pac, attire)
    color = tex.get('color')
    col = np.asarray(color).astype(np.float32) if color is not None else None
    parts = []          # (V, N, UV, T, textured, cutout)
    for key, m in models.items():
        for me in m.meshes:
            T = tris_of(me)
            if not len(T):
                continue
            V = np.array([v[:3] for v in me.verts], float)
            N = np.array([v[3:6] for v in me.verts], float)
            UV = np.array(me.uvs, float)
            cut = bool(struct.unpack_from('>I', me.raw, 0x88)[0] & 0x110)
            # (the eyes' shader, yReflect, reads the same colour sheet - drawn white before: no iris in the preview)
            parts.append((V, N, UV, T, col is not None, cut))
    W, H = size
    img = np.zeros((H, W, 3), np.float32)
    img[:] = (13, 17, 24)
    if not parts:
        Image.fromarray(img.astype(np.uint8)).save(out)
        return out
    allv = np.concatenate([p[0] for p in parts])
    lo, hi = allv.min(0), allv.max(0)
    sc = (H - 50) / max(hi[1] - lo[1], 1e-6)
    light = np.array([0.35, -0.55, -0.75])
    light /= np.linalg.norm(light)
    th, tw = (col.shape[:2] if col is not None else (1, 1))
    for view, cx in ((0, W * 0.3), (1, W * 0.76)):
        zb = np.full((H, W), np.inf, np.float32)
        for V, N, UV, T, textured, cut in parts:
            x = V[:, 0] - (lo[0] + hi[0]) / 2 if view == 0 else -(V[:, 2] - (lo[2] + hi[2]) / 2)
            sx = cx + x * sc
            sy = 25 + (V[:, 1] - lo[1]) * sc
            depth = V[:, 2] if view == 0 else V[:, 0]
            for t in T:
                xs, ys = sx[t], sy[t]
                x0, x1 = int(max(0, np.floor(xs.min()))), int(min(W - 1, np.ceil(xs.max())))
                y0, y1 = int(max(0, np.floor(ys.min()))), int(min(H - 1, np.ceil(ys.max())))
                if x1 < x0 or y1 < y0:
                    continue
                gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
                d = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
                if abs(d) < 1e-9:
                    continue
                b1 = ((gx - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (gy - ys[0])) / d
                b2 = ((xs[1] - xs[0]) * (gy - ys[0]) - (gx - xs[0]) * (ys[1] - ys[0])) / d
                b0 = 1 - b1 - b2
                ins = (b0 >= 0) & (b1 >= 0) & (b2 >= 0)
                if not ins.any():
                    continue
                z = b0 * depth[t[0]] + b1 * depth[t[1]] + b2 * depth[t[2]]
                if view == 1:
                    z = -z
                py, px = np.nonzero(ins)
                py, px = py + y0, px + x0
                zz = z[ins]
                closer = zz < zb[py, px]
                if not closer.any():
                    continue
                w0, w1, w2 = b0[ins][closer], b1[ins][closer], b2[ins][closer]
                py, px, zz = py[closer], px[closer], zz[closer]
                if textured:
                    u = (w0 * UV[t[0], 0] + w1 * UV[t[1], 0] + w2 * UV[t[2], 0]) % 1.0
                    v = (w0 * UV[t[0], 1] + w1 * UV[t[1], 1] + w2 * UV[t[2], 1]) % 1.0
                    c = col[(v * (th - 1)).astype(int), (u * (tw - 1)).astype(int)]
                    if cut:
                        keep = c[:, 3] > 90
                        py, px, zz, c, w0, w1, w2 = py[keep], px[keep], zz[keep], c[keep], w0[keep], w1[keep], w2[keep]
                    rgb = c[:, :3]
                else:
                    rgb = np.tile([235.0, 235.0, 240.0], (len(py), 1))
                n = w0[:, None] * N[t[0]] + w1[:, None] * N[t[1]] + w2[:, None] * N[t[2]]
                n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)
                sh = 0.38 + 0.62 * np.abs(n @ light)
                zb[py, px] = zz
                img[py, px] = np.clip(rgb * sh[:, None], 0, 255)
    im = Image.fromarray(img.astype(np.uint8))
    ImageDraw.Draw(im).text((10, 6), 'the built ch.pac  -  front / side', fill=(150, 165, 190))
    im.save(out)
    return out


if __name__ == '__main__':
    render(sys.argv[1], sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 0)


# ---------------------------------------------------------------- the 2K source, before porting
def render_source(src, out, size=(1000, 760), lod=1, tex_side=512, title='the 2K model, before porting'):
    """A quick look at a source wrestler (source_2k.load's dict) before porting: front / side, every mesh with its own
    colour texture (shrunk to tex_side), a lower LOD of the .mcd (lod; LOD 0 if it has none) - a few seconds, so
    choosing a wrestler shows who it is. The .mcd's axes are SvR's: -Y up, the face toward -Z, +X his left. Hair cards: their vertex
    colour (stored linear) with the rai map's green as the cut-out; alternate looks (variants) left out."""
    from mcd_parse import Mcd
    lods = {}

    def tris_for(me):
        key = me.source_path
        if key not in lods:
            try:
                m = Mcd(key)
                lods[key] = m.lods
            except Exception:   # noqa: BLE001
                lods[key] = []
        L = lods[key]
        if L and len(L) > lod:
            for row in L[lod]:
                if row['mesh'] == me.index:
                    return np.asarray(row['triangles'], np.int64)
        return me.tris

    cache = {}

    def image(path, mode):
        k = (path, mode)
        if k not in cache:
            try:
                im = Image.open(path)
                im.load()
                im = im.convert('RGBA')
                if max(im.size) > tex_side:
                    im = im.resize((tex_side, max(1, tex_side * im.size[1] // im.size[0])), Image.BILINEAR)
                cache[k] = np.asarray(im).astype(np.float32)
            except Exception:   # noqa: BLE001
                cache[k] = None
        return cache[k]

    parts = []      # (V, N, UV, T, texture rgba or None, cutout alpha (texture, channel) or None, solid rgb)
    for me in src['meshes']:
        if getattr(me, 'variant', False) or me.kind == 'teeth' or me.material.lower() in ('m_tongue', 'm_teeth'):
            continue
        T = tris_for(me)
        if not len(T):
            continue
        V = np.asarray(me.positions, float)
        N = np.asarray(me.normals, float)
        UV = np.asarray(me.uvs, float)
        texs = me.textures or {}
        col = image(texs['color'], 'c') if 'color' in texs else None
        cut, solid = None, (200, 200, 205)
        if me.kind in ('hair',) or (me.cutout and col is None):
            rai = image(texs['rai'], 'r') if 'rai' in texs else None
            if rai is not None:
                cut = (rai, 1)
            c = np.median(me.colors[:, :3], 0) if getattr(me, 'colors', None) is not None and len(me.colors) else np.array([.05, .04, .03])
            c = np.where(c <= 0.0031308, 12.92 * c, 1.055 * np.clip(c, 0, 1) ** (1 / 2.4) - 0.055)
            solid = tuple(float(x) * 255 for x in c)
            col = None
        elif me.cutout and col is not None:
            cut = (col, 3)
        parts.append((V, N, UV, T, col, cut, solid))
    W, H = size
    img = np.zeros((H, W, 3), np.float32) + np.array([13, 17, 24], np.float32)
    if not parts:
        Image.fromarray(img.astype(np.uint8)).save(out)
        return out
    allv = np.concatenate([p[0] for p in parts])
    lo, hi = allv.min(0), allv.max(0)
    sc = (H - 50) / max(hi[1] - lo[1], 1e-6)
    light = np.array([0.35, -0.55, -0.75])
    light /= np.linalg.norm(light)
    for view, cx in ((0, W * 0.3), (1, W * 0.76)):
        zb = np.full((H, W), np.inf, np.float32)
        for V, N, UV, T, col, cut, solid in parts:
            x = V[:, 0] - (lo[0] + hi[0]) / 2 if view == 0 else -(V[:, 2] - (lo[2] + hi[2]) / 2)
            sx = cx + x * sc
            sy = 25 + (V[:, 1] - lo[1]) * sc
            depth = V[:, 2] if view == 0 else V[:, 0]
            for t in T:
                xs, ys = sx[t], sy[t]
                x0, x1 = int(max(0, np.floor(xs.min()))), int(min(W - 1, np.ceil(xs.max())))
                y0, y1 = int(max(0, np.floor(ys.min()))), int(min(H - 1, np.ceil(ys.max())))
                if x1 < x0 or y1 < y0:
                    continue
                gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
                d = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
                if abs(d) < 1e-9:
                    continue
                b1 = ((gx - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (gy - ys[0])) / d
                b2 = ((xs[1] - xs[0]) * (gy - ys[0]) - (gx - xs[0]) * (ys[1] - ys[0])) / d
                b0 = 1 - b1 - b2
                ins = (b0 >= 0) & (b1 >= 0) & (b2 >= 0)
                if not ins.any():
                    continue
                z = b0 * depth[t[0]] + b1 * depth[t[1]] + b2 * depth[t[2]]
                py, px = np.nonzero(ins)
                py, px, zz = py + y0, px + x0, z[ins]
                w0, w1, w2 = b0[ins], b1[ins], b2[ins]
                keep = zz < zb[py, px]
                if not keep.any():
                    continue
                py, px, zz, w0, w1, w2 = py[keep], px[keep], zz[keep], w0[keep], w1[keep], w2[keep]
                u = (w0 * UV[t[0], 0] + w1 * UV[t[1], 0] + w2 * UV[t[2], 0]) % 1.0
                v = (w0 * UV[t[0], 1] + w1 * UV[t[1], 1] + w2 * UV[t[2], 1]) % 1.0
                if cut is not None:
                    a, ch = cut
                    ah, aw = a.shape[:2]
                    ok = a[(v * (ah - 1)).astype(int), (u * (aw - 1)).astype(int), ch] > 110
                    py, px, zz, w0, w1, w2, u, v = py[ok], px[ok], zz[ok], w0[ok], w1[ok], w2[ok], u[ok], v[ok]
                    if not len(py):
                        continue
                if col is not None:
                    th, tw = col.shape[:2]
                    rgb = col[(v * (th - 1)).astype(int), (u * (tw - 1)).astype(int), :3]
                else:
                    rgb = np.tile(np.array(solid, np.float32), (len(py), 1))
                n = w0[:, None] * N[t[0]] + w1[:, None] * N[t[1]] + w2[:, None] * N[t[2]]
                n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)
                sh = 0.38 + 0.62 * np.abs(n @ light)
                zb[py, px] = zz
                img[py, px] = np.clip(rgb * sh[:, None], 0, 255)
    im = Image.fromarray(img.astype(np.uint8))
    ImageDraw.Draw(im).text((10, 6), title + '  -  front / side', fill=(150, 165, 190))
    im.save(out)
    return out
