"""AEW Fight Forever -> the 2K26 pipeline (PortIT 2026-11-09).

AEW's skeleton is Yuke's like 2K's (the same J_ core), so an AEW wrestler goes through port_2k / rock_model /
full_port - the face fit and its guards, card / gear weights, full detail + HD texture pack, the checks - instead of
the older portit/convert.py route. Bridge(paks, folder, work) reads the wrestler (portit/source_aew.py) and gives:
  * build(char, attire, out, want_bones) - rock_fbx.build's place: meshes [{name, verts, normals, uv, tris, material,
    texture, weights}] and bones [{name, src, parent, world}] in rock_fbx's space (+Y up, the face toward +Z, +X his
    left, cm; AEW's own is Z up, the face toward +Y: a swap of Y and Z), the skin under gear hidden as rock_fbx does;
  * a folder laid out like a 2K mod (char\\Attire\\Textures\\<part>_color.png / _nrm.png) for rock_model.find_tex.
Mapping:
  * the skin (head and body in one AEW mesh) -> M_Head / M_Body by its weights on J_Head and everything under it;
  * the eyes -> M_EYE (eye_l / eye_r), the lashes -> M_TransB_eyelash;
  * the mouth: AEW's own (teeth, gums, tongue - mt_Mouth) -> M_AEWMouth, its occlusion baked into the colour (SvR has
    none: the inside drew lit), the jaw toned as the lips are (rock_model); the template's teeth stay out
    (rock_model.TEMPLATE_MOUTH). The head's inside-of-the-lips piece -> M_InnerMouth on the face texture (occlusion
    baked in its box), following the fitted lips side by side (full_port.gear_weights, GEAR_HEAD). The mouth's corners
    get their weights smoothed (rock_model.CORNER_SMOOTH): AEW faces had a hard hinge there;
  * the hair (one AEW mesh: hair and beard cards) -> M_Hair (hair_ALPHA) and M_Beard (beard_ALPHA) per card: a card
    whose root sits on skin weighted to AEW's jaw / cheek / lip / chin bones is beard;
  * gear -> M_<Name>, its slot name kept (rock_fbx.DROP_MESHES may name either: PortIT's unticked list sends slots);
  * bones: H_Neck_00 -> H_Neck_tw, H_Forearm1/2_<S> -> H_Elbow_<S>_tw01 / tw02 (2K's twist helpers), J_<finger>F3_<S>_end -> _stub, the rest as
    they are (rock_model folds unknown ones into their parent).
"""
import os
import re
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [os.path.join(os.path.dirname(HERE), 'portit'), os.path.join(os.path.dirname(HERE), 'readers')]

AX = np.array([[1.0, 0, 0], [0, 0, 1.0], [0, 1.0, 0]])   # AEW (x, y, z) -> rock_fbx (x, z, y)
RENAME = {'H_Neck_00': 'H_Neck_tw'}
for _s in 'LR':
    RENAME['H_Forearm1_' + _s] = 'H_Elbow_%s_tw01' % _s
    RENAME['H_Forearm2_' + _s] = 'H_Elbow_%s_tw02' % _s
CROP_PAD = 16          # (px around the head's part of the skin sheet: the mip / filter edge)
BEARD_AHEAD_CM = 4.0   # (a beard card's middle at least this far ahead of J_Head)
BEARD_BONES = re.compile(r'(Jaw|Cheek|Lip|Chin|Mouth)', re.I)



INNER_DARK_CM = 1.5   # (the inner lips: this deep behind their edge (cm) at their darkest)
INNER_FLOOR = 0.15
AO_FLOOR = 0.12   # (the darkest the occlusion makes a texel: the back of the mouth)


def bake_ao(col, orms):
    """colour x AEW's ambient occlusion (orms red) - SvR has no occlusion: the inside of the mouth drew lit, a flat
    pink wall (2026-11-09)"""
    from PIL import Image
    col = col.convert('RGBA')
    ao = np.asarray(orms.convert('RGBA').getchannel('R').resize(col.size, Image.BILINEAR)).astype(np.float64) / 255
    a = np.asarray(col).astype(np.float64)
    a[..., :3] *= (AO_FLOOR + (1 - AO_FLOOR) * ao)[..., None]
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8), 'RGBA')


