"""Face weights for SvR from a STOCK SvR head (ch104): align the stock head to the new head with landmarks (eye
pivots, head and jaw pivots; similarity transform), then each new head vertex takes the face-bone weights of
the nearest stock head vertex. The face bones' pivots move with the same transform.
    face_transfer.py <baseModel.mcd> [out.png]"""
import json
import os
import sys

import numpy as np

MM = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'svr')   # (PortIT's bundled SvR tools)
sys.path.insert(0, MM)
GAME = os.environ.get('PORTIT_GAME', '')   # the SvR 2011 game folder (stock heads come from its pac/ch)
import ch_tool  # noqa: E402
import jboy  # noqa: E402
import svrfmt  # noqa: E402

FACE = ['l_mayu_03', 'l_mayu_02', 'l_mayu_01', 'l_mayu_00', 'r_mayu_03', 'r_mayu_02', 'r_mayu_01', 'r_mayu_00',
        'miken', 'l_mabuta', 'r_mabuta', 'l_meziri', 'r_meziri', 'l_hoho02', 'l_hoho01', 'l_hoho', 'r_hoho02',
        'r_hoho01', 'r_hoho', 'hana', 'u_kuchi', 'u_kuchi_00', 'l_u_kuchi', 'r_u_kuchi', 'd_kuchi', 'd_kuchi_00',
        'd_kuchi_01', 'l_kuchi', 'r_kuchi']
HEAD = set(FACE) | {'atama', 'kubi_x', 'l_eye', 'r_eye'}


def stock_head(ch='ch104', game=None):
    d = open(os.path.join(game or GAME, 'pac', 'ch', ch + '.pac'), 'rb').read()
    h, groups = ch_tool.epk8_read(d)
    blob = groups[0][1][0][1]
    raw = svrfmt.unbpe([x for i, x in svrfmt.pach_read(blob) if i == 0][0])
    m = jboy.read(raw)
    nm = [n['name'] for n in m.nodes]
    P, W = [], []
    for me in m.meshes:
        if me.shader == 'yReflect':      # (eyeballs)
            continue
        # weights are VERTEX-major in the file (per vertex n entries); jboy.read splits them as if block-major,
        # so re-flatten: file order = blocks concatenated
        nw = len(me.weights)
        flat = [e for blk in me.weights for e in blk]
        for vi, v in enumerate(me.verts):
            w = {}
            for j in range(nw):
                bb, x = flat[vi * nw + j]
                if x > 0 and bb[0] != 255 and bb[0] < len(me.palette) and me.palette[bb[0]] > 0:
                    b = nm[me.palette[bb[0]] - 1]
                    w[b] = w.get(b, 0) + x
            if any(b in HEAD for b in w):
                P.append(v[:3])
                W.append(w)
    sk = {b['name']: np.array(b['world']) for b in json.load(open(os.path.join(MM, 'svr11_skeleton.json')))['bones']}
    return np.array(P, float), W, sk


def umeyama(src, dst):
    """similarity transform: dst ~= s * R @ src + t"""
    ms, md = src.mean(0), dst.mean(0)
    a, b = src - ms, dst - md
    U, S, Vt = np.linalg.svd(b.T @ a / len(src))
    D = np.eye(3)
    if np.linalg.det(U @ Vt) < 0:
        D[2, 2] = -1
    R = U @ D @ Vt
    s = np.trace(np.diag(S) @ D) / (a ** 2).sum(1).mean()
    return s, R, md - s * R @ ms


def nearest(a, b, chunk=2048):
    out = np.empty(len(a), int)
    dist = np.empty(len(a))
    for i in range(0, len(a), chunk):
        d2 = ((a[i:i + chunk, None, :] - b[None, :, :]) ** 2).sum(2)
        out[i:i + chunk] = d2.argmin(1)
        dist[i:i + chunk] = np.sqrt(d2.min(1))
    return out, dist


# axis conventions: SvR / 2K = -Y up, face to -Z; AEW (UE) = +Z up, face to +Y   (x = character's left in all)
BASIS = {'svr': np.eye(3), '2k': np.eye(3), 'aew': np.array([[1, 0, 0], [0, 0, -1], [0, -1, 0]], float)}


