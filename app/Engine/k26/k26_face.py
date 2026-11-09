# (PortIT copy of tools/k26_porter/k26_face.py - the pipeline of the working Rock / Brock ports; only file paths changed, marked PortIT:)
"""Face rig for a ported head: the template's face skinning (the ~30 bones under atama the game animates for
expressions, blinks and the mouth) copied onto the new head, and the face bones moved onto the new face.

2K26 faces move by morph targets (the head mesh is weighted to J_Head only), so nothing of theirs carries over:
  1. landmarks on both faces - eye centres, mouth corners and middle (the lips' border loop), nose tip, chin;
  2. an affine fit template face -> new face from them, refined by a few rounds of closest points (ICP) over the
     front of the face, the landmarks kept;
  3. per new head vertex: the face-bone weights of the nearest template face vertices (upper / lower lip kept on
     their own side of the mouth), the rest of the vertex's own weights scaled into what is left;
  4. the template's face bone positions through the same fit -> model.json joints.

    fit = fit_face(template_pac, head_unit_verts, head_tris, eye_l, eye_r)
    weights = transfer(fit, verts, own_weights)       -> per vertex [(bone name, w), ...]
    joints = fit['joints']                            -> {face bone: [x, y, z]}
"""
import os
import sys

import numpy as np

TOOLS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'svr')   # PortIT: the bundled tools
sys.path.insert(0, TOOLS)
import w13_char as W  # noqa: E402

UPPER = ('u_kuchi', 'u_kuchi_00', 'l_u_kuchi', 'r_u_kuchi')
LOWER = ('d_kuchi', 'd_kuchi_00', 'd_kuchi_01', 'l_kuchi', 'r_kuchi')


def _border_loops(tris, n):
    ec = {}
    for t in tris:
        for i in range(3):
            e = (min(t[i], t[(i + 1) % 3]), max(t[i], t[(i + 1) % 3]))
            ec[e] = ec.get(e, 0) + 1
    adj = {}
    for (a, b), c in ec.items():
        if c == 1:
            adj.setdefault(a, []).append(b)
            adj.setdefault(b, []).append(a)
    seen, loops = set(), []
    for s in adj:
        if s in seen:
            continue
        comp, st = [], [s]
        while st:
            x = st.pop()
            if x in seen:
                continue
            seen.add(x)
            comp.append(x)
            st += adj[x]
        loops.append(np.array(comp))
    return loops


def _weld(V, tris, tol=1e-3, side=None):
    """vertices at one position -> one (UV seams); never across sides (stock lips touch at the same positions)"""
    key = {}
    rep = np.zeros(len(V), int)
    for i, p in enumerate(map(tuple, np.round(V / tol).astype(np.int64))):
        rep[i] = key.setdefault(p + ((int(side[i]),) if side is not None else ()), i)
    return rep[tris]


