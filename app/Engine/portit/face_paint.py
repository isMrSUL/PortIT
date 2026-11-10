"""2K26 face paint - used by the port (k26/port_2k.py) and the source preview (portit/source_2k.py).

People's report 2026-11-10: every port lost the face paint. 2K26 keeps it in the base model, two ways:
- BaseModel\\Textures_<look> (+ Materials_<look>): the base textures an attire uses instead, picked by the attire
  folder's name: Tama Tonga's 1220_FacePaint -> Textures_FacePaint, Ultimate Warrior 2017's 254_WM5 ->
  Textures_254_WM5, Finn Balor's Mutiny_Faction ...
- facepaint_color / facepaint_nrm: a see-through layer in the head's own UV layout (Umaga, Asuka, the Boogeyman, Doink,
  the Ultimate Warrior's looks ...) - laid over head_color / head_nrm with its alpha, as the game draws it."""
import os
import re

import numpy as np


def variant_dir(char, attire_dir):
    """BaseModel\\Textures_<look> that goes with this attire folder, or None"""
    base = os.path.join(char, 'BaseModel')
    if not os.path.isdir(base):
        return None
    name = os.path.basename(os.path.normpath(attire_dir)).lower()
    look = re.sub(r'^\d+_', '', name)
    variants = [d for d in os.listdir(base) if d.lower().startswith('textures_') and os.path.isdir(os.path.join(base, d))]
    tail = lambda d: d[len('textures_'):].lower()
    pick = next((d for d in variants if tail(d) in (name, look)), None) or \
        next((d for d in variants if look and (tail(d) in look or look in tail(d))), None)
    return os.path.join(base, pick) if pick else None


def sibling_textures(char, attire_dir):
    """an attire that is only a model (Tama Tonga's 1220_FacePaint): the Textures of a sibling attire, Default first"""
    if os.path.isdir(os.path.join(char, attire_dir, 'Textures')):
        return None
    sib = sorted((d for d in os.listdir(char) if d != 'BaseModel' and os.path.isdir(os.path.join(char, d, 'Textures'))),
                 key=lambda d: (not d.lower().endswith('default_attire'), d))
    return os.path.join(char, sib[0], 'Textures') if sib else None


def _first(dirs, name):
    for d in dirs:
        if d and os.path.isdir(d):
            for f in os.listdir(d):
                if f.lower().split('.')[0] == name:
                    return os.path.join(d, f)
    return None


def prepare(char, attire_dir, out, log=print, normals=True):
    """-> the folders to look in before the attire's / base model's: [painted head (if any), Textures_<look> (if any),
    a sibling attire's Textures (if the attire has none)]. The painted head: out\\_facepaint\\Textures\\head_color.png
    (+ head_nrm.png)."""
    from PIL import Image
    first = []
    var = variant_dir(char, attire_dir)
    if var:
        first.append(var)
        log('textures: this attire uses the base model\'s %s' % os.path.basename(var))
    sib = sibling_textures(char, attire_dir)
    if sib:
        first.append(sib)
        log('textures: %s has none of its own - it wears %s\'s' % (attire_dir, os.path.basename(os.path.dirname(sib))))
    dirs = [var, os.path.join(char, 'BaseModel', 'Textures')]
    paint, head = _first(dirs, 'facepaint_color'), _first(dirs, 'head_color')
    if not (paint and head):
        return first
    dst = os.path.join(out, '_facepaint', 'Textures')
    os.makedirs(dst, exist_ok=True)
    pi = Image.open(paint)
    pi.load()
    pi = pi.convert('RGBA')
    hi = Image.open(head)
    hi.load()
    hi = hi.convert('RGBA')
    if pi.size != hi.size:
        pi = pi.resize(hi.size, Image.LANCZOS)
    alpha = np.asarray(pi.getchannel('A')).astype(np.float64)[..., None] / 255
    h = np.asarray(hi).astype(np.float64)
    p = np.asarray(pi).astype(np.float64)
    c = h.copy()
    c[..., :3] = h[..., :3] * (1 - alpha) + p[..., :3] * alpha
    Image.fromarray(np.clip(c, 0, 255).astype(np.uint8), 'RGBA').save(os.path.join(dst, 'head_color.png'))
    pn, hn = _first(dirs, 'facepaint_nrm'), _first(dirs, 'head_nrm')
    if normals and pn and hn:
        a = Image.open(hn)
        a.load()
        b = Image.open(pn)
        b.load()
        # (RGB and alpha apart: Pillow premultiplies RGBA and the 2K26 normal maps' alpha is ~0 - rock_model.cell_image)
        ha = np.asarray(a.convert('RGB')).astype(np.float64)
        pa = np.asarray(b.convert('RGB').resize(a.size, Image.LANCZOS)).astype(np.float64)
        al = alpha if alpha.shape[:2] == ha.shape[:2] else \
            np.asarray(pi.getchannel('A').resize(a.size, Image.LANCZOS)).astype(np.float64)[..., None] / 255
        Image.fromarray(np.clip(ha * (1 - al) + pa * al, 0, 255).astype(np.uint8), 'RGB').save(os.path.join(dst, 'head_nrm.png'))
    log('face paint: %s laid on the head (%.0f%% of the face texture painted)'
        % (os.path.relpath(paint, char), 100 * float((alpha > 0.04).mean())))
    return [dst] + first
