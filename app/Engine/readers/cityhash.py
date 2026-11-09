"""CityHash64 (Google CityHash v1.1, as Unreal Engine uses it) and UE 4.27 IoStore import ids.

A zen package's import map holds 64-bit ids, not names: for an object in another package
    id = CityHash64(UTF-16LE of the lower-case path '/game/.../package/object') with the top two bits cleared
(UE FPackageObjectIndex::GenerateImportHashFromObjectPath; '.' and ':' turned into '/'). PortIT 2026-11-09: AEW
material slots link their material instance by import index - matched by this hash instead of by name (the names
differ: mt_Hands_L_01 -> MI_Wrists_L_01, mt_Kneepads_R_01 -> MI_Kneepad_R_01: those parts came out untextured).
"""
import struct

M64 = (1 << 64) - 1
K0, K1, K2 = 0xc3a5c85c97cb3127, 0xb492b66fbe98f273, 0x9ae16a3b2f90404f
KMUL = 0x9ddfea08eb382d69


def _f64(s, i):
    return struct.unpack_from('<Q', s, i)[0]


def _f32(s, i):
    return struct.unpack_from('<I', s, i)[0]


def _rot(v, s):
    return v if s == 0 else ((v >> s) | (v << (64 - s))) & M64


def _smix(v):
    return (v ^ (v >> 47)) & M64


def _len16(u, v, mul=KMUL):
    a = ((u ^ v) * mul) & M64
    a ^= a >> 47
    b = ((v ^ a) * mul) & M64
    b ^= b >> 47
    return (b * mul) & M64


def _len0to16(s):
    n = len(s)
    if n >= 8:
        mul = (K2 + n * 2) & M64
        a = (_f64(s, 0) + K2) & M64
        b = _f64(s, n - 8)
        c = (_rot(b, 37) * mul + a) & M64
        d = ((_rot(a, 25) + b) * mul) & M64
        return _len16(c, d, mul)
    if n >= 4:
        mul = (K2 + n * 2) & M64
        a = _f32(s, 0)
        return _len16((n + (a << 3)) & M64, _f32(s, n - 4), mul)
    if n > 0:
        a, b, c = s[0], s[n >> 1], s[n - 1]
        y = (a + (b << 8)) & 0xFFFFFFFF
        z = (n + (c << 2)) & 0xFFFFFFFF
        return (_smix(((y * K2) ^ (z * K0)) & M64) * K2) & M64
    return K2


def _len17to32(s):
    n = len(s)
    mul = (K2 + n * 2) & M64
    a = (_f64(s, 0) * K1) & M64
    b = _f64(s, 8)
    c = (_f64(s, n - 8) * mul) & M64
    d = (_f64(s, n - 16) * K2) & M64
    return _len16((_rot((a + b) & M64, 43) + _rot(c, 30) + d) & M64,
                  (a + _rot((b + K2) & M64, 18) + c) & M64, mul)


def _weak32(w, x, y, z, a, b):
    a = (a + w) & M64
    b = _rot((b + a + z) & M64, 21)
    c = a
    a = (a + x + y) & M64
    b = (b + _rot(a, 44)) & M64
    return (a + z) & M64, (b + c) & M64


def _weak32s(s, i, a, b):
    return _weak32(_f64(s, i), _f64(s, i + 8), _f64(s, i + 16), _f64(s, i + 24), a, b)


def _len33to64(s):
    n = len(s)
    mul = (K2 + n * 2) & M64
    a = (_f64(s, 0) * K2) & M64
    b = _f64(s, 8)
    c = _f64(s, n - 24)
    d = _f64(s, n - 32)
    e = (_f64(s, 16) * K2) & M64
    f = (_f64(s, 24) * 9) & M64
    g = _f64(s, n - 8)
    h = (_f64(s, n - 16) * mul) & M64
    u = (_rot((a + g) & M64, 43) + (_rot(b, 30) + c) * 9) & M64
    v = ((((a + g) & M64) ^ d) + f + 1) & M64
    w = (_bswap(((u + v) * mul) & M64) + h) & M64
    x = (_rot((e + f) & M64, 42) + c) & M64
    y = ((_bswap(((v + w) * mul) & M64) + g) * mul) & M64
    z = (e + f + c) & M64
    a = (_bswap(((x + z) * mul + y) & M64) + b) & M64
    b = (_smix(((z + a) * mul + d + h) & M64) * mul) & M64
    return (b + x) & M64


def _bswap(v):
    return int.from_bytes((v & M64).to_bytes(8, 'little'), 'big')


def cityhash64(s):
    n = len(s)
    if n <= 32:
        return _len0to16(s) if n <= 16 else _len17to32(s)
    if n <= 64:
        return _len33to64(s)
    x = _f64(s, n - 40)
    y = (_f64(s, n - 16) + _f64(s, n - 56)) & M64
    z = _len16((_f64(s, n - 48) + n) & M64, _f64(s, n - 24))
    v = _weak32s(s, n - 64, n, z)
    w = _weak32s(s, n - 32, (y + K1) & M64, x)
    x = ((x * K1) + _f64(s, 0)) & M64
    end = ((n - 1) // 64) * 64
    i = 0
    while True:
        x = (_rot((x + y + v[0] + _f64(s, i + 8)) & M64, 37) * K1) & M64
        y = (_rot((y + v[1] + _f64(s, i + 48)) & M64, 42) * K1) & M64
        x ^= w[1]
        y = (y + v[0] + _f64(s, i + 40)) & M64
        z = (_rot((z + w[0]) & M64, 33) * K1) & M64
        v = _weak32s(s, i, (v[1] * K1) & M64, (x + w[0]) & M64)
        w = _weak32s(s, i + 32, (z + w[1]) & M64, (y + _f64(s, i + 16)) & M64)
        z, x = x, z
        i += 64
        if i == end:
            break
    return _len16((_len16(v[0], w[0]) + _smix(y) * K1 + z) & M64, (_len16(v[1], w[1]) + x) & M64)


def import_id(object_path):
    """UE 4.27 import hash of '/Game/.../Package.Object' (or '/Game/.../Package/Object')"""
    p = object_path.replace('.', '/').replace(':', '/').lower()
    return cityhash64(p.encode('utf-16-le')) & ~(3 << 62) & M64
