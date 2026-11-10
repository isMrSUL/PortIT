"""PortIT: the full-detail 2K26 route and the fixes found porting Bron Breakker (2026-11-07, test copy, port 2.1.9).

Used by port_2k.build. Two parts:

1. For every 2K26 port (any memory budget) - model.json after rock_model.build, before the builder:
   * hair / beard colour: 2K26 stores it linear (Bron's beard 5,3,2) -> sRGB x HAIR_LIFT; the card colour map has
     an on / off cut-out (rai green > STRAND_CUT) and a per-strand shade (rai red / blue). Soft strands + DXT + the
     mips let the game's cut-out fill whole cards (flat dark cards, jagged card edges in game).
   * close-cropped card meshes (every vertex within FLAT_CARD_CM of the skin: a buzz cut, stubble) are left out -
     the 2K head texture has them painted (Bron's scalp cards drew as big flakes).
   * face cards (beards, below the eyes): every vertex takes the weights of the nearest skin vertex on its card's
     side of the mouth (the jaw / lower lip, or the rest - chosen by the card's root), each face bone's weight capped
     so weight x distance stays within the stock head's largest (no tip swings further than stock skin), and cards
     that cross the lip line inside the mouth's width are left out (a moustache tip in the open mouth).
     Scalp cards: the nearest head skin vertex's weights, the same cap.
   * tight gear moves with the skin under it (GEAR_SKIN): the gear's own weights (2K's folded onto fewer bones)
     drifted from the skin - holes at the straps, gaps at the wrist tape.
   * body skin under gear: only removed KEEP_EDGE_CM in from the gear's edges (wraps rock_fbx.covered).
2. The full route (budget >= 1500 pages, game 2.1.6+): no decimation, the cards in the hair model, the texture sheet
   drawn at 8192 x 4096 (head and body at their full 2K size, the rest packed at theirs, halved until they fit), the
   ch.pac carrying it at the stock sizes (2048 x 1024 / 1024 x 1024: game memory as a stock superstar) and a texture
   pack (full sheets as DXT5 DDS named after the pac textures' XXH3 content hash, k26\\ddshash.exe) that the port
   swaps in on the GPU (port native/texture_packs.h; up to 8192). 4096 x 4096 inside the pac drew wrong in game.

checks(): facecheck (face-bone levers vs the stock ch125 head) and gearcheck (gear vs skin drift) - both 0 for Bron.
"""
import io
import json
import os
import re
import struct
import subprocess
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))

HAIR_LIFT = 0.75            # sRGB hair colour x this (Bron's beard 5,3,2 linear -> 29,21,16)
STRAND_CUT = 0.45           # rai green over this = strand
FLAT_CARD_CM = 2.5          # a card mesh entirely this close to the skin is painted on the head texture: left out
KEEP_EDGE_CM = 3.0          # body skin under gear kept this far in from the gear's edges
GEAR_SKIN = (0.15, 0.35)    # model units (10 cm): gear this near the skin moves with it (full / fading out)
GEAR_HEAD = [False]         # gear near the head takes the fitted head's weights too (AEW ports: port_2k sets it - a
                            # mask kept AEW's face weights, partly folded away; Penta's caught on his jaw)
MOUTH_GAP = 0.03            # units: a face card reaching this far over the lip line inside the mouth goes
LOWER = ('d_kuchi', 'd_kuchi_00', 'd_kuchi_01', 'l_kuchi', 'r_kuchi')   # k26_face's lower lip / jaw
BODY_BONES = {'atama', 'kubi', 'kubi_x', 'mune', 'root', 'koshi', 'l_eye', 'r_eye'}
SHEET = (8192, 4096)
PAC_COLOR, PAC_NORMAL = (2048, 1024), (1024, 1024)
MOUTH_BLOCK = (256, 128)
QUICK_READ = [False]        # set by port_2k.build around setup()
MAX_MESH_TRIS = 30000      # a mesh past this is split: the game counts a mesh's indices in 16 bits (<= 65535; the
                           #  strips take 0.9-1.7 per triangle; port_2k halves this and builds again if pac_limits still finds one) - Rey Mysterio's M_Rey, 70,336 triangles -> 118,645
                           #  indices, crashed the draw sub_826E6AD0 (lr 826E6B90) on the select screen, 2026-11-07
SAFE_INDICES = 24000       # indices in one mesh kept within what has run in game (Bron's hair 20,097; Kevin 23,544)
STOCK_MESHES = 33          # meshes in one part: the stock maximum (ch139)
SPLIT = {}                 # mesh name -> triangles a piece (set by port_2k from the built file)
PART_PAGES = 1900          # one model part (body 0x0, hair 0x2710 ...) over 2048 pages crashes the draw
BEND = 0.52                 # radians (30 deg): gearcheck's strong bend    # the template's mouth inside (rock_model TMPL_MOUTH_SRC size)


