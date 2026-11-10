"""WWE 2K19 - 2K22 character packs (chNNNNN.pac, Yuke's engine) -> models PortIT reads like a 2K23+ .mdl.

PortIT 2026-11-10. Layout read from the files themselves (2K22 mods by JOEMASHUPS: Cena, Hogan, Bischoff, Kushida,
Kayla Braxton).

The .pac: "EPK8" ... a "PACH" part list; members are packed "ZLIB" (u32 4656, u32 packed, u32 size, data from +16) or
"OODL" (+4 u32 u32 "KRKN", +16 u32 size, u32 packed, Oodle Kraken data from +24 - unpacked with the user's own
oo2core_9_win64.dll from WWE 2K26 / 2K24 / 2K25, oo2core_6 from 2K19 / 2K20), or stored as they are.
Inside: several models (the body + face, the hair, the eyelashes, the clothes) and one texture table
(LE: u32 count, u32, u32, u32, then 32-byte rows {name[16], "dds\\0", u32 size, u32 offset, u32}).

A model ("JBOY", big-endian; every pointer counts from byte 8):
  +16 u32 (3: an older loose layout)  +24 mesh count  +28 -> meshes (184 bytes each)  +32 bone count
  +36 texture-name count  +40 -> bones (80 bytes: name[16], f32 t[3] @16, f32 euler[3] @32, i32 parent @48)
  +44 -> texture names (16 bytes)  +48 -> name groups (32 bytes: name[16], u32 meshes @24)  +52 group count
  +72 face-shape count  +76 -> face-shape names (16 bytes)  +84 -> skin palette {u32 count, -> u32 bone[count]}
  (the bones are 2K's own: J_Hips ... J_Head, J_Jaw - the same world positions as 2K23's .mdl with mcd_parse's maths)
mesh (46 big-endian words w):
  w0 vertices  w2 own palette size (older layout: w3 ... = bone + 1)  w23 weight slots per vertex  w24 name group
  w26 -> {-> block[k]}: block 0 = the vertices (28 bytes: f32 pos[3], f32 normal[3], u8 ARGB), blocks 1 ... = the
         face shapes as offsets (same 28-byte rows; the model's face-shape names in order)
  w27 -> weights: per vertex `slots` x 8 bytes {palette index u8 (u16 when w41 & 0x80; 255 / 65535 = none), pad,
         f32 weight @4}
  w28 -> uv (f32 u, v)  w29 -> tangents (f32 x3)  w37 parameter count  w38 -> {-> parameter}
         parameter: name[16], u16 type, u16 size, value @20 (type 15: a texture, its index in the texture names)
  w39 -> triangle strips {u8 6, u32 index count @4, -> u16 indices @8} (12 bytes each, up to the first index list)
  w40 vertices again  w41 flags (0x80: two-byte palette indices)  +124 shader name[16]

What PortIT makes of it (the 2K23+ pipeline needs no change): the body's 9 pieces become one M_Body, gear is named by
its own texture (the clothes model calls every piece M_Body), the hair / eyelash / clothes bones join the body's
skeleton by name, the head keeps its 119 face shapes (Jaw_Dn is 2K's jaw-open AU27), textures are written as
<stem>_color.dds / <stem>_nrm.dds."""
import ctypes
import glob
import hashlib
import json
import os
import re
import struct
import zlib

import numpy as np

BASE_NAMES = {'M_Head', 'M_Body', 'M_EYE', 'M_transB_eyeshadow', 'M_MouthBag', 'M_Teeth', 'M_Tongue',
              'M_Head_Caruncle', 'M_transB_eyelash', 'M_LowHead', 'M_LowEyeSh'}
FULL = {'M_transB_eyesha': 'M_transB_eyeshadow', 'M_transB_eyelas': 'M_transB_eyelash'}   # (16-byte names cut short)
CACHE = os.path.join(os.environ.get('LOCALAPPDATA', os.path.expanduser('~')), 'PortIT', 'pac_cache')


# ---------------------------------------------------------------- the .pac

