"""Mesh simplifier for the SvR vertex budget: quadric error, half-edge collapses (a vertex merges INTO a neighbour
that stays, so UVs, normals and skin weights stay exact - nothing is averaged).

Rules: open borders and UV seams (2K / AEW split vertices there: a border of the triangle mesh) keep their shape -
a border vertex only slides along its own border; no collapse may flip a triangle; collapses that join
vertices driven by different main bones cost more (joints keep their resolution).
    simplify(positions, tris, keep_vertices, main_bone=None) -> (vertex index map old -> kept, new tris)
"""
import heapq

import numpy as np


def _plane_quadrics(P, T):
    a, b, c = P[T[:, 0]], P[T[:, 1]], P[T[:, 2]]
    n = np.cross(b - a, c - a)
    area = np.linalg.norm(n, axis=1)
    ok = area > 1e-12
    n[ok] /= area[ok, None]
    d = -(n * a).sum(1)
    p = np.hstack([n, d[:, None]])                      # plane (a, b, c, d)
    K = p[:, :, None] * p[:, None, :] * (area[:, None, None] * 0.5)
    Q = np.zeros((len(P), 4, 4))
    for k in range(3):
        np.add.at(Q, T[:, k], K)
    return Q


def components(T, nv):
    parent = np.arange(nv)

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    for a, b, c in T:
        for x, y in ((a, b), (b, c)):
            rx, ry = find(x), find(y)
            if rx != ry:
                parent[rx] = ry
    return np.array([find(v) for v in range(nv)])