def landmarks(V, tris, eye_l, eye_r, side=None, weld_side=None):
    """-> dict of 3D landmarks on a head mesh (2011 axes: -Y up, -Z front, +X left); side: per vertex -1 / 0 / +1
    (upper / lower lip) to keep touching lips apart"""
    wt = _weld(V, tris, side=side if side is not None else weld_side)
    mid = (eye_l + eye_r) / 2
    iod = np.linalg.norm(eye_l - eye_r)
    # the mouth: the border loop below the eyes, in front, about one eye distance wide
    best = None
    if side is not None:
        # stock lips: closed behind (the mouth pocket is part of the face mesh), so the lips' line is no open edge -
        # it is where upper-lip vertices touch lower-lip ones
        used = np.unique(tris)
        up = used[side[used] == -1]
        lo = used[side[used] == 1]
        d = np.linalg.norm(V[up][:, None] - V[lo][None], axis=2)
        near = d < 0.02
        st = side[tris]
        corner = np.unique(tris[(st == -1).any(1) & (st == 1).any(1)])     # (the triangles joining the lips)
        best = np.concatenate([up[near.any(1)], lo[near.any(0)], corner])
        if not len(best):
            best = None
    for L in (_border_loops(wt, len(V)) if best is None else []):
        P = V[L]
        c = P.mean(0)
        if not (0.6 * iod < c[1] - mid[1] < 2.2 * iod) or abs(c[0] - mid[0]) > 0.3 * iod:
            continue
        # (and not behind the eyes: a mouth is in front of them (-Z). AEW heads end in an open edge at the back of
        #  the neck that falls in this window on smaller heads - Britt Baker / Thunder Rosa got it as the mouth, the
        #  face fitted 1.2 - 1.45x too wide, the lips tore when the jaw opened, 2026-11-09)
        if c[2] - mid[2] > 0.6 * iod:
            continue
        w = P[:, 0].max() - P[:, 0].min()
        if not 0.4 * iod < w < 2.0 * iod:
            continue
        if best is None or len(L) > len(best):
            best = L
    if best is None:
        raise ValueError('no mouth opening found')
    P = V[best]
    ml, mr = P[P[:, 0].argmax()], P[P[:, 0].argmin()]
    mc = np.array([P[:, 0].mean(), P[:, 1].mean(), P[:, 2].min()])     # (z: the front of the lips' line)
    # nose tip: the most forward point between the eyes' and the mouth's height, near the middle
    used = np.unique(tris)
    U = V[used]
    sel = (np.abs(U[:, 0] - mid[0]) < 0.25 * iod) & (U[:, 1] > mid[1]) & (U[:, 1] < mc[1])
    nose = U[sel][U[sel][:, 2].argmin()]
    # chin: the most forward-down point on the middle below the mouth
    sel = (np.abs(U[:, 0] - mid[0]) < 0.25 * iod) & (U[:, 1] > mc[1] + 0.3 * iod) & (U[:, 1] < mc[1] + 1.6 * iod)
    C = U[sel]
    chin = C[(C[:, 1] - C[:, 2]).argmax()]
    return dict(eye_l=eye_l, eye_r=eye_r, mouth_l=ml, mouth_r=mr, mouth=mc, nose=nose, chin=chin), best


def _affine(src, dst, w=None, depth_tie=4.0):
    """least squares x' = s * x + t per axis (both faces are upright and look down -Z: no rotation or shear, so
    points behind the face - the jaw hinge - stay where they belong); the depth scale is tied to the mean of the
    other two (the front of a face says little about its depth)"""
    n = len(src)
    w = np.ones(n) if w is None else np.asarray(w, float)
    sc, tr = np.ones(3), np.zeros(3)
    for ax in range(3):
        X = np.stack([src[:, ax], np.ones(n)], 1) * np.sqrt(w)[:, None]
        Y = dst[:, ax] * np.sqrt(w)
        (sc[ax], tr[ax]), *_ = np.linalg.lstsq(X, Y, rcond=None)
    # tie the depth scale: refit z's offset with s_z pulled toward the mean of s_x, s_y
    sm = (sc[0] + sc[1]) / 2
    W2 = w.sum()
    sz = (np.sum(w * src[:, 2] * (dst[:, 2] - np.average(dst[:, 2], weights=w))) + depth_tie * W2 * sm * np.var(src[:, 2])) /          (np.sum(w * src[:, 2] * (src[:, 2] - np.average(src[:, 2], weights=w))) + depth_tie * W2 * np.var(src[:, 2]) + 1e-12)
    sc[2] = sz
    tr[2] = np.average(dst[:, 2] - sz * src[:, 2], weights=w)
    M = np.zeros((4, 3))
    M[0, 0], M[1, 1], M[2, 2] = sc
    M[3] = tr
    return M          # (4, 3): [x 1] @ M


def _tps_fit(X, Y, lam):
    """3D thin-plate spline (kernel r) X -> Y with per-point smoothing lam"""
    n = len(X)
    K = np.linalg.norm(X[:, None] - X[None], axis=2) + np.diag(lam)
    P = np.concatenate([np.ones((n, 1)), X], 1)
    A = np.zeros((n + 4, n + 4))
    A[:n, :n], A[:n, n:], A[n:, :n] = K, P, P.T
    B = np.zeros((n + 4, 3))
    B[:n] = Y
    sol = np.linalg.solve(A, B)
    return X, sol[:n], sol[n:]


def _tps_apply(tps, P):
    X, w, a = tps
    U = np.linalg.norm(P[:, None] - X[None], axis=2)
    return U @ w + np.concatenate([np.ones((len(P), 1)), P], 1) @ a