def is_pac(path):
    try:
        with open(path, 'rb') as f:
            return f.read(4) in (b'EPK8', b'EPAC')
    except OSError:
        return False


def _oodle():
    """the user's own Oodle (the 2K games ship it) - never part of PortIT"""
    cands = [os.environ.get('PORTIT_OODLE_DLL', '')]
    for steam in (r'C:\Program Files (x86)\Steam', r'C:\Program Files\Steam'):
        for game in ('WWE 2K26', 'WWE 2K25', 'WWE 2K24', 'WWE 2K20', 'WWE 2K19'):
            cands += glob.glob(os.path.join(steam, 'steamapps', 'common', game, 'oo2core_*_win64.dll'))
    for drive in 'CDEFGHIJ':
        for game in ('WWE 2K26', 'WWE 2K25', 'WWE 2K24', 'WWE 2K20', 'WWE 2K19'):
            cands += glob.glob(drive + r':\SteamLibrary\steamapps\common\%s\oo2core_*_win64.dll' % game)
    for p in cands:
        if p and os.path.isfile(p):
            dll = ctypes.WinDLL(p)
            f = dll.OodleLZ_Decompress
            f.restype = ctypes.c_int64
            f.argtypes = [ctypes.c_void_p, ctypes.c_int64, ctypes.c_void_p, ctypes.c_int64] + [ctypes.c_int] * 3 + \
                         [ctypes.c_void_p, ctypes.c_int64, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                          ctypes.c_int64, ctypes.c_int]
            return f
    raise RuntimeError('This pack is packed with Oodle and no WWE 2K game with its oo2core file was found on this PC '
                       '(PortIT uses your own copy; set PORTIT_OODLE_DLL to its path)')


def members(path):
    """every member of a .pac, unpacked: [bytes]"""
    b = open(path, 'rb').read()
    out, spans = [], []
    for m in re.finditer(b'ZLIB', b):
        o = m.start()
        mark, c, r = struct.unpack_from('<III', b, o + 4)
        if mark != 4656 or o + 16 + c > len(b):
            continue
        try:
            x = zlib.decompress(b[o + 16:o + 16 + c])
        except zlib.error:
            continue
        out.append(x)
        spans.append((o, o + 16 + c))
    oodle = None
    for m in re.finditer(b'OODL', b):
        o = m.start()
        if b[o + 12:o + 16] != b'KRKN':
            continue
        usize, csize = struct.unpack_from('<II', b, o + 16)
        oodle = oodle or _oodle()
        src = ctypes.create_string_buffer(b[o + 24:o + 24 + csize], csize)
        dst = ctypes.create_string_buffer(usize + 64)
        got = oodle(src, csize, dst, usize, 1, 0, 0, None, 0, None, None, None, 0, 3)
        if got != usize:
            raise RuntimeError('an Oodle member of %s is damaged' % os.path.basename(path))
        out.append(dst.raw[:usize])
        spans.append((o, o + 24 + csize))
    for m in re.finditer(b'JBOY', b):            # (models stored as they are)
        o = m.start()
        if any(a <= o < e for a, e in spans):
            continue
        L = struct.unpack_from('>I', b, o + 4)[0]
        if struct.unpack_from('>I', b, o + 8)[0] == 0x10 and o + 8 + L <= len(b):
            out.append(b[o:o + 8 + L])
    return out


def textures_of(blob):
    """the texture table member -> {name: dds bytes}, or None if blob is not one"""
    if len(blob) < 48 or blob[32:36] != b'dds\0':
        return None
    n = struct.unpack_from('<I', blob, 0)[0]
    out = {}
    for k in range(n):
        o = 16 + 32 * k
        name = blob[o:o + 16].split(b'\0')[0].decode('ascii', 'replace')
        size, off = struct.unpack_from('<II', blob, o + 20)
        if blob[off:off + 4] == b'DDS ':
            out[name.lower()] = blob[off:off + size]
    return out


# ---------------------------------------------------------------- one JBOY model

