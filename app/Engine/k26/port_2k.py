"""PortIT's 2K driver for the proven Rock / Brock pipeline (rock_model.build + rock_build + bigmodel_install).

What was hand-written per wrestler there (rock_model.CELLS / TARGET / SHINE / SOLID, brock_model's hair colours and
card counts) is filled in here from the wrestler's own meshes, with the values that pipeline tested in game:
  * cells: head 1024 x 1024, body 1024 x 512, eye / eyelash / the template's mouth where The Rock had them; the gear
    and hair cards packed into the free part of the 2048 x 1024 sheet, the bigger meshes first
  * targets (vertices kept): Brock's (body 3000, head 2700, shoes 1200 ...) for the classic 192-page window, scaled
    down until the build fits; gear by its share; hair / beard cards thinned to whole cards (brock_model.thin_cards)
  * shine / sweat: rock_model's tables (skin 10 + luma / 16, shoes 80 + luma / 3, other gear 40 + luma / 3, hair 20)
  * hair cards: brock_model.hair_colour - a flat colour (here: the mean of 2K's per-vertex hair colour) with the
    rai map's green as the cut-out
"""
import glob
import json
import os
import re
import subprocess
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [HERE, os.path.join(os.path.dirname(HERE), 'readers'), os.path.join(os.path.dirname(HERE), 'svr')]

SHEET_FREE = [(1024, 512, 1024, 256), (1024, 768, 512, 256), (1536, 768, 256, 64), (1536, 960, 256, 64)]
CARD_MATS = ('hair', 'combinations', 'beard', 'facial')


BRIDGE = [None]      # (an AEW Fight Forever wrestler: aew_bridge.Bridge stands in for rock_fbx.build - set by build)
LEAVE_OUT = set()   # (meshes the user unticked in PortIT: clothes / gear left out of the port - set by build)


def setup(char, attire, female=False, full=False):
    """fill rock_model's tables for this wrestler -> (R, info)"""
    import brock_model as BM       # (sets rock_fbx / rock_model globals for hair; reset below)
    import rock_fbx
    import rock_model as R
    from mcd_parse import Mcd
    probe = os.path.join(R.os.environ.get('PORTIT_WORK', HERE), '_probe')
    rock_fbx.DROP_MESHES = {'M_Teeth', 'M_Tongue'} | LEAVE_OUT   # (the mouth bag read too, as rock_model.build reads it)
    meshes, bones = rock_fbx.build(char, attire, probe, want_bones=True)
    rock_fbx.NOT_GEAR = {'M_Body', 'M_Head', 'M_EYE', 'M_Head_Caruncle', 'M_TransB_eyelash', 'M_transB_eyelash',
                         'M_Teeth', 'M_Tongue', 'M_MouthBag'} | {m['name'] for m in meshes if is_card(m)}
    # (the user's left-out clothes are dropped before the skin-under-gear test: the skin under them stays)
    R.DROP_MESHES = {'M_Teeth', 'M_Tongue'} | LEAVE_OUT | {m['name'] for m in meshes if m['name'].lower().startswith(('lambert', 'polysurface'))
                                                or 'eyeshadow' in m['name'].lower() or m['name'] == 'S_Hair'}
    # 2K's tongue / teeth under other names (2K25 Ricky Steamboat's tongue is 'vector': kept, it drew as a red block in
    # his mouth, 2026-11-07): an untextured mesh centred within 4 cm of the mouth bag is a mouth part - left out (the
    # template's mouth inside goes in, as for every port)
    bag = next((m for m in meshes if m['name'] == 'M_MouthBag'), None)
    if bag is not None:
        c0 = bag['verts'].mean(0)
        for m in meshes:
            if m is bag or m['texture'] or m['name'] in R.DROP_MESHES or len(m['verts']) > 8000:
                continue
            if np.linalg.norm(m['verts'].mean(0) - c0) < 4.0:
                R.DROP_MESHES.add(m['name'])
                rock_fbx.NOT_GEAR = rock_fbx.NOT_GEAR | {m['name']}
                print('%s: an untextured mesh in the mouth (2K tongue / teeth) - left out' % m['name'])
    R.KIND = {}
    for m in meshes:
        n = m['name'].lower()
        if n == 'm_eye':
            R.KIND[m['name']] = 'eye'
        elif 'eyelash' in n:
            R.KIND[m['name']] = 'lash'
    # texture names (rock_model: the material's base, eye_l / eye_r -> eye)
    tnames = {}
    for m in meshes:
        if m['name'] in R.DROP_MESHES:
            continue
        base = m['material'].split(':')[0]
        tnames.setdefault({'eye_l': 'eye', 'eye_r': 'eye'}.get(base, base), []).append(m)
    cells = {'head': (0, 0, 1024, 1024), 'body': (1024, 0, 1024, 512), 'eye': (1792, 768, 256, 128),
             'eyelash': (1792, 896, 256, 128), 'mouthbag': R.CELLS['mouthbag']}
    rest = [t for t in tnames if t not in cells]
    rest.sort(key=lambda t: -sum(len(m['verts']) for m in tnames[t]))
    free = list(SHEET_FREE)
    for t in rest:
        size = 256 if sum(len(m['verts']) for m in tnames[t]) > 1500 or is_card(tnames[t][0]) else 128
        while True:
            fit = None
            for k, (x, y, w, h) in enumerate(free):
                if w >= size and h >= min(size, h):
                    fit = k
                    break
            if fit is not None or size <= 32:
                break
            size //= 2
        if fit is None:
            if full:        # (the full route lays the sheet out again: full_port.layout - a placeholder here)
                cells[t] = (0, 0, 32, 32)
                continue
            raise RuntimeError('no room in the texture sheet for ' + t)
        x, y, w, h = free.pop(fit)
        ch = min(size, h)
        cells[t] = (x, y, size, ch)
        if w - size > 0:
            free.insert(fit, (x + size, y, w - size, h))
        if h - ch > 0:
            free.append((x, y + ch, size, h - ch))
    R.CELLS = cells
    R.CUTOUT = {t for t, ms in tnames.items() if is_card(ms[0])}
    R.SHINE = {'head': (10, 1 / 16), 'body': (10, 1 / 16), 'eye': (52, 0), 'eyelash': (0, 0), 'eye_caruncle': (40, 0)}
    for t in tnames:
        if t not in R.SHINE:
            R.SHINE[t] = (20, 0) if t in R.CUTOUT else (80, 1 / 3) if 'shoe' in t or 'boot' in t else (40, 1 / 3)
    R.SOLID = dict(R.SOLID, eye_caruncle=(176, 104, 98))
    # hair card colour maps: flat colour from 2K's per-vertex hair colour, the rai map's green as the cut-out
    cards = {}
    for t in R.CUTOUT:
        ms = tnames[t]
        rai = R.find_tex(char, attire_dir(char, attire), re.sub(r'(?i)_alpha$', '', t), '_rai') or \
              R.find_tex(char, attire_dir(char, attire), t, '_rai')
        rgb = hair_rgb(char, attire, ms[0]['name'])
        if rai:
            dst = os.path.join(os.path.dirname(rai), t + '_color.png')
            BM.hair_colour(rai, dst, rgb)
        cards[t] = rgb
    info = {'meshes': {m['name']: len(m['verts']) for m in meshes}, 'cells': cells, 'cards': cards,
            'cutout': sorted(R.CUTOUT), '_meshes': meshes, '_tnames': tnames}
    return R, info