def simplify(P, T, keep, main_bone=None, border_weight=1000.0, bone_cost=4.0, cards=False):
    """cards=True (hair / fur cards): every card keeps >= 2 triangles and only shortens along its strand"""
    P = np.asarray(P, float)
    T = np.asarray(T, np.int64)
    nv = len(P)
    used0 = len(np.unique(T)) if len(T) else 0
    if keep >= used0 or len(T) == 0:          # (count only vertices the triangles use)
        return np.arange(nv), T
    Q = _plane_quadrics(P, T)
    scale = np.ptp(P, 0).max() or 1.0
    # edges, borders
    edge_count = {}
    for t in T:
        for i in range(3):
            e = (min(t[i], t[(i + 1) % 3]), max(t[i], t[(i + 1) % 3]))
            edge_count[e] = edge_count.get(e, 0) + 1
    border_edges = {e for e, c in edge_count.items() if c == 1}
    border_v = np.zeros(nv, bool)
    for a, b in border_edges:
        border_v[a] = border_v[b] = True
    # border constraint planes (perpendicular to the face, through the edge) keep borders in place
    vert_faces = [set() for _ in range(nv)]
    for fi, t in enumerate(T):
        for v in t:
            vert_faces[v].add(fi)
    for a, b in border_edges:
        f = next(iter(vert_faces[a] & vert_faces[b]))
        t = T[f]
        fn = np.cross(P[t[1]] - P[t[0]], P[t[2]] - P[t[0]])
        e = P[b] - P[a]
        n = np.cross(e, fn)
        ln = np.linalg.norm(n)
        if ln < 1e-12:
            continue
        n /= ln
        p = np.append(n, -n @ P[a])
        K = np.outer(p, p) * border_weight * (np.linalg.norm(e) ** 2 + 1e-6)
        Q[a] += K
        Q[b] += K
    comp = comp_tris = axis = None
    if cards:
        comp = components(T, nv)
        comp_tris = {}
        for t in T:
            comp_tris[comp[t[0]]] = comp_tris.get(comp[t[0]], 0) + 1
        axis = np.zeros((nv, 3))
        for c in np.unique(comp):
            idx = np.nonzero(comp == c)[0]
            X = P[idx] - P[idx].mean(0)
            if len(idx) >= 3:
                axis[idx] = np.linalg.svd(X, full_matrices=False)[2][0]
    nbr = [set() for _ in range(nv)]
    for a, b in edge_count:
        nbr[a].add(b)
        nbr[b].add(a)
    faces = T.copy()
    alive_f = np.ones(len(T), bool)
    alive_v = np.ones(nv, bool)
    to = np.arange(nv)
    version = np.zeros(nv, np.int64)

    def cost(u, v):
        """u collapses into v (v stays)"""
        if border_v[u]:
            if not border_v[v] or (min(u, v), max(u, v)) not in border_edges:
                return None
        if cards:
            e = P[v] - P[u]
            le = np.linalg.norm(e)
            if le > 0 and abs(np.dot(e / le, axis[u])) < 0.8:      # across the card: never
                return None
        x = np.append(P[v], 1.0)
        c = float(x @ (Q[u] + Q[v]) @ x)
        if main_bone is not None and main_bone[u] != main_bone[v]:
            c += bone_cost * (np.linalg.norm(P[u] - P[v]) / scale) ** 2 * 1e3
        return c

    heap = []

    def push(u):
        best = None
        for v in nbr[u]:
            c = cost(u, v)
            if c is not None and (best is None or c < best[0]):
                best = (c, v)
        if best:
            heapq.heappush(heap, (best[0], u, best[1], version[u]))

    for u in range(nv):
        push(u)
    remaining = used0
    while remaining > keep and heap:
        c, u, v, ver = heapq.heappop(heap)
        if not alive_v[u] or not alive_v[v] or ver != version[u] or v not in nbr[u]:
            continue
        # no flipped / degenerate triangles around u
        bad = False
        for f in vert_faces[u]:
            t = faces[f]
            if v in t:
                continue
            a, b, cc = (P[x] for x in t)
            n0 = np.cross(b - a, cc - a)
            t2 = [v if x == u else x for x in t]
            a2, b2, c2 = (P[x] for x in t2)
            n1 = np.cross(b2 - a2, c2 - a2)
            if np.dot(n0, n1) <= 1e-12 * (np.linalg.norm(n0) + 1e-12):
                bad = True
                break
        if not bad and cards:
            lost = sum(1 for f in vert_faces[u] if v in faces[f])
            if comp_tris[comp[u]] - lost < 2:
                bad = True
        if bad:
            version[u] += 1
            continue
        if cards:
            comp_tris[comp[u]] -= sum(1 for f in vert_faces[u] if v in faces[f])
        # collapse
        for f in list(vert_faces[u]):
            t = faces[f]
            if v in t:
                alive_f[f] = False
                for x in t:
                    vert_faces[x].discard(f)
            else:
                faces[f] = [v if x == u else x for x in t]
                vert_faces[v].add(f)
        vert_faces[u] = set()
        for w in nbr[u]:
            nbr[w].discard(u)
            if w != v:
                nbr[w].add(v)
                nbr[v].add(w)
                eu = (min(u, w), max(u, w))
                if eu in border_edges:
                    border_edges.discard(eu)
                    border_edges.add((min(v, w), max(v, w)))
        nbr[v].discard(u)
        nbr[u] = set()
        border_edges.discard((min(u, v), max(u, v)))
        Q[v] += Q[u]
        alive_v[u] = False
        to[u] = v
        remaining -= 1
        version[v] += 1
        push(v)
        for w in nbr[v]:
            version[w] += 1
            push(w)
    # follow the chains
    for i in range(nv):
        j = i
        while to[j] != j:
            j = to[j]
        to[i] = j
    newT = faces[alive_f]
    newT = newT[(newT[:, 0] != newT[:, 1]) & (newT[:, 1] != newT[:, 2]) & (newT[:, 0] != newT[:, 2])]
    return to, newT


def compact(to, T):
    """-> (kept old vertex indices, triangles renumbered)"""
    used = np.unique(T)
    remap = -np.ones(len(to), np.int64)
    remap[used] = np.arange(len(used))
    return used, remap[T]


def cull_cards(P, T, keep_verts):
    """hair: drop whole cards until about keep_verts vertices are used; the small, inner cards (under other layers)
    go first - score = card area x distance from the hair's centre"""
    P = np.asarray(P, float)
    T = np.asarray(T, np.int64)
    comp = components(T, len(P))
    tc = comp[T[:, 0]]
    cards = np.unique(tc)
    centre = P[np.unique(T)].mean(0)
    a, b, c = P[T[:, 0]], P[T[:, 1]], P[T[:, 2]]
    area = np.linalg.norm(np.cross(b - a, c - a), axis=1) * 0.5
    score, nverts = {}, {}
    for k in cards:
        sel = tc == k
        vs = np.unique(T[sel])
        score[k] = area[sel].sum() * (np.linalg.norm(P[vs] - centre, axis=1).mean() + 1e-6)
        nverts[k] = len(vs)
    kept, total = set(), 0
    for k in sorted(cards, key=lambda x: -score[x]):
        if total + nverts[k] > keep_verts and kept:
            continue
        kept.add(k)
        total += nverts[k]
    return T[np.isin(tc, list(kept))]