def _strip_tris(idx):
    a, b, c = idx[:-2], idx[1:-1], idx[2:]
    k = np.arange(len(a))
    t = np.stack([np.where(k % 2 == 0, a, b), np.where(k % 2 == 0, b, a), c], 1)
    keep = (a != b) & (b != c) & (a != c)
    return t[keep]


def read_jboy(x):
    u = lambda o: struct.unpack_from('>I', x, o)[0]
    nm = lambda o: x[o:o + 16].split(b'\0')[0].decode('ascii', 'replace')
    count, base, nbones, ntex = u(24), u(28) + 8, u(32), u(36)
    older = base < 88
    bones = []
    bp = u(40) + 8
    for i in range(nbones):
        o = bp + 80 * i
        bones.append({'name': nm(o), 't': struct.unpack_from('>3f', x, o + 16),
                      'euler': struct.unpack_from('>3f', x, o + 32), 'parent': struct.unpack_from('>i', x, o + 48)[0]})
    tp = u(44) + 8
    tex = [nm(tp + 16 * k).lower() for k in range(ntex)]
    gp = u(48) + 8
    groups = [FULL.get(nm(gp + 32 * k), nm(gp + 32 * k)) for k in range(u(52))]
    shape_names = []
    if not older and u(72) < 1000:
        sp = u(76) + 8
        shape_names = [nm(sp + 16 * k) for k in range(u(72))]
    palette = None
    if not older:
        sp = u(84) + 8
        palette = np.frombuffer(x, '>u4', u(sp), u(sp + 4) + 8).astype(np.int64)
    meshes = []
    for i in range(count):
        o = base + 184 * i
        w = [u(o + 4 * k) for k in range(46)]
        n, slots = w[0], w[23]
        vp, wp, uvp, endp, ip = (w[k] + 8 for k in (26, 27, 28, 29, 39))
        real = u(vp) + 8
        nblk = max(1, (real - vp) // 4) if real >= vp + 4 else 1
        blocks = [u(vp + 4 * k) + 8 for k in range(nblk)]
        v = np.frombuffer(x, '>f4', n * 7, blocks[0]).reshape(n, 7)
        argb = np.frombuffer(x, 'u1', n * 28, blocks[0]).reshape(n, 28)[:, 24:28]
        skin = np.array(w[3:3 + w[2]], np.int64) - 1 if older else palette
        raw = np.frombuffer(x, 'u1', n * slots * 8, wp).reshape(n, slots, 8)
        two = bool(w[41] & 0x80)
        idx = raw[:, :, 0].astype(np.int64) * 256 + raw[:, :, 1] if two else raw[:, :, 0].astype(np.int64)
        empty = idx == (65535 if two else 255)
        val = np.frombuffer(x, '>f4', n * slots * 2, wp).reshape(n, slots, 2)[:, :, 1].astype(np.float64)
        ok = ~empty & (idx < len(skin)) & (val > 0)
        bi = np.where(ok, skin[np.minimum(idx, len(skin) - 1)], 0)
        bw = np.where(ok, val, 0.0)
        uv = np.frombuffer(x, '>f4', n * 2, uvp).reshape(n, 2).astype(np.float64)
        # triangle strips (12-byte records up to the first index list)
        tris = []
        first = u(ip + 8) + 8
        for j in range(max(1, (first - ip) // 12)):
            q = ip + 12 * j
            cnt, ptr = u(q + 4), u(q + 8) + 8
            if not cnt or ptr + 2 * cnt > len(x):
                continue
            ix = np.frombuffer(x, '>u2', cnt, ptr).astype(np.int64)
            tris.append(_strip_tris(ix) if x[q] == 6 else ix[:len(ix) // 3 * 3].reshape(-1, 3))
        tris = np.concatenate(tris) if tris else np.zeros((0, 3), np.int64)
        # parameters: the textures
        prm = {}
        for k in range(w[37]):
            p = u(w[38] + 8 + 4 * k) + 8
            typ = struct.unpack_from('>H', x, p + 16)[0]
            if typ == 15:
                ti = struct.unpack_from('>i', x, p + 20)[0]
                if 0 <= ti < ntex:
                    prm[nm(p)] = tex[ti]
        shapes = []
        if len(blocks) - 1 == len(shape_names) and shape_names:
            for k, name in enumerate(shape_names):
                d = np.frombuffer(x, '>f4', n * 7, blocks[k + 1]).reshape(n, 7)[:, :3].astype(np.float64)
                moved = np.nonzero(np.abs(d).max(1) > 1e-5)[0]
                shapes.append({'name': name, 'index': moved, 'delta': d[moved], 'word1': np.zeros(len(moved), np.uint32)})
        meshes.append(dict(n=n, group=groups[w[24]] if w[24] < len(groups) else 'M_Object', shader=nm(o + 124),
                           # (the packs store each normal pointing in: against the triangles on every axis, correlation
                           #  -1.00 for x, y and z on Cena's head / body, Cody's tights / shoes - reversed here; kept as
                           #  stored, the game lit them inside-out, 2026-11-10)
                           pos=v[:, :3].astype(np.float32), nrm=-v[:, 3:6].astype(np.float32),
                           rgba=np.ascontiguousarray(argb[:, [1, 2, 3, 0]]), uv=uv, bone=bi, weight=bw, tris=tris,
                           textures=prm, shapes=shapes))
    return dict(bones=bones, meshes=meshes, groups=groups)


# ---------------------------------------------------------------- all models of a pack -> one 2K-style model

def _title(stem):
    return 'M_' + '_'.join(p[:1].upper() + p[1:] for p in stem.split('_') if p)


def stem_of(me):
    """the texture name a merged mesh goes by: the body is 'body' (its file is all_color - under that name it missed
    the port's body cell and skin settings, and took the gear shine: an oily look in the game, 2026-11-10), the rest
    their own colour map's name"""
    if me['name'] == 'M_Body':
        return 'body'
    return _stem(me['diffuse']) if me['diffuse'] else ''


def _stem(tex):
    return tex[:-len('_color')] if tex.endswith('_color') else tex


def _parent_helpers(bones, pieces):
    """2K22's muscle helper bones (m000_0000 ...) sit at the top of the skeleton with no parent, yet carry skin weight:
    the port folds a bone it does not know into its parent, and with none their weight was dropped - skin flaps at the
    shins of Cena / Kayla Braxton once the legs took SvR's stance. Each one gets the real bone (J_ / H_) it shares its
    vertices with most - from the model's own weights; its world place is kept (only 'local' is worked out again).
    One that shares no vertex with a real bone takes the real bone nearest to it."""
    real = np.array([b['name'].startswith(('J_', 'H_')) for b in bones])
    helper = [i for i, b in enumerate(bones) if b['parent'] < 0 and not real[i] and b['name'] != 'vector']
    if not helper:
        return
    share = np.zeros((len(bones), len(bones)))
    for p in pieces:
        bi, bw = p['bone'], p['weight']
        for a in range(bi.shape[1]):
            for c in range(bi.shape[1]):
                if a != c:
                    np.add.at(share, (bi[:, a], bi[:, c]), np.minimum(bw[:, a], bw[:, c]))
    pos = np.array([b['world'][3, :3] for b in bones])
    for i in helper:
        row = np.where(real, share[i], 0.0)
        if row.max() > 0:
            k = int(row.argmax())
        else:
            d = np.where(real, np.linalg.norm(pos - pos[i], axis=1), np.inf)
            k = int(d.argmin())
        bones[i]['parent'] = k
        bones[i]['local'] = bones[i]['world'] @ np.linalg.inv(bones[k]['world'])


def combine(models):
    """-> dict(bones=[...mcd_parse style], meshes=[...], textures_used={stem: (colour, normal)})"""
    import mcd_parse
    main = max((m for m in models if any(me['group'] == 'M_Head' for me in m['meshes'])),
               key=lambda m: len(m['meshes']), default=models[0])
    order = [main] + [m for m in models if m is not main]
    bones, by_name = [], {}
    maps = []
    for m in order:
        mp = []
        for b in m['bones']:
            # (older hair models hang on 'Bip Head' - it is the head)
            same = 'J_Head' if b['name'] == 'Bip Head' and 'J_Head' in by_name else b['name']
            if same in by_name:
                mp.append(by_name[same])
                continue
            par = m['bones'][b['parent']]['name'] if 0 <= b['parent'] < len(m['bones']) else None
            pk = by_name.get(par, -1) if par else -1
            by_name[b['name']] = len(bones)
            mp.append(len(bones))
            bones.append({'name': b['name'], 'parent': pk, 't': b['t'], 'euler': b['euler'], 'flag': 0, 'jiggle': -1,
                          'jiggle_data': None})
        maps.append(np.array(mp, np.int64))
    # the finger / toe end bones 2K23+ has (J_IndexF3_L_stub ...): the pipeline aims the last joints at them. 2K23's sit
    # 1.1 x the last joint's own length further along its X, unrotated - made the same way (no vertex uses them)
    for i in range(len(bones)):
        n = bones[i]['name']
        if re.fullmatch(r'J_\w+F3_[LR]|J_Toe_[LR]|H_Toe_[LR]', n) and n + '_stub' not in by_name:
            ln = float(np.linalg.norm(bones[i]['t'])) or 2.5
            by_name[n + '_stub'] = len(bones)
            bones.append({'name': n + '_stub', 'parent': i, 't': (1.1 * ln, 0.0, 0.0), 'euler': (0.0, 0.0, 0.0), 'flag': 0,
                          'jiggle': -1, 'jiggle_data': None})
    for bn in bones:
        M = np.eye(4)
        M[:3, :3] = mcd_parse.euler_matrix(bn['euler'])
        M[3, :3] = bn['t']
        bn['local'] = M
        bn['world'] = M if bn['parent'] < 0 else M @ bones[bn['parent']]['world']
    # name every piece, then put pieces of one name and one texture together
    pieces = []
    for m, mp in zip(order, maps):
        for me in m['meshes']:
            diffuse = me['textures'].get('g_mDiffusePrm') or me['textures'].get('g_mEyeOvlPrm') or ''
            name = me['group']
            if name == 'M_Body' and diffuse and diffuse != 'all_color' and not me['shader'].startswith('yCS_skin'):
                name = _title(_stem(diffuse))           # (the clothes model calls every piece M_Body)
            if name.startswith('M_Object'):
                name = _title(_stem(diffuse)) if diffuse else name
            pieces.append(dict(me, name=name, diffuse=diffuse, bone=mp[me['bone']]))
    _parent_helpers(bones, pieces)
    merged = {}
    for p in pieces:
        merged.setdefault((p['name'], p['diffuse']), []).append(p)
    meshes, seen = [], {}
    for (name, diffuse), ps in merged.items():
        k = seen.get(name, 0)
        seen[name] = k + 1
        # (a second piece on one name with its own texture is named by it: Hogan's beard in M_Hair -> M_Beard)
        alt = _title(_stem(diffuse)) if diffuse else ''
        final = name if k == 0 else alt if alt and alt not in seen else '%s_%d' % (name, k + 1)
        seen.setdefault(final, 1)
        off = np.cumsum([0] + [p['n'] for p in ps])
        slots = max(p['bone'].shape[1] for p in ps)
        pad = lambda a, fill: np.pad(a, ((0, 0), (0, slots - a.shape[1])), constant_values=fill)
        meshes.append(dict(
            name=final, diffuse=diffuse, normal=ps[0]['textures'].get('g_mTNmlMapPrm', ''), shader=ps[0]['shader'],
            pos=np.concatenate([p['pos'] for p in ps]), nrm=np.concatenate([p['nrm'] for p in ps]),
            rgba=np.concatenate([p['rgba'] for p in ps]), uv=np.concatenate([p['uv'] for p in ps]),
            bone=np.concatenate([pad(p['bone'], 0) for p in ps]), weight=np.concatenate([pad(p['weight'], 0.0) for p in ps]),
            tris=np.concatenate([p['tris'] + off[j] for j, p in enumerate(ps)]),
            shapes=ps[0]['shapes'] if len(ps) == 1 else []))
    return dict(bones=bones, meshes=meshes)


def fill(m, path, raw):
    """mcd_parse.Mcd for a PortIT pointer file ("PJBY" + json): the base (body, face) or attire part of a pack"""
    info = json.loads(raw[4:].decode('utf-8'))
    whole = _model(os.path.join(os.path.dirname(path), info['model']))
    want_base = info['part'] == 'base'
    m.version, m.game, m.mdl = 0, '2K22 (Yuke\'s)', -1
    m.strings, m.chunks, m.gtpt, m.trailer = [], {}, [], b''
    m.bones = whole['bones']
    m.bone_f0 = m.bone_f1 = 0.0
    m.meshes, m.lods, m.objects = [], [[]], []
    for me in whole['meshes']:
        if (re.sub(r'_\d+$', '', me['name']) in BASE_NAMES) != want_base:     # (M_EYE_2: a second eye texture)
            continue
        n = len(me['pos'])
        w = me['weight']
        s = w.sum(1, keepdims=True)
        w = np.where(s > 0, w / np.where(s > 0, s, 1), 0.0)
        k = len(m.meshes)
        stem = stem_of(me) or me['name'][2:].lower()
        # (stored the way the 2K .mcd stores V already - flipping it turned Hogan's nWo shirt upside down)
        uv = me['uv'].astype(np.float32)
        m.meshes.append({'material': me['name'], 'bbox': tuple(me['pos'].min(0)) + tuple(me['pos'].max(0)),
                         'vertices': n, 'object': stem, 'shader': me['shader'],
                         # (no COLOR stream: the packs' vertex colours are plain white, and 2K23+ reads COLOR as
                         #  the hair's colour - Cena's brown hair came out white)
                         'streams': [('POSITION', ('R32_G32_B32', 'float'), me['pos']),
                                     ('NORMAL', ('R32_G32_B32', 'float'), me['nrm']),
                                     ('TEXCOORD', ('R32_G32', 'float'), uv)],
                         'skin_bones': me['bone'].astype(np.uint16), 'skin_weights': w,
                         'remap': np.arange(n, dtype=np.uint32), 'shapes': me['shapes'], 'stats': b''})
        # (wound like the .mcd's stored order: the (b, a, c) swap mcd_parse makes. Proven in the game: without it SvR
        #  drew the inside of Cena's / Cody's heads, 2026-11-10)
        m.lods[0].append({'mesh': k, 'triangles': me['tris'][:, [1, 0, 2]], 'subsets': [(k, 0, 3 * len(me['tris']), 0)]})
        m.objects.append((stem + ':skinned', 0))
    m.bbox = tuple(np.min([b[:3] for b in (mb['bbox'] for mb in m.meshes)], 0)) + \
        tuple(np.max([b[3:] for b in (mb['bbox'] for mb in m.meshes)], 0)) if m.meshes else (0,) * 6


_MODELS = {}


def _model(path):
    if path not in _MODELS:
        blobs = []
        with open(path, 'rb') as f:
            data = f.read()
        q = 0
        while q < len(data):
            L = struct.unpack_from('<Q', data, q)[0]
            blobs.append(data[q + 8:q + 8 + L])
            q += 8 + L
        _MODELS.clear()
        _MODELS[path] = combine([read_jboy(b) for b in blobs])
    return _MODELS[path]


# ---------------------------------------------------------------- a pack -> a 2K-style wrestler folder (cached)

def is_character_pack(name):
    """chNNNN.pac (or any pack name that is not a txNNNN texture pack) - a texture pack is no wrestler"""
    return not re.match(r'(?i)tx\d', os.path.basename(name))


def texture_packs(pac):
    """txNNNN*.pac beside chNNNN*.pac (the same number)"""
    m = re.match(r'(?i)ch(\d+)', os.path.basename(pac))
    if not m:
        return []
    d = os.path.dirname(pac)
    return sorted(os.path.join(d, f) for f in os.listdir(d)
                  if re.match(r'(?i)tx%s(\D|$)' % m.group(1), f) and f.lower().endswith('.pac'))


def _key(pac):
    st = os.stat(pac)
    return hashlib.sha1(('%s|%d|%d' % (os.path.abspath(pac).lower(), st.st_size, int(st.st_mtime))).encode()).hexdigest()[:12]


def cached(pac):
    """the unpacked wrestler folder of a pack if it is already in the cache, else None"""
    try:
        root = os.path.join(CACHE, _key(pac))
    except OSError:
        return None
    if not os.path.exists(os.path.join(root, '.done')):
        return None
    d = [os.path.join(root, x) for x in os.listdir(root) if os.path.isdir(os.path.join(root, x))]
    return d[0] if d else None


def extract(pac, name=None, log=print):
    """chNNNNN.pac -> <cache>/<key>/<name>/ {BaseModel/baseModel.mdl, Attire/Attire.mdl, Textures} (PortIT pointer
    files the model reader opens; the textures named <stem>_color.dds / <stem>_nrm.dds) -> that folder"""
    key = _key(pac)
    name = re.sub(r'[^A-Za-z0-9_ .()-]+', '_', name or os.path.splitext(os.path.basename(pac))[0]).strip() or 'wrestler'
    root = os.path.join(CACHE, key)
    char = os.path.join(root, name)
    done = os.path.join(root, '.done')
    if os.path.exists(done) and os.path.isdir(char):
        return char
    log('unpacking %s (WWE 2K19 - 2K22 pack)...' % os.path.basename(pac))
    blobs = members(pac)
    for tx in texture_packs(pac):          # (2K19 / 2K20: the textures can sit in txNNNN.pac beside chNNNN.pac)
        log('  + textures from %s' % os.path.basename(tx))
        blobs += members(tx)
    models = [b for b in blobs if b[:4] in (b'JBOY', b'YOBJ')]
    tex = {}
    for b in blobs:
        t = textures_of(b)
        if t:
            tex.update(t)
    if not models:
        raise RuntimeError('%s: no model inside (not a WWE 2K19 - 2K22 character pack?)' % os.path.basename(pac))
    os.makedirs(root, exist_ok=True)
    model_file = os.path.join(root, 'models.bin')
    with open(model_file, 'wb') as f:
        for b in models:
            f.write(struct.pack('<Q', len(b)))
            f.write(b)
    whole = _model(model_file)
    for part, folder, fname in (('base', 'BaseModel', 'baseModel.mdl'), ('attire', 'Attire', 'Attire.mdl')):
        d = os.path.join(char, folder)
        os.makedirs(os.path.join(d, 'Textures'), exist_ok=True)
        with open(os.path.join(d, fname), 'wb') as f:
            f.write(b'PJBY' + json.dumps({'model': os.path.relpath(model_file, d), 'part': part,
                                          'pac': os.path.abspath(pac)}).encode('utf-8'))
        for me in whole['meshes']:
            stem = stem_of(me)
            for src, role in ((me['diffuse'], 'color'), (me['normal'], 'nrm')):
                if stem and src and src.lower() in tex:
                    out = os.path.join(d, 'Textures', '%s_%s.dds' % (stem, role))
                    if not os.path.exists(out):
                        with open(out, 'wb') as f:
                            f.write(tex[src.lower()])
    open(done, 'w').write(pac)
    return char


def looks_female(model_path):
    """a woman's model by 2K's own data: the women's 12699-vertex body - see mcd_parse.looks_female"""
    try:
        raw = open(model_path, 'rb').read()
        info = json.loads(raw[4:].decode('utf-8'))
        whole = _model(os.path.join(os.path.dirname(model_path), info['model']))
    except (OSError, ValueError, KeyError, struct.error):
        return False
    body = next((me for me in whole['meshes'] if me['name'] == 'M_Body'), None)
    return body is not None and len(body['pos']) == 12699