def _nearest(A, B, chunk=2048):
    """index of the nearest B point for each A point"""
    out = np.empty(len(A), int)
    d2 = np.empty(len(A))
    bb = (B * B).sum(1)
    for i in range(0, len(A), chunk):
        a = A[i:i + chunk]
        D = (a * a).sum(1)[:, None] - 2 * a @ B.T + bb[None, :]
        out[i:i + chunk] = D.argmin(1)
        d2[i:i + chunk] = D[np.arange(len(a)), out[i:i + chunk]]
    return out, np.sqrt(np.maximum(d2, 0))


def template_face(pac):
    """the template's skin vertices with face-bone weights (+ a margin), welded, with their weights"""
    c = W.Conv(pac)
    Wn = c.world11(c.tnodes)
    names = [n['name'] for n in c.tnodes]
    atama = c.tindex['atama']
    under = set()
    for i in range(len(c.tframe)):
        j = i
        while j >= 0:
            if j == atama:
                under.add(i)
                break
            j = c.tframe[j]['parent']
    V, N, T, Wl = [], [], [], []
    for s in c.body.meshes:
        if s.shader == 'yReflect':
            continue
        nw = len(s.weights)
        flat = [e for b in s.weights for e in b]
        vc = len(s.verts)
        # (the mouth inside - small meshes on atama / d_kuchi only - is not the face: its open edges looked like
        #  the lips to landmarks(), and its weights are no skin's)
        bset = {names[s.palette[sl[0]] - 1] for sl, w in flat if sl[0] != 255 and w > 0}
        if vc <= 600 and bset <= {'atama', 'd_kuchi'}:
            continue
        off = len(V)
        for v in range(vc):
            V.append(s.verts[v][:3])
            N.append(s.verts[v][3:6])
            Wl.append([(names[s.palette[sl[0]] - 1], w) for sl, w in flat[v * nw:(v + 1) * nw] if sl[0] != 255 and w > 0])
        for prim, idx in s.strips:
            import jboy
            tr = jboy.strip_to_tris(idx) if prim == 6 else [tuple(idx[j:j + 3]) for j in range(0, len(idx) - 2, 3)]
            T += [(a + off, b + off, cc + off) for a, b, cc in tr if len({a, b, cc}) == 3]
    V = np.array(V)
    T = np.array(T)
    face_bones = {names[i] for i in under} - {'atama', 'l_eye', 'r_eye'}
    pos = {names[i]: Wn[i][:3, 3] for i in under}
    return dict(V=V, N=np.array(N), T=T, W=Wl, face_bones=face_bones, pos=pos,
                eye_l=Wn[c.tindex['l_eye']][:3, 3], eye_r=Wn[c.tindex['r_eye']][:3, 3])


