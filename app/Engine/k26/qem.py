# (PortIT copy of tools/k26_porter/qem.py - the pipeline of the working Rock / Brock ports; only file paths changed, marked PortIT:)
"""Mesh decimation by half-edge collapses ordered by quadric error (Garland-Heckbert), for game meshes whose
vertices carry UVs / normals / skin weights: a collapse only removes a vertex (b goes onto its neighbour a, which
keeps its position and attributes), so every surviving vertex keeps its own exact data.

    keep, tris = simplify(V, F, target, locked=None, importance=None, attr=None, attr_weight=0.0)
      V (n,3) positions, F (m,3) triangles; target = vertices to keep (of those F uses)
      locked  (n,) bool: never removed (boundary / seam vertices are locked too unless lock_borders=False: a UV
              seam is a border, since the two sides are separate vertices; unlocked, a border vertex only collapses
              along its border)
      importance (n,) cost multiplier for removing a vertex (the face > 1)
      attr (n,k) per-vertex values (skin weights) whose difference along an edge adds attr_weight * |da| * len^2
    -> keep: indices of the surviving vertices (sorted), tris: (m',3) in ORIGINAL vertex indices
"""
import heapq

import numpy as np


def _quadrics(V, F):
    Q = np.zeros((len(V), 4, 4))
    a, b, c = V[F[:, 0]], V[F[:, 1]], V[F[:, 2]]
    n = np.cross(b - a, c - a)
    area = np.linalg.norm(n, axis=1)
    ok = area > 1e-12
    n[ok] /= area[ok, None]
    d = -np.einsum('ij,ij->i', n, a)
    p = np.concatenate([n, d[:, None]], axis=1)
    K = np.einsum('i,ij,ik->ijk', area * ok, p, p)
    for k in range(3):
        np.add.at(Q, F[:, k], K)
    return Q


def simplify(V, F, target, locked=None, importance=None, attr=None, attr_weight=0.0, min_dot=0.5, min_quality=0.15,
             lock_borders=True):
    V = np.asarray(V, float)
    F = np.asarray(F, int).reshape(-1, 3)
    F = F[(F[:, 0] != F[:, 1]) & (F[:, 1] != F[:, 2]) & (F[:, 0] != F[:, 2])]
    n = len(V)
    lock = np.zeros(n, bool) if locked is None else np.asarray(locked, bool).copy()
    imp = np.ones(n) if importance is None else np.asarray(importance, float)
    # borders (edges of one triangle, incl. UV seams) and non-manifold edges stay
    ecount = {}
    for t in F:
        for i in range(3):
            e = (min(t[i], t[(i + 1) % 3]), max(t[i], t[(i + 1) % 3]))
            ecount[e] = ecount.get(e, 0) + 1
    border = np.zeros(n, bool)
    bedges = set()
    for (a, b), c in ecount.items():
        if c == 1:
            border[a] = border[b] = True
            bedges.add((a, b))
        if c > 2 or (c == 1 and lock_borders):
            lock[a] = lock[b] = True
    Q = _quadrics(V, F)
    faces = [list(t) for t in F]
    alive_f = [True] * len(faces)
    vf = [set() for _ in range(n)]
    for fi, t in enumerate(faces):
        for v in t:
            vf[v].add(fi)
    used = np.zeros(n, bool)
    used[F.ravel()] = True
    alive = used.copy()
    count = int(used.sum())
    stamp = [0] * n
    Vh = np.concatenate([V, np.ones((n, 1))], axis=1)
    A = None if attr is None else np.asarray(attr, float)

    def nbrs(v):
        s = set()
        for fi in vf[v]:
            s.update(faces[fi])
        s.discard(v)
        return s

    def cost(b, a):
        q = Q[a] + Q[b]
        x = Vh[a]
        c = float(x @ q @ x)
        if A is not None and attr_weight:
            L2 = float(((V[a] - V[b]) ** 2).sum())
            c += attr_weight * float(np.abs(A[a] - A[b]).sum()) * L2
        return max(c, 0.0) * imp[b] + 1e-12 * float(((V[a] - V[b]) ** 2).sum())

    heap = []

    def push_edges(v):
        for u in nbrs(v):
            if not lock[v]:
                heapq.heappush(heap, (cost(v, u), v, u, stamp[v], stamp[u]))
            if not lock[u]:
                heapq.heappush(heap, (cost(u, v), u, v, stamp[u], stamp[v]))

    for (a, b) in ecount:
        if not lock[a]:
            heapq.heappush(heap, (cost(a, b), a, b, 0, 0))
        if not lock[b]:
            heapq.heappush(heap, (cost(b, a), b, a, 0, 0))

    def normal(p0, p1, p2):
        return np.cross(V[p1] - V[p0], V[p2] - V[p0])

    def quality(p0, p1, p2):      # 1 for an equilateral triangle, 0 for a sliver
        a = float(np.linalg.norm(normal(p0, p1, p2)))
        e = float(((V[p1] - V[p0]) ** 2).sum() + ((V[p2] - V[p1]) ** 2).sum() + ((V[p0] - V[p2]) ** 2).sum())
        return 2 * 3 ** 0.5 * a / e if e > 0 else 0.0

    while count > target and heap:
        c, b, a, sb, sa = heapq.heappop(heap)
        if not (alive[a] and alive[b]) or stamp[b] != sb or stamp[a] != sa:
            continue
        shared = [fi for fi in vf[b] if a in faces[fi]]
        if not shared:
            continue
        # (borders unlocked: a border vertex only slides along its own border, onto a border neighbour)
        if border[b] and (min(a, b), max(a, b)) not in bedges:
            continue
        # link condition: the common neighbours are exactly the shared triangles' third vertices
        third = {v for fi in shared for v in faces[fi]} - {a, b}
        if nbrs(a) & nbrs(b) != third:
            continue
        ok = True
        for fi in vf[b]:
            if fi in shared:
                continue
            t = faces[fi]
            old = normal(*t)
            nt = [a if v == b else v for v in t]
            new = normal(*nt)
            lo, ln = np.linalg.norm(old), np.linalg.norm(new)
            if ln < 1e-12 or (lo > 1e-12 and float(old @ new) < min_dot * lo * ln):
                ok = False
                break
            qn = quality(*nt)
            if qn < min_quality and qn < quality(*t):      # (no new slivers: they shade as spikes)
                ok = False
                break
        if not ok:
            continue
        for fi in shared:
            alive_f[fi] = False
            for v in faces[fi]:
                vf[v].discard(fi)
        for fi in list(vf[b]):
            faces[fi] = [a if v == b else v for v in faces[fi]]
            vf[a].add(fi)
        vf[b] = set()
        Q[a] += Q[b]
        alive[b] = False
        count -= 1
        stamp[a] += 1
        push_edges(a)
    tris = np.array([faces[fi] for fi in range(len(faces)) if alive_f[fi]], dtype=int).reshape(-1, 3)
    keep = np.nonzero(alive & used)[0]
    return keep, tris