def _mouth_piece(m, eye, iod):
    """-> per triangle: True on the head's own inside-of-the-mouth piece (a separate piece, small, its middle under
    the eyes by 0.6 - 2.2 eye distances, near the middle line), or None"""
    comp = _components(m['tris'], len(m['verts']))
    tc = comp[m['tris'][:, 0]]
    best = None
    for r in set(comp.tolist()):
        sel = comp == r
        if sel.sum() > 0.25 * len(sel):
            continue
        c = m['verts'][sel].mean(0) - eye
        if 0.6 * iod < -c[1] < 2.2 * iod and abs(c[0]) < 0.5 * iod:
            if best is None or sel.sum() > (comp == best).sum():
                best = r
    return None if best is None else tc == best

def _components(tris, n):
    parent = np.arange(n)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    for a, b, c in tris:
        for x, y in ((a, b), (b, c)):
            rx, ry = find(x), find(y)
            if rx != ry:
                parent[rx] = ry
    return np.array([find(i) for i in range(n)])


def _sub(m, tri_sel):
    """a part of a mesh dict: the triangles tri_sel and their vertices"""
    T = m['tris'][tri_sel]
    used = np.unique(T)
    remap = -np.ones(len(m['verts']), int)
    remap[used] = np.arange(len(used))
    out = dict(m, tris=remap[T], verts=m['verts'][used], normals=m['normals'][used], uv=m['uv'][used],
               weights={b: w[used] for b, w in m['weights'].items() if w[used].max() > 0})
    for k in ('aew_w', 'lip_src'):
        if k in m:
            out[k] = m[k][used]
    return out