# ------------------------------------------------------------------------------------------------ set-up hooks
def install(port_2k, log=print):
    """patch rock_fbx / brock_model / port_2k for every 2K26 port (idempotent)"""
    import brock_model as BM
    import rock_fbx
    if getattr(rock_fbx, '_portit_full', False):
        return
    rock_fbx._portit_full = True
    # (PortIT's work PNGs - the 8192 x 4096 sheets, the cut textures - saved at fast zlib level 1: the same pixels,
    #  ~7 s less a port; the default level 6 spent that compressing files only PortIT reads back)
    from PIL import Image as _Im
    if not getattr(_Im.Image.save, '_portit_fast', False):
        plain_save = _Im.Image.save

        def fast_save(self, fp, format=None, **params):
            name = fp if isinstance(fp, str) else getattr(fp, 'name', '')
            if (format or '').upper() == 'PNG' or str(name).lower().endswith('.png'):
                params.setdefault('compress_level', 1)
            return plain_save(self, fp, format, **params)
        fast_save._portit_fast = True
        _Im.Image.save = fast_save
    lin_hair = port_2k.hair_rgb

    def srgb_hair(char, attire, mesh_name):
        c = np.array(lin_hair(char, attire, mesh_name), float) / 255
        s = np.where(c <= 0.0031308, 12.92 * c, 1.055 * c ** (1 / 2.4) - 0.055)
        return tuple(int(min(255, round(255 * x * HAIR_LIFT))) for x in s)
    port_2k.hair_rgb = srgb_hair

    def strand_colour(src, dst, rgb):
        from PIL import Image
        a = np.asarray(Image.open(src).convert('RGBA')).astype(np.float64) / 255
        g, r, b = a[..., 1], a[..., 0], a[..., 2]
        shade = 0.55 + 0.75 * r + 0.25 * b
        out = np.zeros(a.shape, np.uint8)
        out[..., :3] = np.clip(np.array(rgb)[None, None] * shade[..., None], 0, 255)
        out[..., 3] = np.where(g > STRAND_CUT, 255, 0)
        Image.fromarray(out, 'RGBA').save(dst)
    BM.hair_colour = strand_colour
    plain = rock_fbx.covered

    def covered_inner(verts, normals, att, out_cm=6.0, in_cm=1.5, cell=8.0):
        if QUICK_READ[0]:       # (port_2k.setup's first read: names / sizes / textures only - the test is slow)
            return np.zeros(len(verts), bool)
        hid = plain(verts, normals, att, out_cm, in_cm, cell)
        P = np.asarray(verts, float)
        keep = hid.copy()
        idx = np.nonzero(hid)[0]
        open_p = P[~hid]
        for i0 in range(0, len(idx), 2048):
            q = P[idx[i0:i0 + 2048]]
            d = ((q[:, None] - open_p[None]) ** 2).sum(-1).min(1) if len(open_p) else np.full(len(q), 1e9)
            keep[idx[i0:i0 + 2048][d < KEEP_EDGE_CM ** 2]] = False
        return keep
    rock_fbx.covered = covered_inner


def flat_cards(meshes, card_names, log=print):
    """card meshes lying flat on the skin (every vertex within FLAT_CARD_CM, 2K units = cm) -> their names"""
    skin = [m['verts'] for m in meshes if m['name'] in ('M_Body', 'M_Head')]
    if not skin:
        return set()
    S = np.concatenate(skin)
    out = set()
    for m in meshes:
        if m['name'] not in card_names:
            continue
        P = np.asarray(m['verts'], float)
        far = 0.0
        for i0 in range(0, len(P), 1024):
            far = max(far, float(np.sqrt(((P[i0:i0 + 1024, None] - S[None]) ** 2).sum(-1).min(1)).max()))
        if far < FLAT_CARD_CM:
            out.add(m['name'])
            log('%s: hair cards lie flat on the skin (all within %.1f cm) - painted on the head texture, left out'
                % (m['name'], far))
    return out