def is_card(m):
    mat = m['material'].lower()
    return mat.endswith('_alpha') or any(mat.startswith(c) for c in CARD_MATS)


def attire_dir(char, attire):
    p = os.path.join(char, attire)
    return os.path.relpath(os.path.dirname(p), char) if os.path.isfile(p) else attire


def hair_rgb(char, attire, mesh_name):
    """2K hair has no colour map: its colour is per vertex (the COLOR stream) - their mean"""
    if BRIDGE[0] is not None:      # (AEW: the hair's own colour map - the median of its opaque texels, made linear)
        from PIL import Image
        p = os.path.join(BRIDGE[0].texdir, 'hair_ALPHA_color.png')
        if os.path.exists(p):
            a = np.asarray(Image.open(p).convert('RGBA')).reshape(-1, 4)
            a = a[a[:, 3] > 128][:, :3] / 255.0
            if len(a):
                c = np.median(a, axis=0)
                lin = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
                return tuple(int(round(255 * x)) for x in lin)
        return (90, 70, 50)
    import rock_fbx
    from mcd_parse import Mcd
    m = Mcd(rock_fbx.attire_mcd(char, attire))
    for k, mb in enumerate(m.meshes):
        if mb['material'] == mesh_name:
            c = m.stream(k, 'COLOR')
            if c is not None and len(c):
                return tuple(int(x) for x in np.median(c[:, :3], axis=0))
    return (90, 70, 50)