def fit_face(template_pac, V, tris, eye_l, eye_r, icp_rounds=8, src_side=None):
    tf = template_face(template_pac)
    # the template's head: its skin vertices that hang on atama or a face bone at all
    head = np.array([any(b in tf['face_bones'] or b == 'atama' for b, _ in wl) for wl in tf['W']])
    th = np.nonzero(head)[0]
    keep_t = head[tf['T']].all(1)
    side_all = np.array([(-1 if u > l + 0.05 else (1 if l > u + 0.05 else 0)) for u, l in
                         ((sum(w for b, w in e if b in UPPER), sum(w for b, w in e if b in LOWER)) for e in tf['W'])])
    Ls, mouth_s = landmarks(tf['V'], tf['T'][keep_t], tf['eye_l'], tf['eye_r'], side=side_all)
    touching = False
    try:
        Ld, mouth_d = landmarks(V, tris, eye_l, eye_r)
    except ValueError:
        # (lips modelled touching - the upper and lower lip edges on the very same points, welded shut: 2K26 Bray
        #  Wyatt #861, 'no mouth opening found', 2026-11-09). The source's own sides (its jaw-open shape / lip bones)
        #  keep them apart: the opening is where they touch
        if src_side is None:
            raise
        Ld, mouth_d = landmarks(V, tris, eye_l, eye_r, weld_side=(np.asarray(src_side) + 1))
        touching = True
        print('face fit: the lips touch - opened along the source rig lip line')
    keys = list(Ls)
    src = np.array([Ls[k] for k in keys])
    dst = np.array([Ld[k] for k in keys])
    lw = np.array([8.0 if k.startswith(('eye', 'mouth')) else 4.0 for k in keys])
    M = _affine(src, dst, lw)
    # ICP over the front of the face
    iod = np.linalg.norm(tf['eye_l'] - tf['eye_r'])
    mid = (tf['eye_l'] + tf['eye_r']) / 2
    S = tf['V'][th]
    front = S[:, 1] < Ls['chin'][1]          # (the whole head above the chin: its depth places the jaw hinge)
    S = S[front]
    used = np.unique(tris)
    D = V[used]
    for _ in range(icp_rounds):
        Sx = np.concatenate([S, np.ones((len(S), 1))], 1) @ M
        j, d = _nearest(Sx, D)
        ok = d < np.percentile(d, 85)
        M = _affine(np.concatenate([S[ok], src]), np.concatenate([D[j[ok]], dst]),
                    np.concatenate([np.ones(ok.sum()), lw * max(1.0, ok.sum() / 40.0)]))

    # thin-plate spline from the stretch fit's closest-point pairs (smoothed) + the landmarks (exact): the lips land
    # on the lips, the skull's depth places the jaw hinge
    Sx = np.concatenate([S, np.ones((len(S), 1))], 1) @ M
    j, d = _nearest(Sx, D)
    ok = np.nonzero(d < np.percentile(d, 80))[0]
    rng = np.random.default_rng(0)
    ok = rng.choice(ok, size=min(len(ok), 220), replace=False)
    ctrl = np.concatenate([src, S[ok]])
    goal = np.concatenate([dst, D[j[ok]]])
    lam = np.concatenate([np.zeros(len(src)), np.full(len(ok), 0.05)])
    tps = _tps_fit(ctrl, goal, lam)

    def apply(P):
        return _tps_apply(tps, np.atleast_2d(P))

    # bone pivots: near the surface through the warp; deep ones (> 0.3 behind the eyes: the jaw hinge, outer
    # cheeks, back of the brows) through the whole-head stretch, which keeps the skull's depth
    eye_z = (tf['eye_l'][2] + tf['eye_r'][2]) / 2
    joints = {}
    for b in tf['face_bones']:
        if b not in tf['pos']:
            continue
        p = tf['pos'][b]
        joints[b] = ((np.append(p, 1) @ M) if p[2] > eye_z + 0.3 else apply(p)[0]).tolist()
    # Guard (2026-11-07, 2K26 Bron Breakker): the spline can swing wildly near the lips, where the landmarks and the
    # closest-point pairs disagree - his lip bones landed up to 2 units off (l_kuchi on the right side, by the eye),
    # and every expression swung his lips around those far points (a skin flap by the eye, a torn mouth). The lip
    # bones (kuchi, not the jaw hinge) now take the template mouth's local stretch (as template_mouth: width from the
    # corners, height from nose to chin, on the mouth's middle); any other face bone the spline puts more than 0.25
    # from the whole-head stretch takes the stretch.
    a, bd = Ls, Ld
    sx = abs(bd['mouth_l'][0] - bd['mouth_r'][0]) / max(abs(a['mouth_l'][0] - a['mouth_r'][0]), 1e-6)
    sy = (bd['chin'][1] - bd['nose'][1]) / ((a['chin'][1] - a['nose'][1]) or 1e-6)
    msc = np.array([sx, sy, (sx + sy) / 2])
    guarded = []
    for b in list(joints):
        p = tf['pos'][b]
        pa = np.append(p, 1) @ M
        if 'kuchi' in b and b != 'd_kuchi':
            q = (p - a['mouth']) * msc + bd['mouth']
        elif np.linalg.norm(np.array(joints[b]) - pa) > 0.25:
            q = pa
        else:
            continue
        if np.linalg.norm(np.array(joints[b]) - q) > 0.05:
            guarded.append('%s %.2f' % (b, float(np.linalg.norm(np.array(joints[b]) - q))))
        joints[b] = q.tolist()
    if guarded:
        print('face fit: bones placed by the stretch, not the spline (moved by): ' + ', '.join(guarded))
    Sx = apply(tf['V'][th])
    # (the same guard for the template's skin the weights are copied from: where the spline swung a vertex more
    #  than 0.3 from the whole-head stretch - the mouth pocket deep in the head landed by Bron's right eye, and
    #  vertices there took lower-lip weights: a flap by the eye in game - the stretch places it)
    Sa = np.concatenate([tf['V'][th], np.ones((len(th), 1))], 1) @ M
    swung = np.linalg.norm(Sx - Sa, axis=1) > 0.3
    Sx[swung] = Sa[swung]
    if swung.any():
        print('face fit: %d template vertices placed by the stretch (the spline swung them)' % int(swung.sum()))
    # side of the mouth for template and new lip vertices (the lips' border loop split by the corners)
    side_t = np.zeros(len(th))
    for k, i in enumerate(th):
        up = sum(w for b, w in tf['W'][i] if b in UPPER)
        lo = sum(w for b, w in tf['W'][i] if b in LOWER)
        side_t[k] = -1 if up > lo + 0.05 else (1 if lo > up + 0.05 else 0)
    res = dict(warp=apply, landmarks_tmpl=Ls, M=M, scale=np.diag(M[:3]).tolist(), joints=joints, Sx=Sx, SW=[tf['W'][i] for i in th], side_t=side_t, face_bones=tf['face_bones'],
               landmarks_src={k: apply(v)[0] for k, v in Ls.items()}, landmarks_dst=Ld, mouth_loop=mouth_d,
               residual=float(np.mean(np.linalg.norm(apply(src) - dst, axis=1))), lips_touch=touching)
    return res