# ------------------------------------------------------------------------------------------------ texture layout
def layout(R, char, attire_dir, skip=(), log=print):
    """the 8192 x 4096 sheet's cells: head / body at their full size, the rest packed at their source size (halved
    until all fit; `skip`: textures of left-out meshes); the template's mouth block at the top right's bottom"""
    from PIL import Image
    W_, H_ = SHEET
    cells = {'head': (0, 0, 2048, 2048), 'body': (2048, 0, 4096, 2048)}
    mx, my = 6144 + 2048 - MOUTH_BLOCK[0], 2048 - MOUTH_BLOCK[1]
    want = {}
    for t in R.CELLS:
        if t in cells or t == 'mouthbag' or t in skip:
            continue
        src = (R.find_tex(char, attire_dir, t, '_color') or R.find_tex(char, attire_dir, re.sub(r'(?i)_alpha$', '', t), '_rai')
               or R.find_tex(char, attire_dir, t, ''))
        try:
            w, h = Image.open(src).size if src else (256, 256)
        except Exception:   # noqa: BLE001
            w, h = 256, 256
        want[t] = (min(w, 4096), min(h, 2048))
    for shrink in range(6):
        free = [(6144, 0, 2048, my), (6144, my, 2048 - MOUTH_BLOCK[0], MOUTH_BLOCK[1]), (0, 2048, W_, H_ - 2048)]
        got, ok = {}, True
        for t, (w, h) in sorted(want.items(), key=lambda kv: -kv[1][0] * kv[1][1]):
            w, h = max(64, w >> shrink), max(64, h >> shrink)
            free.sort(key=lambda r: r[2] * r[3])
            k = next((i for i, (x, y, fw, fh) in enumerate(free) if fw >= w and fh >= h), None)
            if k is None:
                ok = False
                break
            x, y, fw, fh = free.pop(k)
            got[t] = (x, y, w, h)
            if fw - w > 0:
                free.append((x + w, y, fw - w, h))
            if fh - h > 0:
                free.append((x, y + h, fw, fh - h))
        if ok:
            cells.update(got)
            if shrink:
                log('texture sheet: the smaller parts at 1/%d of their 2K size (room)' % (1 << shrink))
            break
    else:
        raise RuntimeError('texture sheet: the parts do not fit')
    # (self-check: no two cells overlap, none on the mouth block, all on the sheet)
    boxes = dict(cells, _mouth=(mx, my) + MOUTH_BLOCK)
    names = list(boxes)
    for i, a in enumerate(names):
        x, y, w, h = boxes[a]
        if x < 0 or y < 0 or x + w > W_ or y + h > H_:
            raise RuntimeError('texture sheet: %s off the sheet %s' % (a, boxes[a]))
        for b in names[i + 1:]:
            X, Y, Wb, Hb = boxes[b]
            if x < X + Wb and X < x + w and y < Y + Hb and Y < y + h:
                raise RuntimeError('texture sheet: %s %s overlaps %s %s' % (a, boxes[a], b, boxes[b]))
    R.SHEET = SHEET
    R.CELLS = cells
    R.TMPL_MOUTH_CELL = (mx, my)
    R.CELLS['mouthbag'] = (mx + 128 + 16, my + 16, 96, 96)
    return cells


# ------------------------------------------------------------------------------------------------ weights
def _cards(P, T):
    parent = np.arange(len(P))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    for a, b, c in T:
        for x, y in ((a, b), (b, c)):
            rx, ry = find(x), find(y)
            if rx != ry:
                parent[rx] = ry
    return np.array([find(i) for i in range(len(P))])


def lever_limits(template, joints):
    import k26_face as FT
    tf = FT.template_face(template)
    face = set(tf['face_bones'])
    lim = {}
    for p, wl in zip(tf['V'], tf['W']):
        for b, w in wl:
            if b in face and b in tf['pos'] and w > 0.01:
                lim[b] = max(lim.get(b, 0.0), w * float(np.linalg.norm(p - tf['pos'][b])))
    iod = float(np.linalg.norm(np.array(joints['l_eye']) - np.array(joints['r_eye'])))
    return lim, iod / float(np.linalg.norm(tf['eye_l'] - tf['eye_r']))


def _nearest(P, Q):
    j = np.empty(len(P), int)
    d = np.empty(len(P))
    for i0 in range(0, len(P), 1024):
        dd = ((P[i0:i0 + 1024, None] - Q[None]) ** 2).sum(-1)
        j[i0:i0 + 1024] = dd.argmin(1)
        d[i0:i0 + 1024] = dd.min(1)
    return j, d