def targets(info, scale):
    """vertices kept per mesh (Brock's tested numbers for the 192-page window, x scale)"""
    T = {}
    gear_total = 0
    for n, v in info['meshes'].items():
        low = n.lower()
        if low == 'm_body':
            T[n] = 3000
        elif low == 'm_head':
            T[n] = 2700
        elif low in ('m_eye',) or 'eyelash' in low or low in ('m_teeth', 'm_tongue'):
            continue
        elif low == 'm_mouthbag':
            T[n] = 220
        elif 'caruncle' in low:
            T[n] = 160
        else:
            gear_total += v
    gear_room = 3000
    for n, v in info['meshes'].items():
        if n not in T and n.lower() not in ('m_eye',) and 'eyelash' not in n.lower():
            T[n] = max(200, int(gear_room * v / max(gear_total, 1)))
    return {n: max(150, int(t * scale)) for n, t in T.items()}


FULL_BUDGET = 1500   # (the big-model memory, game 2.1.6+: full detail + a texture pack - full_port.py)


def card_meshes(info):
    """the hair / beard card meshes (their texture is a cut-out one)"""
    cut = set(info['cutout'])
    return [n for n in info['meshes']
            if any(n.lower().replace('m_', '') in t.lower() or t.lower().startswith(n.lower().replace('m_', ''))
                   for t in cut)]


def build(char, attire, out, game, budget=190, template=None, log=print, female=False, pack_prefix='portit_hd', leave_out=(),
          bridge=None):
    """-> pac, builder report, report lines, info (+ info['pack']: the texture pack files, info['checks']).
    bridge: an aew_bridge.Bridge (AEW Fight Forever) - its meshes / bones stand in for the 2K .mcd reader"""
    import rock_fbx
    plain_build = rock_fbx.build
    BRIDGE[0] = bridge
    import full_port as FP
    FP.GEAR_HEAD[0] = bridge is not None
    import rock_model
    rock_model.TEMPLATE_MOUTH = bridge is None
    rock_model.NORMAL_FLIP_G = bridge is None      # (2K26 green flipped to SvR's; AEW's matches already)
    rock_model.CORNER_SMOOTH = 14   # (the mouth corners' hard weight edge: AEW first, 2K26 too - the user asked, 2026-11-09)
    if bridge is not None:
        rock_fbx.build = bridge.build
        char, attire = bridge.char, bridge.attire
    try:
        return _build(char, attire, out, game, budget, template, log, female, pack_prefix, leave_out)
    finally:
        rock_fbx.build = plain_build
        BRIDGE[0] = None