def cut_lips(V, tris, src_side, L, margin=0.1):
    """touching lips (fit_face's lips_touch): the triangles joining an upper-lip vertex to a lower-lip one between the
    mouth's corners and near its line are the closed mouth's skin - left out, so the mouth opens there (2K26 Bray
    Wyatt #861: a skin sheet stretched across his open mouth, 2026-11-09) -> kept triangles"""
    s = np.asarray(src_side)[tris]
    cross = (s.min(1) < 0) & (s.max(1) > 0)
    c = V[tris].mean(1)
    x0, x1 = sorted((L['mouth_l'][0], L['mouth_r'][0]))
    near = (c[:, 0] > x0) & (c[:, 0] < x1) & (np.abs(c[:, 1] - L['mouth'][1]) < margin)
    cut = cross & near
    if cut.any():
        print('face fit: the touching lips opened - %d triangles between them left out' % int(cut.sum()))
    return tris[~cut]


def lip_sides(V, tris, loop, ml, mr, radius=0.35):
    """per vertex: -1 upper lip, +1 lower lip, 0 neither - by which half of the lips' border loop it is nearest to
    along the surface (welded at UV seams), within radius: the inner lips curl deep into the mouth, where height says
    nothing about the side"""
    import heapq
    wt = _weld(V, tris)
    adj = {}
    for t in wt:
        for i in range(3):
            a, b = t[i], t[(i + 1) % 3]
            adj.setdefault(a, set()).add(b)
            adj.setdefault(b, set()).add(a)
    rep = _weld(V, np.arange(len(V))[:, None].repeat(3, 1))[:, 0]
    # the loop as a cycle (its border edges), cut at the two corners: one way round is a lip, the other the other
    # (the lips touch: both edges of the opening are at about one height, so height can't split them)
    ec = {}
    for t in wt:
        for i in range(3):
            e = (min(t[i], t[(i + 1) % 3]), max(t[i], t[(i + 1) % 3]))
            ec[e] = ec.get(e, 0) + 1
    ring = {rep[i] for i in loop}
    nb = {}
    for (a, b), c in ec.items():
        if c == 1 and a in ring and b in ring:
            nb.setdefault(a, []).append(b)
            nb.setdefault(b, []).append(a)
    start = max(ring, key=lambda r: V[r][0])
    # (never onto a point already walked: where more than two border edges meet - 2K25 Ricky Steamboat's lips - the
    #  walk went back and forth for ever and filled the memory, 2026-11-07; with two ways on, the one that keeps
    #  going round: the most unwalked border neighbours)
    # Traced as a pen would: each border edge once (a pinch - the lips touching at a point, 4 border edges - is passed
    # twice), and at a pinch the straightest way on (in the front view) - that keeps to the same lip; taking the
    # other one put most of Ricky's lower lip on the upper side (upper 524, lower 169: his lower lip stayed shut
    # and a strip of it stretched to the chin when the jaw opened).
    used = set()
    cyc, prev, cur = [start], None, start
    while True:
        nxt = [x for x in nb.get(cur, []) if (min(cur, x), max(cur, x)) not in used]
        if not nxt:
            break
        if prev is None or len(nxt) == 1:
            # (the first step: the way round with the upper side first does not matter - the halves are swapped below)
            n2 = nxt[0]
        else:
            d0 = (V[cur] - V[prev])[:2]
            d0 = d0 / max(np.linalg.norm(d0), 1e-9)

            def turn(x):
                d1 = (V[x] - V[cur])[:2]
                return -float(d0 @ (d1 / max(np.linalg.norm(d1), 1e-9)))
            n2 = min(nxt, key=turn)
        used.add((min(cur, n2), max(cur, n2)))
        prev, cur = cur, n2
        if cur == start:
            break
        cyc.append(cur)
    k = cyc.index(min(cyc, key=lambda r: V[r][0]))
    half_a, half_b = cyc[:k + 1], cyc[k:] + [start]
    def off_ring_y(h):      # (the vertices just off each half: the upper lip's are higher, -Y up)
        return np.mean([V[x][1] for r in h[1:-1] for x in adj.get(r, ()) if x not in ring])
    up, lo = (half_a, half_b) if off_ring_y(half_a) < off_ring_y(half_b) else (half_b, half_a)
    dist, lab, heap = {}, {}, []
    for r in up[1:-1]:
        dist[r], lab[r] = 0.0, -1
        heapq.heappush(heap, (0.0, r))
    for r in lo[1:-1]:
        dist[r], lab[r] = 0.0, 1
        heapq.heappush(heap, (0.0, r))
    while heap:
        d, a = heapq.heappop(heap)
        if d > dist.get(a, 1e9) or d > radius:
            continue
        for b in adj.get(a, ()):
            nd = d + float(np.linalg.norm(V[a] - V[b]))
            if nd < dist.get(b, 1e9) and nd <= radius:
                dist[b], lab[b] = nd, lab[a]
                heapq.heappush(heap, (nd, b))
    out = np.array([lab.get(rep[i], 0) for i in range(len(V))])
    # Check the guess above on the whole of each lip: the upper lip's area (out to radius, up to the nose) sits higher
    # (-Y up) than the lower's. The ring's next vertices alone can mislead where the inner lips curl into the mouth:
    # 2K26 Bron Breakker came out with the lips swapped - the upper lip on the jaw, his lip stretched to the chin
    # when the mouth opened (2026-11-07).
    far = np.array([dist.get(rep[i], 0.0) > 0.08 for i in range(len(V))])
    yu, yl = V[(out == -1) & far, 1], V[(out == 1) & far, 1]
    if len(yu) and len(yl) and yu.mean() > yl.mean():
        out = -out
    return out