def card_weights(spec, card_names, template, log=print):
    """face cards (median below the eyes): nearest skin on the card's side of the mouth; scalp cards: nearest head
    skin; both with the lever cap"""
    ms = spec['attires'][0]['meshes']
    J = {k: np.array(v) for k, v in spec['joints'].items()}
    eye_y = (J['l_eye'][1] + J['r_eye'][1]) / 2
    skin = [m for m in ms if m['name'].startswith(('M_Head', 'M_Body'))]
    SP = np.concatenate([np.array(m['positions']) for m in skin])
    SW = [[(m['bones'][b], w) for b, w in ws] for m in skin for ws in m['weights']]
    is_head = np.concatenate([np.full(len(m['positions']), m['name'].startswith('M_Head')) for m in skin])
    lower = np.array([sum(w for bn, w in e if bn in LOWER) for e in SW]) >= 0.3
    lim, scale = lever_limits(template, spec['joints'])
    face_cards = set()
    for m in ms:
        if not any(m['name'] == n or m['name'].startswith(n + '_') for n in card_names):
            continue
        P = np.array(m['positions'])
        comp = _cards(P, np.array(m['triangles']))
        face = np.median(P[:, 1]) > eye_y            # (-Y up: below the eyes)
        nn, dist = _nearest(P, SP)
        side = {}
        if face:
            face_cards.add(m['name'])
            root = {}
            for i in range(len(P)):
                if comp[i] not in root or dist[i] < dist[root[comp[i]]]:
                    root[comp[i]] = i
            side = {r: bool(lower[nn[i]]) for r, i in root.items()}
            for s_ in (True, False):
                idx = np.nonzero(lower == s_)[0]
                vi = np.array([i for i in range(len(P)) if side[comp[i]] == s_], int)
                if len(vi) and len(idx):
                    j, _ = _nearest(P[vi], SP[idx])
                    nn[vi] = idx[j]
        else:
            idx = np.nonzero(is_head)[0]
            j, _ = _nearest(P, SP[idx])
            nn = idx[j]
        wl_all, capped = [], 0
        for v, i in enumerate(nn):
            wl = dict(SW[i])
            spare = 0.0
            for b in list(wl):
                if b not in lim or b == 'd_kuchi' or b not in J:
                    continue
                wmax = lim[b] * scale / max(float(np.linalg.norm(P[v] - J[b])), 1e-6)
                if wl[b] > wmax:
                    spare += wl[b] - wmax
                    wl[b] = wmax
                    capped += 1
            if spare:
                to = 'd_kuchi' if side.get(comp[v]) else 'atama'
                wl[to] = wl.get(to, 0.0) + spare
            top = sorted(wl.items(), key=lambda kv: -kv[1])[:4]
            tot = sum(w for _, w in top)
            wl_all.append([(b, w / tot) for b, w in top])
        bones = sorted({b for wl in wl_all for b, _ in wl})
        bi = {b: k for k, b in enumerate(bones)}
        m['bones'] = bones
        m['weights'] = [[[bi[b], round(w, 5)] for b, w in wl if w > 1e-5] for wl in wl_all]
        log('%s: %s cards (%d), on the skin under them%s; %d face-bone weights capped to the stock swing' % (
            m['name'], 'face' if face else 'scalp', len(set(comp.tolist())),
            (', %d on the jaw side' % sum(side.values())) if face else '', capped))
    return face_cards


def mouth_cards(spec, face_cards, log=print):
    import k26_face as FT
    ms = spec['attires'][0]['meshes']
    heads = [m for m in ms if m['name'].startswith('M_Head')]
    V, T, off = [], [], 0
    for m in heads:
        V.append(np.array(m['positions']))
        T.append(np.array(m['triangles']) + off)
        off += len(m['positions'])
    V, T = np.concatenate(V), np.concatenate(T)
    J = spec['joints']
    L, _ = FT.landmarks(V, T, np.array(J['l_eye']), np.array(J['r_eye']))
    my = L['mouth'][1]
    x0, x1 = sorted((L['mouth_l'][0], L['mouth_r'][0]))
    half, xm, zf = (x1 - x0) / 2, (x0 + x1) / 2, L['mouth'][2]
    for m in ms:
        if m['name'] not in face_cards:
            continue
        P = np.array(m['positions'])
        Tm = np.array(m['triangles'])
        jaw = np.array([sum(w for b, w in ws if m['bones'][b] in LOWER) for ws in m['weights']])
        comp = _cards(P, Tm)
        inside = (np.abs(P[:, 0] - xm) < half * 1.1) & (P[:, 2] < zf + 0.35)
        drop = set()
        for r in set(comp.tolist()):
            vi = np.nonzero(comp == r)[0]
            if jaw[vi].mean() > 0.2:
                hit = inside[vi] & (P[vi, 1] < my - MOUTH_GAP)
            else:
                hit = inside[vi] & (P[vi, 1] > my + MOUTH_GAP)
            if hit.any():
                drop.add(r)
        if not drop:
            continue
        keep_t = np.array([comp[t[0]] not in drop for t in Tm])
        used = np.unique(Tm[keep_t])
        remap = -np.ones(len(P), int)
        remap[used] = np.arange(len(used))
        m['triangles'] = remap[Tm[keep_t]].tolist()
        for k in ('positions', 'normals', 'uvs', 'weights'):
            m[k] = [m[k][i] for i in used]
        log('%s: %d of %d cards crossed the lip line in the mouth - left out' % (m['name'], len(drop), len(set(comp.tolist()))))