def _build(char, attire, out, game, budget, template, log, female, pack_prefix, leave_out):
    import full_port as FP
    os.environ['PORTIT_GAME'] = game
    os.environ['PORTIT_WORK'] = out
    os.makedirs(out, exist_ok=True)
    FP.install(sys.modules[__name__], log)
    full = budget >= FULL_BUDGET
    import time
    t0 = time.time()
    FP.QUICK_READ[0] = True
    try:
        LEAVE_OUT.clear()
        LEAVE_OUT.update(n for n in leave_out if n)
        if LEAVE_OUT:
            log('left out (unticked): ' + ', '.join(sorted(LEAVE_OUT)))
        R, info = setup(char, attire, female, full)
    finally:
        FP.QUICK_READ[0] = False
    log('read the 2K model: %.0f s' % (time.time() - t0))
    import brock_model as BM
    template = template or os.path.join(game, 'pac', 'ch', 'ch125.pac')
    cards = card_meshes(info)
    pac = os.path.join(out, 'ch.pac')
    flat = FP.flat_cards(info['_meshes'], set(cards), log)   # (lying flat on the skin: painted on the head)
    model = os.path.join(out, 'model.json')
    if full:
        skip = {t for t, ms in info['_tnames'].items() if all(m['name'] in flat for m in ms)}
        cells = FP.layout(R, char, attire_dir(char, attire), skip, log)
        log('texture sheet 8192 x 4096: ' + ', '.join('%s %dx%d' % (t, c[2], c[3]) for t, c in cells.items()))
        json.dump({'cells': R.CELLS, 'mouth': R.TMPL_MOUTH_CELL}, open(os.path.join(out, 'cells.json'), 'w'))
        R.TARGET = {}                          # (no decimation: every 2K vertex)
        for n in cards:
            R.KIND[n] = 'hair'                 # (the cards in the hair model, child 0x2710)
    # (the full route: a model must fit its wrestler's vertex window - a model past it crashed the draw, 929-page
    #  Rey Mysterio in a 2048 KB window, 2026-11-07; FP.window_pages reads the DLL's bigmodel.log)
    cap = FP.window_pages(game, log) if full else 0
    scale = 1.0
    for attempt in range(6):
        card_t = {}
        if not full:
            T = targets(info, scale)
            for n in list(T):
                if n in cards:
                    card_t[n] = max(300, int(1300 * scale))
                    del T[n]
            R.TARGET = T
        elif scale < 1.0:
            # (full detail over the game's vertex window: every mesh and the cards kept at `scale` - just enough)
            R.TARGET = {n: max(150, int(v * scale)) for n, v in info['meshes'].items() if n not in cards}
            for n in cards:
                card_t[n] = max(300, int(info['meshes'][n] * scale))

        def prepare(meshes, bones, card_t=card_t):
            head = next(i for i, b in enumerate(bones) if b['src'] == 'J_Head')
            for m in meshes:
                if not m['weights']:
                    m['weights'] = {head: np.ones(len(m['verts']))}
                if m['name'] in card_t:
                    nc, kc = BM.thin_cards(m, card_t[m['name']])
                    log('%s: cards %d -> %d' % (m['name'], nc, kc))
            return [m for m in meshes if m['name'] not in flat]
        R.PREPARE = prepare
        t0 = time.time()
        spec, report = R.build(char, attire, out, template)
        log('model built: %.0f s' % (time.time() - t0))
        t0 = time.time()
        FP.fix_model(model, [n for n in cards if n not in flat], template, log)
        log('weights fixed: %.0f s' % (time.time() - t0))
        t0 = time.time()
        rep = FP.build_pac(model, pac, full)
        log('ch.pac built: %.0f s' % (time.time() - t0))
        if not rep.get('ok'):
            raise RuntimeError(rep.get('error') or 'build failed')
        pages = rep['attires'][0]['pages']
        if full and pages > cap and attempt < 5:
            scale *= cap / pages * 0.97
            log('build %d: %d pages - over the game vertex window (%d pages a wrestler), simplifying (x%.2f)'
                % (attempt + 1, pages, cap, scale))
            continue
        if not full and pages > budget - 2 and attempt < 5:
            scale *= (budget - 2) / pages * 0.97
            log('build %d: %d pages - over %d, simplifying more (x%.2f)' % (attempt + 1, pages, budget, scale))
            continue
        log('build %d: %d pages (%s)' % (attempt + 1, pages, 'full detail' if full else 'window %d' % budget))
        # (the built file against the game's hard limits - never hand out a model that crashes)
        heavy, most = FP.heavy_meshes(pac)
        if heavy and attempt < 5:
            # (a built mesh over the indices that have run in game: the split size lowered and built again - the
            #  builder itself cuts meshes at 20 bones, so a built mesh can't be matched to a model.json mesh)
            FP.MAX_MESH_TRIS = max(2000, int(min(FP.MAX_MESH_TRIS, max(len(m['triangles']) for m in
                                   json.load(open(model))['attires'][0]['meshes'])) * 0.6))
            log('build %d: %d mesh(es) over %d indices - meshes over %d triangles split, again'
                % (attempt + 1, len(heavy), FP.SAFE_INDICES, FP.MAX_MESH_TRIS))
            continue
        if heavy:
            report.append('warning: %d mesh(es) still over %d indices (the most that has run in game; the game takes 65535)'
                          % (len(heavy), FP.SAFE_INDICES))
            log(report[-1])
        if most > FP.STOCK_MESHES:
            report.append('warning: a part has %d meshes (stock up to %d)' % (most, FP.STOCK_MESHES))
            log(report[-1])
        probs = FP.pac_limits(pac, cap if full else budget)
        if any('indices' in p_ for p_ in probs) and FP.MAX_MESH_TRIS > 3000 and attempt < 5:
            FP.MAX_MESH_TRIS //= 2
            log('build %d: a mesh over 65535 indices - split at %d triangles, again' % (attempt + 1, FP.MAX_MESH_TRIS))
            continue
        if probs:
            raise RuntimeError('the built model breaks the game limits (it would crash): ' + '; '.join(probs))
        report.append('game limits: every mesh under 65535 vertices / indices, every part under %d pages, %d pages '
                      'in all (the window takes %d)' % (FP.PART_PAGES, pages, cap if full else budget))
        face_over, gear_over, notes = FP.checks(model, template)
        info['checks'] = {'face_over': face_over, 'gear_over': gear_over, 'notes': notes}
        report.append('checks: %d face vertices swing past the stock head, %d gear vertices drift from the skin%s'
                      % (face_over, gear_over, ('' if not notes else ' - ' + '; '.join(notes[:6]))))
        log(report[-1])
        if LEAVE_OUT:
            report.append('left out by you: ' + ', '.join(sorted(LEAVE_OUT)))
        if flat:
            report.append('left out (painted on the head texture): ' + ', '.join(sorted(flat)))
        info['pack'] = []
        if full:
            cut_cells = [R.CELLS[t] for t in info['cutout'] if t in R.CELLS]
            info['pack'] = FP.make_pack(out, pac, os.path.join(out, 'texture_pack'), pack_prefix, cut_cells, log)
        return pac, rep, report, info
    raise RuntimeError('could not fit the model')