JAW_BRANCH = ('d_kuchi', 'd_kuchi_00', 'd_kuchi_01', 'l_kuchi', 'r_kuchi')   # (the jaw and the bones under it)
JAW_FREE = 0.12      # units: skin this far above the lips' line takes no jaw weight


def transfer(fit, V, own, tris=None, k=4, reach=0.6, gain=1.0, src_side=None):
    """V (n,3) new head vertices (2011 units, the head given to fit_face), own [[(bone, w)], ...] -> new weights;
    gain < 1 tones the face down (the rest goes to the head's own bones): the game's expressions are broad"""
    Ld = fit['landmarks_dst']
    ml, mr, mc = Ld['mouth_l'], Ld['mouth_r'], Ld['mouth']
    Sx, side_t = fit['Sx'], fit['side_t']
    try:
        sides = lip_sides(V, tris, fit['mouth_loop'], ml, mr) if tris is not None else None
    except Exception:   # noqa: BLE001  (touching lips: the walk has no open edge - the source's sides below)
        sides = None
    if src_side is not None and (sides is None or not np.any(sides)):
        # (no walk: the lips' area by distance to the mouth's line, each vertex's lip from the source rig)
        Lp = V[np.asarray(fit['mouth_loop'])]
        d = np.sqrt(((V[:, None] - Lp[None]) ** 2).sum(-1)).min(1)
        sides = np.where(d < 0.35, np.asarray(src_side), 0)
    if sides is not None and src_side is not None and len(src_side) == len(sides):
        # (the walk finds the lips; which lip each vertex is on comes from the source game's own rig: 2K's AU27
        #  jaw-open shape, AEW's lip / jaw bones - see rock_fbx.lip_side_au27, aew_bridge)
        src = np.asarray(src_side)
        sides = np.where((sides != 0) & (src != 0), src, sides)
    above = min(ml[1], mr[1], mc[1]) - JAW_FREE      # (2011: -Y up)
    out = []
    bb = (Sx * Sx).sum(1)
    D = (V * V).sum(1)[:, None] - 2 * V @ Sx.T + bb[None, :]
    D = np.sqrt(np.maximum(D, 0))
    for i, v in enumerate(V):
        d = D[i]
        # lip vertices: only template vertices of the same side
        s = sides[i] if sides is not None else 0
        if s:      # (a lip vertex: only the template's same lip, however far - the inner lips curl deep)
            d = np.where(side_t == s, d, np.inf)
        nn = np.argsort(d)[:k]
        if not np.isfinite(d[nn[0]]) or (d[nn[0]] > reach and not s):
            out.append(own[i])
            continue
        wsum = {}
        tot = 0.0
        for j in nn:
            if not np.isfinite(d[j]):
                continue
            g = 1.0 / (d[j] + 0.02)
            tot += g
            for b, w in fit['SW'][j]:
                if b in fit['face_bones']:
                    wsum[b] = wsum.get(b, 0.0) + g * w
        face = {b: gain * w / tot for b, w in wsum.items() if gain * w / tot > 0.01}
        F = sum(face.values())
        if F <= 0:
            out.append(own[i])
            continue
        F = min(F, 1.0)
        mix = [(b, w) for b, w in face.items()] + [(b, w * (1 - F)) for b, w in own[i]]
        # (nothing well above the lips follows the jaw: AEW Penta's nostril took the template's lower-lip weights
        #  (d_kuchi_01 0.31) and stretched down to his chin when the mouth opened, 2026-11-09) - its jaw share to atama
        if v[1] < above:
            jw = sum(w for b, w in mix if b in JAW_BRANCH)
            if jw > 0:
                mix = [(b, w) for b, w in mix if b not in JAW_BRANCH] + [('atama', jw)]
        out.append(mix)
    return out