def gear_weights(spec, skip, log=print):
    ms = spec['attires'][0]['meshes']
    body = [m for m in ms if m['name'].startswith(('M_Body', 'M_Head') if GEAR_HEAD[0] else 'M_Body')]
    BP = np.concatenate([np.array(m['positions']) for m in body])
    BW = [{m['bones'][b]: w for b, w in ws} for m in body for ws in m['weights']]
    lo, hi = GEAR_SKIN
    for m in ms:
        if m['name'].startswith(('M_Body', 'M_Head', 'M_EYE', 'M_TransB', 'M_MouthBag', 'mouth_', 'M_AEWMouth')) or m['name'] in skip \
                or m.get('kind') in ('eye', 'lash', 'hair'):
            continue
        P = np.array(m['positions'])
        j, d2 = _nearest(P, BP)
        if GEAR_HEAD[0] and m['name'].startswith('M_InnerMouth'):
            # (AEW's inner lips: each point on its own lip's skin - upper with upper, lower (on the jaw) with lower;
            #  at the corners the nearest skin was often the other lip's: steps in the open mouth's corners)
            own_lo = np.array([sum(w for b, w in ws if m['bones'][b] == 'd_kuchi') > 0.2 for ws in m['weights']])
            sk_lo = np.array([e.get('d_kuchi', 0.0) > 0.2 for e in BW])
            for side in (False, True):
                sel = np.nonzero(own_lo == side)[0]
                cand = np.nonzero(sk_lo == side)[0]
                if len(sel) and len(cand):
                    jj, dd2 = _nearest(P[sel], BP[cand])
                    j[sel], d2[sel] = cand[jj], dd2
        elif GEAR_HEAD[0]:
            # (AEW gear on the face - a mask: a point above the lower lip's top edge (2011: -Y up) follows skin off the
            #  jaw only; Penta's mask tip over his upper lip took the lower lip as its nearest skin and dropped with
            #  the jaw, 2026-11-09)
            jawb = ('d_kuchi', 'd_kuchi_00', 'd_kuchi_01', 'l_kuchi', 'r_kuchi')
            sk_j = np.array([sum(e.get(b, 0.0) for b in jawb) > 0.2 for e in BW])
            hit = np.nonzero(sk_j[j])[0]
            if len(hit):
                LJ = BP[sk_j]
                up = []
                for v in hit:
                    near = LJ[np.abs(LJ[:, 0] - P[v, 0]) < 0.05]
                    if len(near) and P[v, 1] < near[:, 1].min():
                        up.append(v)
                cand = np.nonzero(~sk_j)[0]
                if up and len(cand):
                    up = np.array(up)
                    jj, dd2 = _nearest(P[up], BP[cand])
                    j[up], d2[up] = cand[jj], dd2
                    log('%s: %d points above the lower lip kept off the jaw' % (m['name'], len(up)))
        dd = np.sqrt(d2)
        out, moved = [], 0
        for v in range(len(P)):
            own = {m['bones'][b]: w for b, w in m['weights'][v]}
            t = float(np.clip((hi - dd[v]) / (hi - lo), 0, 1))
            if t <= 0:
                out.append(own)
                continue
            moved += 1
            mix = {}
            for b, w in own.items():
                mix[b] = mix.get(b, 0.0) + (1 - t) * w
            for b, w in BW[j[v]].items():
                mix[b] = mix.get(b, 0.0) + t * w
            top = sorted(mix.items(), key=lambda kv: -kv[1])[:4]
            tot = sum(w for _, w in top)
            out.append({b: w / tot for b, w in top})
        bones = sorted({b for e in out for b in e})
        bi = {b: k for k, b in enumerate(bones)}
        m['bones'] = bones
        m['weights'] = [[[bi[b], round(w, 5)] for b, w in sorted(e.items(), key=lambda kv: -kv[1]) if w > 1e-5]
                        for e in out]
        if moved:
            log('%s: %d of %d vertices move with the skin under them' % (m['name'], moved, len(P)))