class Bridge:
    def __init__(self, paks, folder, work, log=print):
        import source_aew
        self.log = log
        self.src = source_aew.load(paks, folder)
        self.folder = folder
        self.char = os.path.join(work, 'aew_char', folder)
        self.attire = 'Attire'
        self.texdir = os.path.join(self.char, self.attire, 'Textures')
        os.makedirs(self.texdir, exist_ok=True)
        os.makedirs(os.path.join(self.char, 'BaseModel', 'Textures'), exist_ok=True)
        self.female = bool(self.src.get('female'))
        self._make()

    # ---- the pieces, once
    def _tex(self, tname, me, ao=False):
        """the mesh's colour / normal images -> <tname>_color.png / _nrm.png -> 'textures/...' or None; ao: the
        occlusion (orms red) baked into the colour"""
        imgs = me.images or {}
        out = None
        for role, suffix in (('color', '_color'), ('normal', '_nrm')):
            im = imgs.get(role)
            if im is None:
                continue
            path = os.path.join(self.texdir, tname + suffix + '.png')
            if not os.path.exists(path):
                im = im.convert('RGBA')
                if ao and role == 'color' and imgs.get('orms') is not None:
                    im = bake_ao(im, imgs['orms'])
                im.save(path)
            if role == 'color':
                out = path
        return out

    def _tex_crop(self, tname, me, uv):
        """the head's part of AEW's skin sheet (head and body share one 4096 sheet; the face used ~1600 x 1200 of it -
        the whole sheet in SvR's head cell halved the face) -> <tname>_color / _nrm.png cut to the part's texture
        box (+ CROP_PAD px), uv moved onto it in place -> the colour path"""
        imgs = me.images or {}
        col = imgs.get('color')
        if col is None:
            return None
        W, H = col.size
        rows = 1.0 - uv[:, 1]                                  # (rock_fbx's uv: v up; the image's rows go down)
        x0 = max(0, int(np.floor(uv[:, 0].min() * W)) - CROP_PAD)
        x1 = min(W, int(np.ceil(uv[:, 0].max() * W)) + CROP_PAD)
        y0 = max(0, int(np.floor(rows.min() * H)) - CROP_PAD)
        y1 = min(H, int(np.ceil(rows.max() * H)) + CROP_PAD)
        out = None
        for role, suffix in (('color', '_color'), ('normal', '_nrm')):
            im = imgs.get(role)
            if im is None:
                continue
            k = (im.size[0] / W, im.size[1] / H)
            path = os.path.join(self.texdir, tname + suffix + '.png')
            im.convert('RGBA').crop((int(x0 * k[0]), int(y0 * k[1]), int(x1 * k[0]), int(y1 * k[1]))).save(path)
            if role == 'color':
                out = path
        self._crop = (x0, y0, x1 - x0, y1 - y0, W, H)
        uv[:, 0] = (uv[:, 0] * W - x0) / (x1 - x0)
        uv[:, 1] = 1.0 - (rows * H - y0) / (y1 - y0)
        return out

    def _shade_inner(self, inner):
        """the inner lips' part of the head texture darkened as a mouth is: by AEW's occlusion (orms red) where the
        skin has it, and by depth behind the lips' edge everywhere (Penta's skin map has no occlusion there: his inner
        lips drew bright skin-orange in the open mouth, 2026-11-09) - the darker of the two"""
        from PIL import Image, ImageDraw
        if not inner['texture']:
            return
        im = Image.open(inner['texture']).convert('RGBA')
        W, H = im.size
        uv, V, T = inner['uv'], inner['verts'], inner['tris']
        # depth: behind the piece's front (+Z is the face's front) -> 1 at the lips' edge, INNER_FLOOR deep inside
        depth = V[:, 2].max() - V[:, 2]
        f = np.clip(1.0 - depth / INNER_DARK_CM, INNER_FLOOR, 1.0)
        mask = Image.new('L', (W, H), 255)
        dr = ImageDraw.Draw(mask)
        px = np.stack([uv[:, 0] * W, (1 - uv[:, 1]) * H], 1)
        for t in T:
            dr.polygon([tuple(px[k]) for k in t], fill=int(255 * f[t].mean()))
        fac = np.asarray(mask).astype(np.float64) / 255
        me = getattr(self, '_skin_me', None)
        if me is not None and (me.images or {}).get('orms') is not None and hasattr(self, '_crop'):
            cx, cy, cw, ch, SW, SH = self._crop
            orms = me.images['orms'].convert('RGBA')
            k = (orms.size[0] / SW, orms.size[1] / SH)
            o = orms.crop((int(cx * k[0]), int(cy * k[1]), int((cx + cw) * k[0]), int((cy + ch) * k[1])))
            ao = np.asarray(o.getchannel('R').resize((W, H), Image.BILINEAR)).astype(np.float64) / 255
            box = np.asarray(mask) < 255
            fac = np.where(box, np.minimum(fac, AO_FLOOR + (1 - AO_FLOOR) * ao), fac)
        a = np.asarray(im).astype(np.float64)
        a[..., :3] *= fac[..., None]
        Image.fromarray(np.clip(a, 0, 255).astype(np.uint8), 'RGBA').save(inner['texture'])

    def _make(self):
        src = self.src
        bnames = [b['name'] for b in src['bones']]
        P = np.array([b['world'][3, :3] for b in src['bones']])          # (row-vector world: translation in row 3)
        self.bones = []
        for i, b in enumerate(src['bones']):
            w = np.eye(4)
            w[:3, 3] = AX @ P[i]
            par = b['parent'] if b['parent'] is not None and b['parent'] >= 0 else None
            nm = RENAME.get(b['name'], re.sub(r'^(J_\w+F3_[LR])_end$', r'\1_stub', b['name']))   # (finger tips: 2K's _stub)
            self.bones.append(dict(name=nm, src=nm, parent=par, world=w))
        # J_Head and everything under it
        kids = {}
        for i, b in enumerate(src['bones']):
            if b['parent'] is not None and b['parent'] >= 0:
                kids.setdefault(b['parent'], []).append(i)
        head = set()
        stack = [bnames.index('J_Head')] if 'J_Head' in bnames else []
        while stack:
            i = stack.pop()
            head.add(i)
            stack += kids.get(i, [])
        beardb = {i for i, n in enumerate(bnames) if BEARD_BONES.search(n) and i in head}
        meshes = []
        self._names = set()
        for me in src['meshes']:
            V = np.asarray(me.positions, float) @ AX.T
            N = np.asarray(me.normals, float) @ AX.T
            uv = np.asarray(me.uvs, float).copy()
            uv[:, 1] = 1.0 - uv[:, 1]
            T = np.asarray(me.tris, np.int64)
            w = {}
            bi, bw = np.asarray(me.bone_idx), np.asarray(me.bone_w, float)
            for c in range(bi.shape[1]):
                for b in np.unique(bi[bw[:, c] > 0, c]):
                    w[int(b)] = w.get(int(b), 0) + np.where(bi[:, c] == b, bw[:, c], 0.0)
            key = re.sub(r'^(mt|mi|s)_', '', me.material, flags=re.I)
            key = re.sub(r'_\d+$', '', key)
            base = dict(verts=V, normals=N, uv=uv, tris=T, weights=w, slot=me.material)
            if me.kind == 'head':
                # (each vertex's lip from AEW's own rig: weights on the lower lip / jaw bones against the upper
                #  lip's - the face transfer's sides, not a walk round the lips' edge)
                jw = sum((w.get(b, 0) for b in range(len(bnames)) if b in w and
                          re.search(r'(^J_Jaw$|Lip_[LRC]_b_|Jaw_)', bnames[b])), np.zeros(len(V)))
                uw = sum((w.get(b, 0) for b in range(len(bnames)) if b in w and re.search(r'Lip_[LRC]_u_', bnames[b])),
                         np.zeros(len(V)))
                base['lip_src'] = np.where((jw > uw) & (jw > 0.05), 1, np.where((uw >= jw) & (uw > 0.05), -1, 0))
                hw = sum((w.get(b, 0) for b in head), np.zeros(len(V)))
                th = hw[T].mean(1) > 0.5
                # (the body before the head, as 2K lists them: rock_model joins the head / body seam by giving the
                #  head's seam vertices the body's weights - only when the body came first. Head first, the seam
                #  was never joined: a crack opened along Adam Cole's neck in game, 2026-11-09)
                for nm, mat, sel in (('M_Body', 'body', ~th), ('M_Head', 'head', th)):
                    part = _sub(base, sel)
                    part.update(name=nm, material=mat, texture=self._tex_crop(mat, me, part['uv']) if nm == 'M_Head'
                                else self._tex(mat, me))
                    meshes.append(part)
                self._skin = (V, w)
                self._skin_me = me
            elif me.kind == 'teeth':
                # (AEW's own mouth: teeth, gums, tongue, the mouth's back - rigged to J_Head / J_Jaw / J_Tongue and made
                #  for these lips; the template's teeth sat behind AEW's inner lips, hidden - a dark hole, 2026-11-09)
                meshes.append(dict(base, name='M_AEWMouth', material='aewmouth', texture=self._tex('aewmouth', me, ao=True)))
            elif me.kind == 'eye':
                side = 'l' if re.search(r'_L(_|$)', me.material, re.I) else 'r'
                meshes.append(dict(base, name='M_EYE', material='eye_' + side, texture=self._tex('eye', me)))
            elif me.kind == 'lash':
                meshes.append(dict(base, name='M_TransB_eyelash', material='eyelash', texture=self._tex('eyelash', me)))
            elif me.kind == 'hair':
                meshes += self._hair(base, me, head, beardb)
            else:
                nm, mat = 'M_' + key[:1].upper() + key[1:], key.lower()
                k = 1
                while (nm + (str(k) if k > 1 else '')) in self._names:   # (two slots of one name: one file each)
                    k += 1
                nm, mat = nm + (str(k) if k > 1 else ''), mat + (str(k) if k > 1 else '')
                self._names.add(nm)
                meshes.append(dict(base, name=nm, material=mat, texture=self._tex(mat, me)))
        # the inside of the lips: AEW's head mesh has it as a piece of its own (joined to the lips only by vertices
        # at the same points) - to the face fit that closed the lips ('no mouth opening found'). It becomes
        # M_InnerMouth: the face's texture (it is on the skin sheet), and as gear near the head it takes the fitted
        # lips' weights (full_port.GEAR_HEAD) - a flat dark M_MouthBag hid the teeth
        hm = next((m for m in meshes if m['name'] == 'M_Head'), None)
        if hm is not None and 'J_Eye_L' in bnames and 'J_Eye_R' in bnames:
            eL, eR = AX @ P[bnames.index('J_Eye_L')], AX @ P[bnames.index('J_Eye_R')]
            bag = _mouth_piece(hm, (eL + eR) / 2, np.linalg.norm(eL - eR))
            if bag is not None:
                inner = _sub(hm, bag)
                inner.update(name='M_InnerMouth', material='head', texture=hm['texture'])
                # (its lower lip's face bones fold into the head in SvR: moved onto J_Jaw, so the lower inner lip
                #  is known as the jaw's - full_port.gear_weights keeps each side on its own lip's skin)
                if 'J_Jaw' in bnames:
                    jaw = bnames.index('J_Jaw')
                    wi = inner['weights']
                    for k in [k for k in wi if re.search(r'Lip_[LRC]_b_', bnames[k])]:
                        wi[jaw] = wi.get(jaw, 0) + wi.pop(k)
                rest = _sub(hm, ~bag)
                hm.clear()
                hm.update(rest)
                meshes.append(inner)
                self._shade_inner(inner)
                self.log('AEW: the inside of the lips (%d vertices) -> M_InnerMouth' % len(inner['verts']))
        # winding: rock_fbx's (2K) triangles face outward (rock_model flips them for SvR) - AEW's swap of Y and Z
        # mirrored them: each mesh's triangles agree with its own normals again
        for m in meshes:
            T, V, N = m['tris'], m['verts'], m['normals']
            if not len(T):
                continue
            fn = np.cross(V[T[:, 1]] - V[T[:, 0]], V[T[:, 2]] - V[T[:, 0]])
            agree = np.einsum('ij,ij->i', fn, N[T].mean(1))
            if (agree < 0).sum() > (agree > 0).sum():
                m['tris'] = T[:, [0, 2, 1]]
        self.meshes = meshes
        self.log('AEW: %d meshes for the 2K pipeline - %s' % (len(meshes), ', '.join(m['name'] for m in meshes)))

    def _hair(self, base, me, head, beardb):
        """the hair mesh -> scalp cards (M_Hair) and beard cards (M_Beard): a beard card hangs in front of the face
        below the eyes (its middle ahead of J_Head by BEARD_AHEAD_CM, under the eye line) and grows from skin
        weighted to the jaw / cheek / lip / chin bones (Adam Cole's side locks reach his jaw too: they stay hair)"""
        comp = _components(base['tris'], len(base['verts']))
        SV, SW = getattr(self, '_skin', (None, None))
        P = {b['name']: b['world'][:3, 3] for b in self.bones}
        eye_y = (P['J_Eye_L'][1] + P['J_Eye_R'][1]) / 2 if 'J_Eye_L' in P and 'J_Eye_R' in P else None
        hz = P['J_Head'][2] if 'J_Head' in P else None
        is_beard = {}
        V = base['verts']
        if SV is not None and eye_y is not None and not self.female:     # (women: no beard - Jade Cargill had 1)
            for r in set(comp.tolist()):
                vi = np.nonzero(comp == r)[0]
                c = V[vi].mean(0)
                if c[1] > eye_y - 1.0 or c[2] < hz + BEARD_AHEAD_CM:
                    continue
                best, sv = 1e18, 0
                for i0 in range(0, len(SV), 4096):     # (the card's nearest skin vertex, in chunks)
                    d = ((V[vi][:, None] - SV[None, i0:i0 + 4096]) ** 2).sum(-1)
                    k = np.unravel_index(np.argmin(d), d.shape)
                    if d[k] < best:
                        best, sv = d[k], i0 + k[1]
                bw = sum(SW[b][sv] for b in beardb if b in SW)
                is_beard[r] = bw > 0.3
        tri_beard = np.array([is_beard.get(comp[t[0]], False) for t in base['tris']], bool)
        self.log('AEW hair: %d cards, %d of them beard' % (len(set(comp.tolist())), sum(is_beard.values())))
        out = []
        for nm, mat, sel in (('M_Hair', 'hair', ~tri_beard), ('M_Beard', 'beard', tri_beard)):
            if sel.any():
                # (a second hair mesh - Bryan Danielson's beard slot, Jon Moxley's - gets names of its own: two
                #  M_Hair shared one texture file, the second drew with the first one's)
                k = 1
                while (nm + (str(k) if k > 1 else '')) in self._names:
                    k += 1
                sfx = str(k) if k > 1 else ''
                self._names.add(nm + sfx)
                part = _sub(base, sel)
                part.update(name=nm + sfx, material=mat + sfx + '_ALPHA', texture=self._tex(mat + sfx + '_ALPHA', me))
                out.append(part)
        return out

    # ---- rock_fbx.build's place
    def build(self, char, attire, out, want_bones=False):
        import copy
        import rock_fbx
        drop = set(rock_fbx.DROP_MESHES)
        meshes = [copy.deepcopy(m) for m in self.meshes if m['name'] not in drop and m.get('slot') not in drop]
        for m in meshes:
            m.pop('slot', None)
        report = ['%-18s %6d verts %6d tris  %-12s %s  %d bones' % (m['name'], len(m['verts']), len(m['tris']),
                                                                    m['material'], m['texture'], len(m['weights']))
                  for m in meshes]
        rock_fbx.hide_body(meshes, report)
        print('\n'.join(report))
        bones = copy.deepcopy(self.bones)
        return (meshes, bones) if want_bones else meshes