def template_mouth(template_pac, fit, gain=1.0):
    """the template's mouth inside (gums + teeth, lower teeth + tongue: small skin meshes hanging on atama /
    d_kuchi only, between the eyes and the chin) warped into the new mouth -> [dict(positions, normals, uvs (the
    template sheet's), weights [(bone, w)], triangles (as stored: stock winding), specular)]"""
    import struct
    import jboy
    c = W.Conv(template_pac)
    names = [n['name'] for n in c.tnodes]
    out = []
    for s in c.body.meshes:
        if s.shader != 'yBumpMapSS' or len(s.verts) > 600:
            continue
        nw = len(s.weights)
        flat = [e for b in s.weights for e in b]
        wl = [[(names[s.palette[sl[0]] - 1], w) for sl, w in flat[v * nw:(v + 1) * nw] if sl[0] != 255 and w > 0]
              for v in range(len(s.verts))]
        if not all(b in ('atama', 'd_kuchi') for e in wl for b, _ in e):
            continue
        P = np.array([v[:3] for v in s.verts])
        N = np.array([v[3:6] for v in s.verts])
        # a local stretch from the mouth landmarks (the spline is pinned to the skin and swings wildly this deep):
        # width from the corners, height from nose to chin, depth their mean, on the mouth's middle
        a, b = fit['landmarks_tmpl'], fit['landmarks_dst']
        sx = abs(b['mouth_l'][0] - b['mouth_r'][0]) / abs(a['mouth_l'][0] - a['mouth_r'][0])
        sy = (b['chin'][1] - b['nose'][1]) / (a['chin'][1] - a['nose'][1])
        sc = np.array([sx, sy, (sx + sy) / 2])
        Q = (P - a['mouth']) * sc + b['mouth']
        Nn = N / sc
        Nn /= np.maximum(np.linalg.norm(Nn, axis=1, keepdims=True), 1e-9)
        tris = []
        for prim, idx in s.strips:
            tr = jboy.strip_to_tris(idx) if prim == 6 else [tuple(idx[j:j + 3]) for j in range(0, len(idx) - 2, 3)]
            tris += [t for t in tr if len(set(t)) == 3]
        p = {n: v for n, t, v in s.params}
        spec = struct.unpack('>f', p['g_fSpecularLev'][:4])[0] if 'g_fSpecularLev' in p else None
        wl = [[(b, w * (gain if b == 'd_kuchi' else 1.0)) for b, w in e] +
              [('atama', sum(w for b, w in e if b == 'd_kuchi') * (1 - gain))] for e in wl]
        out.append(dict(positions=Q, normals=Nn, uvs=np.array(s.uvs, float), weights=wl, triangles=np.array(tris),
                        specular=spec))
    return out
