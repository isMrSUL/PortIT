"""Skin weights for meshes whose bones cannot be mapped (cloth / physics rigs like 2934_BronB_Outfit's 879
'bone_NNNN' bones): each vertex copies the SvR weights of the nearest point of the wrestler's base body.
    weights_fallback.py <baseModel.mcd> <outfit.mcd>"""
import collections
import json
import os
import sys

import numpy as np

import rig_2k
from face_transfer import nearest
from mcd_parse import Mcd


def svr_weights(m, k, template=None, female=False):
    """mesh k of an Mcd -> per vertex {svr bone: weight} through rig_2k"""
    bones = [(b['name'], m.bones[b['parent']]['name'] if b['parent'] >= 0 else None) for b in m.bones]
    res = rig_2k.match(bones, template, female)
    mb = m.meshes[k]
    out = []
    for bs, ws in zip(mb['skin_bones'], mb['skin_weights']):
        d = collections.defaultdict(float)
        for b, w in zip(bs, ws):
            if w > 0:
                d[res['map'][m.bones[b]['name']]] += float(w)
        out.append(dict(d))
    return out, res


def fallback(body_pos, body_w, pos):
    idx, dist = nearest(pos, body_pos)
    return [body_w[i] for i in idx], dist


if __name__ == '__main__':
    base, outfit = Mcd(sys.argv[1]), Mcd(sys.argv[2])
    k = [i for i, mb in enumerate(base.meshes) if mb['material'] == 'M_Body'][0]
    bw, _ = svr_weights(base, k)
    bpos = base.stream(k, 'POSITION').astype(float)
    for j, mb in enumerate(outfit.meshes):
        pos = outfit.stream(j, 'POSITION').astype(float)
        w, dist = fallback(bpos, bw, pos)
        top = collections.Counter()
        for x in w:
            for b, v in x.items():
                top[b] += v
        print('mesh %d %s: %d verts, distance to body cm median %.2f 95%% %.2f max %.2f; bones %s' % (
            j, mb['material'], len(pos), np.median(dist), np.percentile(dist, 95), dist.max(),
            [b for b, _ in top.most_common(8)]))