def transfer(new_pos, new_landmarks, stock=None, space='2k'):
    """new_landmarks: dict l_eye, r_eye, atama, d_kuchi -> positions in the new model's space.
    The stock head is first turned into the game's axes (BASIS), so the 4-point fit only fixes size, place and
    small tilts (4 nearly flat points alone can flip up / forward)."""
    P, W, sk = stock or stock_head()
    B = BASIS[space]
    P = P @ B.T
    sk = {k: B @ v for k, v in sk.items()}
    keys = ['l_eye', 'r_eye', 'atama', 'd_kuchi']
    s, R, t = umeyama(np.array([sk[k] for k in keys]), np.array([new_landmarks[k] for k in keys]))
    ang = np.degrees(np.arccos(np.clip((np.trace(R) - 1) / 2, -1, 1)))
    if ang > 25:                 # a flip, not a tilt: keep the axes, fit size and place only
        R = np.eye(3)
        src, dst = np.array([sk[k] for k in keys]), np.array([new_landmarks[k] for k in keys])
        s = np.sqrt(((dst - dst.mean(0)) ** 2).sum() / ((src - src.mean(0)) ** 2).sum())
        t = dst.mean(0) - s * src.mean(0)
    Pn = (s * (R @ P.T)).T + t
    idx, dist = nearest(new_pos, Pn)
    weights = [W[i] for i in idx]
    pivots = {b: s * R @ sk[b] + t for b in FACE + ['atama', 'l_eye', 'r_eye']}
    return weights, dist, pivots, (s, R, t), Pn


# matching face points: SvR face bone (its weighted skin area)  <->  2K FACS shapes (the skin they move)
PAIRS = [('l_mabuta', ['au045_l']), ('r_mabuta', ['au045_r']), ('l_mayu_00', ['au001_l']),
         ('r_mayu_00', ['au001_r']), ('l_mayu_01', ['au002_l']), ('r_mayu_01', ['au002_r']),
         ('miken', ['au004_l', 'au004_r']), ('hana', ['au009_l', 'au009_r']), ('l_kuchi', ['au012_l']),
         ('r_kuchi', ['au012_r']), ('u_kuchi_00', ['au010_l', 'au010_r']), ('d_kuchi_01', ['au016_l', 'au016_r']),
         ('l_hoho', ['au006_lb']), ('r_hoho', ['au006_rb'])]


def stock_centroids(P, W):
    out = {}
    for b, _ in PAIRS:
        w = np.array([x.get(b, 0) for x in W])
        if w.sum() > 0:
            out[b] = (P * w[:, None] ** 2).sum(0) / (w ** 2).sum()
    return out


def shape_centroids(pos, shapes):
    sh = {s['name']: s for s in shapes}
    out = {}
    for b, names in PAIRS:
        acc, tot = np.zeros(3), 0.0
        for n in names:
            if n not in sh or not len(sh[n]['index']):
                continue
            mag2 = (sh[n]['delta'] ** 2).sum(1)
            acc += (pos[sh[n]['index']] * mag2[:, None]).sum(0)
            tot += mag2.sum()
        if tot > 0:
            out[b] = acc / tot
    return out


def tps_fit(src, dst, reg=1e-3):
    n = len(src)
    d = np.linalg.norm(src[:, None] - src[None], axis=2)
    K = d + reg * np.eye(n)          # 3D thin-plate kernel U(r) = r
    Pm = np.hstack([np.ones((n, 1)), src])
    A = np.zeros((n + 4, n + 4))
    A[:n, :n], A[:n, n:], A[n:, :n] = K, Pm, Pm.T
    b = np.zeros((n + 4, 3))
    b[:n] = dst
    coef = np.linalg.solve(A, b)
    return src, coef


def tps_apply(model, pts):
    src, coef = model
    n = len(src)
    U = np.linalg.norm(pts[:, None] - src[None], axis=2)
    return U @ coef[:n] + np.hstack([np.ones((len(pts), 1)), pts]) @ coef[n:]