def split_big(spec, log=print):
    """meshes over MAX_MESH_TRIS triangles -> pieces of nearby triangles (sorted along the mesh's longest axis), each
    with its own vertices: the same look, every piece under the game's 16-bit index count"""
    out = []
    for m in spec['attires'][0]['meshes']:
        T = np.array(m['triangles'])
        most = min(MAX_MESH_TRIS, SPLIT.get(m['name'], MAX_MESH_TRIS))
        if len(T) <= most:
            out.append(m)
            continue
        P = np.array(m['positions'])
        cen = P[T].mean(1)
        axis = int(np.argmax(cen.max(0) - cen.min(0)))
        order = np.argsort(cen[:, axis], kind='stable')
        n = -(-len(T) // most)
        for k, part in enumerate(np.array_split(order, n)):
            tri = T[np.sort(part)]
            used = np.unique(tri)
            remap = -np.ones(len(P), int)
            remap[used] = np.arange(len(used))
            piece = {key: val for key, val in m.items() if key not in ('positions', 'normals', 'uvs', 'weights', 'triangles')}
            piece['name'] = '%s_s%d' % (m['name'], k)
            for key in ('positions', 'normals', 'uvs', 'weights'):
                piece[key] = [m[key][i] for i in used]
            piece['triangles'] = remap[tri].tolist()
            out.append(piece)
        log('%s: %d triangles - split into %d meshes (the game: 16-bit index counts)' % (m['name'], len(T), n))
    spec['attires'][0]['meshes'] = out


def inner_bridges(spec, log=print, spread=0.3):
    """AEW's inner lips: triangles joining a point on the jaw to one off it (jaw-branch weights apart by more than
    spread) left out - they stretched across the open mouth as a curtain (Penta, 2026-11-09)"""
    jawb = ('d_kuchi', 'd_kuchi_00', 'd_kuchi_01', 'l_kuchi', 'r_kuchi')
    for m in spec['attires'][0]['meshes']:
        if not m['name'].startswith('M_InnerMouth'):
            continue
        w = np.array([sum(x for b, x in ws if m['bones'][b] in jawb) for ws in m['weights']])
        T = np.array(m['triangles'])
        keep = (w[T].max(1) - w[T].min(1)) <= spread
        if not keep.all():
            m['triangles'] = T[keep].tolist()
            log('%s: %d triangles across the lips left out' % (m['name'], int((~keep).sum())))


def fix_model(path, card_names, template, log=print):
    """the weight fixes on a rock_model model.json (in place), then oversized meshes split"""
    spec = json.load(open(path))
    face = card_weights(spec, card_names, template, log)
    if face:
        mouth_cards(spec, face, log)
    gear_weights(spec, set(card_names), log)
    if GEAR_HEAD[0]:
        inner_bridges(spec, log)
    split_big(spec, log)
    with open(path, 'w') as f:      # (one write: json.dump streams thousands of small ones - seconds a port)
        f.write(json.dumps(spec))


def pac_limits(pac, window_pages=0):
    """the built ch.pac against the game's hard limits (each one a crash in game) -> list of problems (empty: ok):
    per mesh vertices / indices <= 65535; per part <= PART_PAGES pages; the whole model within its vertex window"""
    import ch_tool
    import jboy
    import svrfmt
    probs = []
    _, groups = ch_tool.epk8_read(open(pac, 'rb').read())
    for typ, ents in groups:
        if typ != b'EMD ':
            continue
        for name, blob in ents:
            nm = name.decode('latin1').strip('\0 ')
            if not nm.endswith('2'):
                continue
            total = 0
            for i, x in svrfmt.pach_read(ch_tool.unpack(blob)):
                raw = ch_tool.unpack(x)
                if raw[:4] != b'JBOY':
                    continue
                pages = 0
                for k, s in enumerate(jboy.read(raw).meshes):
                    ni = sum(len(st[1]) for st in s.strips)
                    if len(s.verts) > 65535 or ni > 65535:
                        probs.append('%s part %s mesh %d: %d vertices, %d indices (the game takes 65535)' % (nm, hex(i), k, len(s.verts), ni))
                    stride = 32 if max(1, s.u5c[0]) == 1 else 44
                    pages += -(-len(s.verts) * stride // 4096) + -(-ni * 2 // 4096)
                if pages > PART_PAGES:
                    probs.append('%s part %s: %d pages (one part over 2048 crashes the draw)' % (nm, hex(i), pages))
                total += pages
            if window_pages and total > window_pages:
                probs.append('%s: %d pages, the vertex window takes %d' % (nm, total, window_pages))
    return probs


def heavy_meshes(pac):
    """(vertices, indices) of the built meshes over SAFE_INDICES, and the largest mesh count of a part"""
    import ch_tool
    import jboy
    import svrfmt
    out, most = [], 0
    _, groups = ch_tool.epk8_read(open(pac, 'rb').read())
    for typ, ents in groups:
        if typ != b'EMD ':
            continue
        for name, blob in ents:
            if not name.decode('latin1').strip('\0 ').endswith('2'):
                continue
            for i, x in svrfmt.pach_read(ch_tool.unpack(blob)):
                raw = ch_tool.unpack(x)
                if raw[:4] != b'JBOY':
                    continue
                ms = jboy.read(raw).meshes
                most = max(most, len(ms))
                for s in ms:
                    ni = sum(len(st[1]) for st in s.strips)
                    if ni > SAFE_INDICES:
                        out.append((len(s.verts), ni))
    return out, most


# ------------------------------------------------------------------------------------------------ checks
def checks(path, template):
    """-> (face_over, gear_over, notes): vertices swinging past the stock head's levers; gear drifting from the skin
    more than the body's own neighbours (99%)"""
    import k26_face as FT
    spec = json.load(open(path))
    ms = spec['attires'][0]['meshes']
    J = {k: np.array(v) for k, v in spec['joints'].items()}
    lim, scale = lever_limits(template, spec['joints'])
    notes, face_over = [], 0
    for m in ms:
        P = np.array(m['positions'])
        bad, worst = 0, 0.0
        for v, ws in enumerate(m['weights']):
            if len(ws) > 4:
                notes.append('%s: a vertex with %d weights' % (m['name'], len(ws)))
            for bi, w in ws:
                b = m['bones'][bi]
                if b in BODY_BONES or w <= 0.01 or b not in lim or b not in J:
                    continue
                r = w * float(np.linalg.norm(P[v] - J[b])) / (lim[b] * scale)
                if r > 1.25:
                    bad += 1
                    worst = max(worst, r)
        if bad:
            notes.append('%s: %d face-bone weights swing past the stock head (worst %.1fx)' % (m['name'], bad, worst))
        face_over += bad
    body = [m for m in ms if m['name'].startswith(('M_Body', 'M_Head') if GEAR_HEAD[0] else 'M_Body')]   # (as gear_weights)
    BP = np.concatenate([np.array(m['positions']) for m in body])
    BW = [{m['bones'][b]: w for b, w in ws} for m in body for ws in m['weights']]

    def drift(p, wa, wb):
        return sum(abs(wa.get(b, 0.0) - wb.get(b, 0.0)) * float(np.linalg.norm(p - J[b]))
                   for b in set(wa) | set(wb) if b in J)
    rng = np.random.default_rng(1)
    own = []
    for i in rng.choice(len(BP), min(1500, len(BP)), replace=False):
        d = np.linalg.norm(BP - BP[i], axis=1)
        c = np.nonzero((d > GEAR_SKIN[1] * 0.3) & (d < GEAR_SKIN[1] * 0.6))[0]
        if len(c):
            own.append(drift(BP[i], BW[i], BW[c[0]]))
    limit = float(np.percentile(own, 99)) if own else 1.0
    gear_over = 0
    for m in ms:
        if m['name'].startswith(('M_Body', 'M_Head', 'M_EYE', 'M_TransB', 'M_MouthBag', 'mouth_', 'M_AEWMouth')) or \
                m.get('kind') in ('eye', 'lash', 'hair'):
            continue
        P = np.array(m['positions'])
        W = [{m['bones'][b]: w for b, w in ws} for ws in m['weights']]
        j, d2 = _nearest(P, BP)
        # (a gap or poke-through shows when the drift at a strong bend (30 deg) passes the space between the gear and
        #  the skin; tight gear has ~0 space, so the test is as strict as the body's own neighbours there, a loose
        #  jacket 3 cm off the skin may differ more - Harley Race's entrance jacket, 2026-11-07)
        bad = 0
        for v in range(len(P)):
            gap = float(np.sqrt(d2[v]))
            if gap <= GEAR_SKIN[1]:
                dr = drift(P[v], W[v], BW[j[v]])
                if dr > limit and dr * BEND > gap + 0.02:
                    bad += 1
        if bad:
            notes.append('%s: %d vertices drift from the skin under them' % (m['name'], bad))
        gear_over += bad
    return face_over, gear_over, notes


# ------------------------------------------------------------------------------------------------ texture pack
def build_pac(model_json, pac, full):
    """rock_build on model.json -> (ok, report dict); the full route's pac sheets at the stock sizes"""
    run = ['import sys', 'sys.path.insert(0, %r)' % HERE,
           'sys.argv = %r' % (['rock_build.py', model_json, pac] + (['--no-limit'] if full else []),),
           'import rock_build as RB']
    if full:
        run += ['RB.B.COLOR_SIZE = %r' % (PAC_COLOR,), 'RB.B.NORMAL_SIZE = %r' % (PAC_NORMAL,),
                # (the bump sheet must not get the colour's alpha: rock_build tells the sheets apart by size)
                'o = RB.OneCell.sheet',
                'def sheet(self, images, size, fill):',
                '    if tuple(fill) != (128, 128, 255, 255):',
                '        return o(self, images, size, fill)',
                '    keep = RB._LASH.get("png")',
                '    RB._LASH["png"] = None',
                '    try:',
                '        return o(self, images, size, fill)',
                '    finally:',
                '        RB._LASH["png"] = keep',
                'RB.OneCell.sheet = sheet']
    run += ['sys.exit(RB.main(sys.argv))']
    r = subprocess.run([sys.executable, '-I', '-c', '\n'.join(run)], capture_output=True, text=True)
    try:
        return json.loads(r.stdout)
    except ValueError:
        raise RuntimeError('builder failed: ' + (r.stderr or r.stdout)[-1500:])


def _dds_levels(im, cells):
    from PIL import Image
    import w13_char as W
    w, h = im.size
    mips = int(np.log2(max(w, h))) + 1
    out = None
    for k in range(mips):
        lv = im if k == 0 else W.resize_rgba(im, (max(1, w >> k), max(1, h >> k)))
        if cells and k:
            a = np.array(lv)
            for x, y, cw, ch in cells:
                x0, y0 = x >> k, y >> k
                x1, y1 = max((x + cw) >> k, x0 + 1), max((y + ch) >> k, y0 + 1)
                a[y0:y1, x0:x1, 3] = np.where(a[y0:y1, x0:x1, 3] >= 96, 255, 0)
            lv = Image.fromarray(a, 'RGBA')
        b = io.BytesIO()
        lv.save(b, 'DDS', pixel_format='DXT5')
        v = b.getvalue()
        out = bytearray(v) if out is None else out + v[128:]
    struct.pack_into('<I', out, 8, struct.unpack_from('<I', out, 8)[0] | 0x20000)
    struct.pack_into('<I', out, 28, mips)
    struct.pack_into('<I', out, 20, max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * 16)
    struct.pack_into('<I', out, 108, struct.unpack_from('<I', out, 108)[0] | 0x400008)
    return bytes(out)


def pac_texture_hashes(pac, work):
    """{'color': hash16, 'normal': hash16} of the pac's first attire (XXH3-64 of the DDS top level, as the port)"""
    import ch_tool
    import svrfmt
    _, groups = ch_tool.epk8_read(open(pac, 'rb').read())
    out = {}
    for _, data in svrfmt.pach_read(groups[0][1][0][1]):
        raw = svrfmt.unbpe(data)
        if not ch_tool.is_bundle(raw):
            continue
        for k in range(struct.unpack_from('<I', raw, 0)[0]):
            rec = raw[16 + 32 * k:48 + 32 * k]
            nm = rec[:16].split(b'\0')[0].decode('latin1')
            size, off = struct.unpack_from('<II', rec, 20)
            if nm not in ('color', 'normal'):
                continue
            d = raw[off:off + size]
            hh, w = struct.unpack_from('<II', d, 12)
            tmp = os.path.join(work, '_pac_%s.dds' % nm)
            open(tmp, 'wb').write(d)
            top = ((w + 3) // 4) * ((hh + 3) // 4) * 16
            # (PortIT 1.0.1: in Python - users' antivirus / Smart App Control blocked ddshash.exe: "ddshash failed: ")
            import xxh3
            out[nm] = xxh3.file_hash(tmp, 128, top)
    return out


PACK_SIZE = [8192]   # (the HD sheets' width in the pack: 8192 (8192 x 4096, ~90 MB a wrestler), 4096 (~22 MB) or
#                       2048 (~6 MB) - the worker sets it from the port request's hd_size, PortIT 2026-11-10: people's
#                       packs reached ~1 GB)


def make_pack(work, pac, out_dir, prefix, cut_cells, log=print):
    """the full sheets (work\\color.png / normal.png) -> out_dir\\<prefix>_<color|normal>_<hash>.dds, at PACK_SIZE"""
    from PIL import Image
    os.makedirs(out_dir, exist_ok=True)
    # (this port's older pack files go first: their names carry the old content hash, and left beside the new ones
    #  they were installed too - each re-port added another 45 MB file, 2026-11-10)
    for f in os.listdir(out_dir):
        if f.startswith(prefix + '_') and f.lower().endswith('.dds'):
            os.remove(os.path.join(out_dir, f))
    hashes = pac_texture_hashes(pac, work)
    files = []
    for nm in ('color', 'normal'):
        im = Image.open(os.path.join(work, nm + '.png')).convert('RGBA')
        cells = cut_cells if nm == 'color' else []
        w = PACK_SIZE[0]
        if im.size[0] > w:
            k = w / im.size[0]
            if nm == 'normal':      # (RGB and alpha apart: Pillow premultiplies RGBA - see rock_model.cell_image)
                rgb = im.convert('RGB').resize((w, int(im.size[1] * k)), Image.LANCZOS)
                im = Image.merge('RGBA', rgb.split() + (im.getchannel('A').resize(rgb.size, Image.LANCZOS),))
            else:
                import w13_char as W
                im = W.resize_rgba(im, (w, int(im.size[1] * k)), Image.LANCZOS)
            cells = [tuple(int(round(v * k)) for v in c) for c in cells]
        f = os.path.join(out_dir, '%s_%s_%s.dds' % (prefix, nm, hashes[nm]))
        open(f, 'wb').write(_dds_levels(im, cells))
        files.append(f)
        log('texture pack: %s (%d x %d, %.1f MB)' % (os.path.basename(f), im.size[0], im.size[1], os.path.getsize(f) / 1048576))
    return files


def window_pages(game, log=print):
    """pages a wrestler's model may take in this game: its vertex window (the DLL's bigmodel.log "vertex windows ...
    -> N KB") / 4 KB, less a margin; no log: the classic 768 KB window. A model past its window crashes the draw even
    on port 2.1.9 (its "anywhere" placement), so this - not the port's 1600 - is the full route's limit."""
    import re
    kb = 768
    try:
        m = re.findall(r'vertex windows[^:]*: *\d+ *-> *(\d+) *KB', open(os.path.join(game, 'bigmodel.log'), encoding='utf-8',
                                                                         errors='replace').read())
        if m:
            kb = int(m[-1])
    except OSError:
        pass
    pages = kb // 4 - 24
    log('vertex window: %d KB a wrestler -> models up to %d pages%s' % (kb, pages,
        '' if kb > 768 else ' (no big-model DLL found: install it for full-detail ports)'))
    return pages


def enabled_pack(game):
    """the first texture pack enabled in the game's svr2011.toml (native_texture_packs), or None"""
    try:
        for line in open(os.path.join(game, 'svr2011.toml'), encoding='utf-8', errors='replace'):
            k, _, v = line.partition('=')
            if k.strip() == 'native_texture_packs':
                v = v.strip().strip('"')
                first = [p.strip() for p in v.split(';') if p.strip()]   # (the port splits on ';')
                return first[0] if first else None
    except OSError:
        pass
    return None
