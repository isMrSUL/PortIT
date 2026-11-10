"""XXH3-64 (seed 0) in Python + NumPy - the same value as ddshash.exe (Zig's std.hash.XxHash3), which the SvR 2011
PC port's texture packs name their files by.

PortIT 1.0.1 (2026-11-10): users' PCs blocked the small native ddshash.exe (unsigned; antivirus / Smart App Control)
- "ddshash failed: " with no message on every full-detail port. This needs no executable. It follows Zig 0.13's
lib/std/hash/xxhash.zig line by line; checked equal to ddshash.exe on every length 0 - 2100 and on the textures of
real ports (see xxh3_check in PortIT's notes).
"""
import struct

import numpy as np

SECRET = bytes((
    0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
    0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
    0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
    0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
    0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
    0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
    0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
    0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
    0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
    0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
    0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
    0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
))
M64 = (1 << 64) - 1
P32_1, P32_2, P32_3 = 0x9E3779B1, 0x85EBCA77, 0xC2B2AE3D
P64_1, P64_2, P64_3, P64_4, P64_5 = (0x9E3779B185EBCA87, 0xC2B2AE3D27D4EB4F, 0x165667B19E3779F9,
                                     0x85EBCA77C2B2AE63, 0x27D4EB2F165667C5)
MX1, MX2 = 0x165667919E3779F9, 0x9FB21C651E98DF25


def _u64(b, o):
    return struct.unpack_from('<Q', b, o)[0]


def _u32(b, o):
    return struct.unpack_from('<I', b, o)[0]


def _rotl(x, r):
    return ((x << r) | (x >> (64 - r))) & M64


def _avalanche_h3(x):
    x = ((x ^ (x >> 37)) * MX1) & M64
    return x ^ (x >> 32)


def _avalanche_h64(x):
    x = ((x ^ (x >> 33)) * P64_2) & M64
    x = ((x ^ (x >> 29)) * P64_3) & M64
    return x ^ (x >> 32)


def _rrmxmx(x, n):
    x = ((x ^ _rotl(x, 49) ^ _rotl(x, 24)) * MX2) & M64
    x = ((x ^ (((x >> 35) + n) & M64)) * MX2) & M64
    return x ^ (x >> 28)


def _fold(a, b):
    p = a * b
    return (p & M64) ^ (p >> 64)


def _mix16(inp, io, sec, so):
    return _fold(_u64(inp, io) ^ _u64(sec, so), _u64(inp, io + 8) ^ _u64(sec, so + 8))


def _hash_long(data):
    n = len(data)
    stripes = (n - 1) // 64                      # (whole 64-byte stripes before the last block)
    sec64 = np.frombuffer(SECRET, np.uint8)
    # the secret's 8-byte words at every byte offset s*8 (stripe s of a block uses secret[s*8 : s*8+64])
    keys = np.stack([sec64[s * 8:s * 8 + 64].view('<u8') for s in range(16)])          # (16, 8)
    acc = np.array([P32_3, P64_1, P64_2, P64_3, P64_4, P32_2, P64_5, P32_1], dtype=np.uint64)
    scr = np.frombuffer(SECRET[128:192], '<u8')
    swap = [1, 0, 3, 2, 5, 4, 7, 6]
    lo32 = np.uint64(0xFFFFFFFF)
    with np.errstate(over='ignore'):
        if stripes:
            d = np.frombuffer(data, '<u8', count=stripes * 8).reshape(stripes, 8)
            full = stripes // 16
            # every stripe's two additions (mod 2**64: NumPy's uint64 wraps): lo*hi of data^key, and data swapped
            # in lane pairs; summed per block of 16 stripes, then a scramble after each whole block
            k = np.tile(keys, (full + 1, 1))[:stripes]
            mixed = d ^ k
            add = (mixed & lo32) * (mixed >> np.uint64(32)) + d[:, swap]
            for b in range(full):
                acc = acc + add[b * 16:(b + 1) * 16].sum(axis=0, dtype=np.uint64)
                acc = acc ^ (acc >> np.uint64(47))
                acc = acc ^ scr
                acc = acc * np.uint64(P32_1)
            if stripes > full * 16:
                acc = acc + add[full * 16:].sum(axis=0, dtype=np.uint64)
        last = np.frombuffer(data[n - 64:], '<u8')
        key = np.frombuffer(SECRET[192 - 64 - 7:192 - 7], '<u8')
        mixed = last ^ key
        acc = acc + (mixed & lo32) * (mixed >> np.uint64(32)) + last[swap]
        acc = acc ^ np.frombuffer(SECRET[11:11 + 64], '<u8')
    a = [int(x) for x in acc]
    r = (P64_1 * n) & M64
    for i in range(4):
        r = (r + _fold(a[2 * i], a[2 * i + 1])) & M64
    return _avalanche_h3(r)


def xxh3_64(data):
    """XXH3-64 (seed 0) of bytes"""
    data = bytes(data)
    n = len(data)
    s = SECRET
    if n > 240:
        return _hash_long(data)
    if n > 128:
        acc = (P64_1 * n) & M64
        for i in range(8):
            acc = (acc + _mix16(data, i * 16, s, i * 16)) & M64
        acc_end = _mix16(data, n - 16, s, 136 - 17)
        for i in range(8, n // 16):
            acc_end = (acc_end + _mix16(data, i * 16, s, (i - 8) * 16 + 3)) & M64
        return _avalanche_h3((_avalanche_h3(acc) + acc_end) & M64)
    if n > 16:
        acc = (P64_1 * n) & M64
        for i in range(4):
            io, so = 48 - i * 16, 96 - i * 32
            if n > so:
                acc = (acc + _mix16(data, io, s, so)) & M64
                acc = (acc + _mix16(data, n - (io + 16), s, so + 16)) & M64
        return _avalanche_h3(acc)
    if n > 8:
        lo = _u64(data, 0) ^ (_u64(s, 24) ^ _u64(s, 32))
        hi = _u64(data, n - 8) ^ (_u64(s, 40) ^ _u64(s, 48))
        bswap = int.from_bytes(lo.to_bytes(8, 'little'), 'big')
        return _avalanche_h3((n + bswap + hi + _fold(lo, hi)) & M64)
    if n > 3:
        key = _u64(s, 8) ^ _u64(s, 16)
        combined = ((_u32(data, 0) << 32) + _u32(data, n - 4)) & M64
        return _rrmxmx(key ^ combined, n)
    if n > 0:
        key = _u32(s, 0) ^ _u32(s, 4)
        blk = data[n - 1] | (n << 8) | (data[0] << 16) | (data[n // 2] << 24)
        return _avalanche_h64(key ^ blk)
    return _avalanche_h64(_u64(s, 56) ^ _u64(s, 64))


def file_hash(path, offset, length):
    """XXH3-64 of a byte range of a file, as 16 hex digits (ddshash.exe's output)"""
    with open(path, 'rb') as f:
        f.seek(offset)
        data = f.read(length)
    return '%016x' % xxh3_64(data)
