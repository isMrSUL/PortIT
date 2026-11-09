"""AEW (UE 4.27, PS4) Texture2D reader: header + mips from .uasset / .ubulk, PS4 un-tiling, DDS / PNG out.
    aew_tex.py <T_x.uasset> [out.png]"""
import io
import os
import struct
import sys

import numpy as np

from zenpkg import Pkg, Reader  # noqa: E402

BLOCK = {'PF_DXT1': (8, 4), 'PF_DXT5': (16, 4), 'PF_BC5': (16, 4), 'PF_BC4': (8, 4), 'PF_BC7': (16, 4),
         'PF_B8G8R8A8': (4, 1), 'PF_G8': (1, 1), 'PF_BC6H': (16, 4)}


def read(path):
    pkg = Pkg(open(path, 'rb').read())
    for e, s, end in pkg.export_data():
        if e['name'] == os.path.splitext(os.path.basename(path))[0]:
            break
    r = Reader(pkg, s, end)
    props = r.props()
    d = pkg.d
    o = r.o
    o += 4                      # UObject guid flag (0)
    o += 2                      # strip flags (UTexture)
    o += 2                      # strip flags (UTexture2D)
    cooked = struct.unpack_from('<i', d, o)[0]
    o += 4
    # pixel format name (FName) then FTexturePlatformData
    fmt_name = pkg.fname(*struct.unpack_from('<II', d, o))
    o += 8
    skip = struct.unpack_from('<q', d, o)[0]     # SkipOffset
    o += 8
    sx, sy, packed = struct.unpack_from('<iiI', d, o)
    o += 12
    n = struct.unpack_from('<i', d, o)[0]
    fmt = d[o + 4:o + 4 + n - 1].decode('latin1')
    o += 4 + n
    first_mip, nmips = struct.unpack_from('<ii', d, o)
    o += 8
    mips = []
    ubulk = os.path.splitext(path)[0] + '.ubulk'
    for i in range(nmips):
        cooked_mip = struct.unpack_from('<i', d, o)[0]
        o += 4
        flags, count = struct.unpack_from('<Ii', d, o)
        o += 8
        size, off = struct.unpack_from('<iq', d, o)
        o += 12
        if flags & 0x20:          # BULKDATA_Unused (no payload)
            src = ('none', 0, 0)
        elif flags & 0x100:       # BULKDATA_PayloadInSeperateFile -> .ubulk
            src = ('ubulk', off, size)
        else:                     # inline right here
            src = ('inline', o, size)
            o += size
        mx, my, mz = struct.unpack_from('<iii', d, o)
        o += 12
        mips.append({'flags': flags, 'count': count, 'size': size, 'offset': off, 'src': src, 'w': mx, 'h': my})
    return {'props': props, 'fmt': fmt, 'w': sx, 'h': sy, 'mips': mips, 'ubulk': ubulk, 'pkg': pkg}


def mip_bytes(t, i):
    m = t['mips'][i]
    kind, off, size = m['src']
    if kind == 'inline':
        return t['pkg'].d[off:off + size]
    with open(t['ubulk'], 'rb') as f:
        f.seek(off)
        return f.read(size)


def dds(fmt, w, h, data):
    fourcc = {'PF_DXT1': b'DXT1', 'PF_DXT5': b'DXT5', 'PF_BC5': b'ATI2', 'PF_BC4': b'ATI1'}.get(fmt)
    hdr = bytearray(128)
    hdr[0:4] = b'DDS '
    struct.pack_into('<7I', hdr, 4, 124, 0x1007 | 0x80000, h, w, len(data), 0, 1)
    struct.pack_into('<2I', hdr, 76, 32, 4)
    if fourcc:
        hdr[84:88] = fourcc
        return bytes(hdr) + data
    hdr[84:88] = b'DX10'          # BC7: DX10 header
    dx10 = struct.pack('<5I', 98, 3, 0, 1, 0)
    return bytes(hdr) + dx10 + data


def to_image(fmt, w, h, data):
    from PIL import Image
    return Image.open(io.BytesIO(dds(fmt, w, h, data)))


if __name__ == '__main__':
    t = read(sys.argv[1])
    print(t['fmt'], t['w'], t['h'], 'mips', len(t['mips']))
    for i, m in enumerate(t['mips'][:4]):
        print('  mip', i, m['w'], m['h'], 'size', m['size'], 'flags %x' % m['flags'], m['src'][0], 'off', m['offset'])


def _morton8():
    xs, ys = [], []
    for t in range(64):
        x = y = 0
        for b in range(3):
            x |= ((t >> (2 * b)) & 1) << b
            y |= ((t >> (2 * b + 1)) & 1) << b
        xs.append(x)
        ys.append(y)
    return np.array(xs), np.array(ys)


def untile_ps4(fmt, w, h, data):
    """PS4 GNM tiling: 8x8-block tiles (Morton order inside a tile), tiles row by row; sizes padded to 8 blocks"""
    bpb, bd = BLOCK[fmt]
    bw, bh = max(1, (w + bd - 1) // bd), max(1, (h + bd - 1) // bd)
    tw, th = (bw + 7) // 8, (bh + 7) // 8
    need = tw * th * 64 * bpb
    src = np.frombuffer(data[:need].ljust(need, b'\0'), np.uint8).reshape(th, tw, 64, bpb)
    mx, my = _morton8()
    out = np.zeros((th * 8, tw * 8, bpb), np.uint8)
    for ty in range(th):
        for tx in range(tw):
            out[ty * 8 + my, tx * 8 + mx] = src[ty, tx]
    return out[:bh, :bw].tobytes()