def transfer_tps(new_pos, new_landmarks, shapes=None, stock=None, space='2k', targets=None):
    """rigid landmark fit first, then a thin-plate warp so the stock face areas sit on the new face's areas:
    2K: the FACS shapes' areas (shapes); other games: targets = {svr face bone: centroid of the matching skin area}"""
    P, W, sk = stock or stock_head()
    w0, d0, piv0, (s, R, t), Pn = transfer(new_pos, new_landmarks, (P, W, sk), space)
    sk = {k: BASIS[space] @ v for k, v in sk.items()}
    sc = stock_centroids(Pn, W)
    tc = targets if targets is not None else shape_centroids(new_pos, shapes)
    keys = [k for k in sc if k in tc]
    anchors_src = [Pn.mean(0) + (Pn - Pn.mean(0)).max(0) * 1.0, Pn.mean(0) + (Pn - Pn.mean(0)).min(0) * 1.0]
    src = np.array([sc[k] for k in keys] + [s * R @ sk[k] + t for k in ('l_eye', 'r_eye', 'atama', 'd_kuchi')]
                   + anchors_src)
    dst = np.array([tc[k] for k in keys] + [new_landmarks[k] for k in ('l_eye', 'r_eye', 'atama', 'd_kuchi')]
                   + anchors_src)
    model = tps_fit(src, dst)
    Pw = tps_apply(model, Pn)
    idx, dist = nearest(new_pos, Pw)
    weights = [W[i] for i in idx]
    pivots = {b: tps_apply(model, (s * R @ sk[b] + t)[None])[0] for b in FACE + ['atama', 'l_eye', 'r_eye']}
    return weights, dist, pivots, keys


def landmarks_2k(mcd):
    W = {b['name']: b['world'][3, :3] for b in mcd.bones}
    return {'l_eye': W['J_Eye_L'], 'r_eye': W['J_Eye_R'], 'atama': W['J_Head'], 'd_kuchi': W['J_Jaw']}


def render(pos, tris, weights, out, title=''):
    from PIL import Image, ImageDraw
    colors = {}
    rng = np.random.RandomState(3)
    for b in FACE:
        colors[b] = tuple(int(x) for x in rng.randint(60, 255, 3))
    img = Image.new('RGB', (520, 560), 'white')
    dr = ImageDraw.Draw(img)
    c = pos.mean(0)
    span = np.ptp(pos[:, :2], 0).max()
    sc = 480 / span
    order = np.argsort(pos[tris].mean(1)[:, 2])[::-1]    # far (+z) first; the face looks to -z
    for tr in tris[order]:
        p = pos[tr]
        n = np.cross(p[1] - p[0], p[2] - p[0])
        if n[2] > 0:            # back facing (seen from -z)
            continue
        wsum = {}
        for v in tr:
            for b, x in weights[v].items():
                if b in colors:
                    wsum[b] = wsum.get(b, 0) + x
        shade = 0.55 + 0.45 * abs(n[2]) / (np.linalg.norm(n) + 1e-9)
        if wsum:
            b = max(wsum, key=wsum.get)
            k = min(1.0, wsum[b] / 3 * 2.5)
            col = tuple(int((255 * (1 - k) + colors[b][i] * k) * shade) for i in range(3))
        else:
            col = tuple(int(235 * shade) for _ in range(3))
        dr.polygon([(260 + (q[0] - c[0]) * sc, 280 + (q[1] - c[1]) * sc) for q in p], fill=col)
    dr.text((8, 8), title, fill='black')
    img.save(out)


if __name__ == '__main__':
    from mcd_parse import Mcd
    m = Mcd(sys.argv[1])
    head = [k for k, mb in enumerate(m.meshes) if mb['material'] == 'M_Head'][0]
    pos = m.stream(head, 'POSITION').astype(float)
    tris = m.lods[0][head]['triangles']
    lm = landmarks_2k(m)
    assert lm['l_eye'][0] > lm['r_eye'][0], 'left/right mirrored'
    weights, dist, pivots, (s, R, t), Pn = transfer(pos, lm)
    print('scale %.3f (stock 10 cm units -> cm)  nearest distance cm: median %.2f  95%% %.2f  max %.2f' % (
        s, np.median(dist), np.percentile(dist, 95), dist.max()))
    face_v = sum(1 for w in weights if any(b in FACE for b in w))
    print('head verts %d, with face weights %d' % (len(pos), face_v))
    out = sys.argv[2] if len(sys.argv) > 2 else 'face_transfer.png'
    render(pos, tris, weights, out, os.path.basename(os.path.dirname(os.path.dirname(sys.argv[1]))))
    P, W, sk = stock_head()
    render(P * 1.0, np.zeros((0, 3), int), W, 'face_stock_dummy.png')
