"""PortIT's engine protocol (the same shape as AnimIT's porter): PortIT.exe writes request.json, runs
    python -I -B worker.py request.json response.json
and reads response.json {ok, ...} / {ok: false, error}. Progress lines go to stdout.

Commands:
  list     {source: '2k26' | 'aew', path}                 -> wrestlers [{id, name, female, attires?}]
  inspect  {source, path, id, attire?}                     -> meshes [{name, kind, verts, variant, textures}]
  port     {source, path, id, attire?, game, out, female, budget, face, mod_id, mod_name, template?, leave_out?: [mesh], hd_size?: 8192|4096|2048}
                                                           -> {out, pac, pages, report[], preview}
  preview  {pac, out}                                      -> {preview}
  source_preview {source, path, id, attire?, leave_out?}   -> {preview}   (the 2K source drawn before porting)
  bigmodel {game, action: status | install | remove}     -> {installed: portit | other | none, active, version, note}
  install  {mod_dir, game, mod_id}                         -> {installed, pack?, pack_enabled?}   (Mods/Superstars/<id>, old
                                                              one backed up; the texture_pack folder into the game's enabled texture pack)
Game files are only read; output goes to a new folder (install copies into the game only when asked).
"""
import json
import os
import shutil
import sys
import time
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [os.path.join(HERE, x) for x in ('readers', 'portit', 'svr')]


def log(*a):
    print(*a, flush=True)


def list_2k(path):
    import source_2k
    out = []
    for root, dirs, files in os.walk(path):
        if os.path.basename(root).lower() == 'basemodel' and any(source_2k.is_base_model(f) for f in files):
            folder = os.path.dirname(root)
            base, att = source_2k.find_wrestler(folder)
            name = os.path.basename(folder)
            parts = name.split('_')
            nice = ' '.join(parts[1:] if parts[0].isdigit() else parts)
            # (PortIT 2026-11-10: by 2K's own data - the women's body template; the folder name too)
            import mcd_parse
            female = 'female' in folder.lower() or bool(base and mcd_parse.looks_female(base))
            out.append({'id': folder, 'name': nice, 'female': female,
                        'attires': [os.path.relpath(a, folder) for a in att], 'group': os.path.basename(os.path.dirname(folder))})
            dirs[:] = []
            continue
        # (PortIT 2026-11-10: WWE 2K19 - 2K22 character packs, chNNNNN.pac - one entry per pack, unpacked when chosen)
        import jboy_2k
        packs = sorted(f for f in files if f.lower().endswith('.pac') and jboy_2k.is_character_pack(f)
                       and jboy_2k.is_pac(os.path.join(root, f)))
        for f in packs:
            pac = os.path.join(root, f)
            name = os.path.basename(root)
            nice = name + (' (%s)' % os.path.splitext(f)[0] if len(packs) > 1 else '')
            cached = jboy_2k.cached(pac)
            import mcd_parse
            female = bool(cached and mcd_parse.looks_female(os.path.join(cached, 'BaseModel', 'baseModel.mdl')))
            out.append({'id': PAC_PREFIX + pac, 'name': nice, 'female': female, 'attires': ['Attire'],
                        'group': os.path.basename(os.path.dirname(root))})
    return sorted(out, key=lambda r: r['name'].lower())


def port_2k_attires(folder):
    import source_2k
    _, att = source_2k.find_wrestler(folder)
    return [os.path.relpath(a, folder) for a in att]


# ---- WWE 2K26 game install: its bakedfile*.cak archives, read by PortIT.exe --cake (Core/CakeSource.cs, BakeIT.Core)
CAK_PREFIX = 'cak:'
CAK_CACHE = os.path.join(os.environ.get('LOCALAPPDATA', HERE), 'PortIT', 'k26cache')


def is_game_folder(path):
    return bool(path) and os.path.isdir(path) and any(f.lower().startswith('bakedfile') and f.lower().endswith('.cak')
                                                       for f in os.listdir(path))


def cake(cmd, game, **kw):
    """PortIT.exe --cake: the 2K26 archives (index / extract)"""
    import subprocess
    exe = os.environ.get('PORTIT_EXE') or ''
    if not os.path.isfile(exe):
        for c in (os.path.join(os.path.dirname(HERE), 'PortIT.exe'),
                  os.path.join(os.path.dirname(HERE), 'bin', 'Release', 'net10.0', 'PortIT.exe')):
            if os.path.isfile(c):
                exe = c
                break
    if not os.path.isfile(exe):
        raise RuntimeError('PortIT.exe not found (it reads the 2K26 archives)')
    os.makedirs(CAK_CACHE, exist_ok=True)
    job = os.path.join(CAK_CACHE, 'job_%d' % os.getpid())
    req = dict(kw, cmd=cmd, game=game, cache=CAK_CACHE)
    json.dump(req, open(job + '_req.json', 'w', encoding='utf-8'))
    if os.path.exists(job + '_resp.json'):
        os.remove(job + '_resp.json')
    subprocess.run([exe, '--cake', job + '_req.json', job + '_resp.json'], check=False)
    if not os.path.exists(job + '_resp.json'):
        raise RuntimeError('reading the 2K26 archives failed (no answer from PortIT.exe --cake)')
    r = json.load(open(job + '_resp.json', encoding='utf-8'))
    if not r.get('ok'):
        raise RuntimeError('2K26 archives: ' + (r.get('error') or 'failed'))
    return r


def list_game(game):
    """the wrestlers in a 2K26 install: id 'cak:<Characters folder>', the attires as their .mcd paths"""
    log('reading the 2K26 archives (the first time takes a little longer)...')
    ws = cake('index', game)['wrestlers']
    names = {}
    for w in ws:
        names[w['name'].lower()] = names.get(w['name'].lower(), 0) + 1
    out = []
    for w in ws:
        name = w['name']
        if names[name.lower()] > 1:          # (several models of one name: the 2K number tells them apart)
            name += ' #' + w['folder'].split('_')[0]
        if all('mattel' in a.lower() for a in w['attires']):
            name += ' (Mattel figure)'       # (toy-like faces: the face rig needs a human face - see resolve_cak)
        out.append({'id': CAK_PREFIX + w['folder'], 'name': name, 'female': False, 'attires': w['attires'],
                    'group': 'WWE 2K26 (game)'})
    return sorted(out, key=lambda r: r['name'].lower())


PAC_PREFIX = 'pac:'


def resolve_cak(req):
    """a game wrestler (id 'cak:...'): his base model + the chosen attire extracted (cached) -> req['id'] = that folder,
    as a 2K26 mod folder. A WWE 2K19 - 2K22 pack (id 'pac:<chNNNNN.pac>'): unpacked (cached) the same way."""
    if req.get('source') == '2k26' and str(req.get('id', '')).startswith(PAC_PREFIX):
        import jboy_2k
        pac = req['id'][len(PAC_PREFIX):]
        name = os.path.basename(os.path.dirname(pac)) + ' ' + os.path.splitext(os.path.basename(pac))[0]
        return dict(req, id=jboy_2k.extract(pac, name, log=log), attire=os.path.join('Attire', 'Attire.mdl'))
    if req.get('source') != '2k26' or not str(req.get('id', '')).startswith(CAK_PREFIX):
        return req
    folder = req['id'][len(CAK_PREFIX):]
    att = req.get('attire') or ''
    if not att:
        w = next((w for w in cake('index', req['path'])['wrestlers'] if w['folder'] == folder), None)
        if not w:
            raise RuntimeError(folder + ': not in the 2K26 archives')
        att = next((a for a in w['attires'] if 'mattel' not in a.lower()), w['attires'][0])   # (not the action figure)
    log('extracting %s / %s from the 2K26 archives...' % (folder, att.replace(chr(92), '/').split('/')[0]))
    path = cake('extract', req['path'], folder=folder, attire=att)['path']
    return dict(req, id=path, attire=att)


def load_source(req):
    req = resolve_cak(req)
    if req['source'] == '2k26':
        import source_2k
        att = req.get('attire')
        return source_2k.load(req['id'], os.path.join(req['id'], att) if att else None)
    import source_aew
    return source_aew.load(req['path'], req['id'])


def model_file_folder(path):
    """one chosen model file (people's request 2026-11-10: open just one model, without a whole game or mod folder):
    a 2K wrestler's .mcd / .mdl -> its wrestler folder (the one holding BaseModel); a .pac -> None (listed alone)"""
    d = os.path.dirname(os.path.abspath(path))
    for _ in range(4):
        if os.path.isdir(os.path.join(d, 'BaseModel')):
            return d
        d = os.path.dirname(d)
    return None


def cmd_list(req):
    if req['source'] == '2k26' and os.path.isfile(req['path']):
        p = req['path']
        if p.lower().endswith('.pac'):
            import jboy_2k
            if not jboy_2k.is_pac(p):
                raise ValueError('%s is not a WWE 2K19 - 2K22 character pack' % os.path.basename(p))
            cached = jboy_2k.cached(p)
            import mcd_parse
            return {'wrestlers': [{'id': PAC_PREFIX + p, 'name': os.path.basename(os.path.dirname(p)) + ' (%s)' % os.path.basename(p)[:-4],
                                   'female': bool(cached and mcd_parse.looks_female(os.path.join(cached, 'BaseModel', 'baseModel.mdl'))),
                                   'attires': ['Attire'], 'group': 'chosen file'}]}
        folder = model_file_folder(p)
        if not folder:
            raise ValueError('No BaseModel folder beside %s: choose a wrestler\'s baseModel / attire model file' % p)
        return {'wrestlers': list_2k(folder)}
    if req['source'] == '2k26':
        if is_game_folder(req['path']):
            return {'wrestlers': list_game(req['path'])}
        return {'wrestlers': list_2k(req['path'])}
    import source_aew
    return {'wrestlers': source_aew.roster(req['path'])}


PREVIEWS = os.path.join(os.environ.get('LOCALAPPDATA', HERE), 'PortIT', 'previews')


def source_preview(req, src):
    """the source drawn before porting (portit/preview.render_source; the unticked meshes left out) -> png path"""
    if req.get('source') not in ('2k26', 'aew'):
        return None
    import re
    import preview
    gone = {n.lower() for n in (req.get('leave_out') or [])}
    view = dict(src, meshes=[m for m in src['meshes'] if m.material.lower() not in gone])
    if req.get('source') == 'aew':
        view = aew_view(view)
    os.makedirs(PREVIEWS, exist_ok=True)
    # (old previews: only the last hour's kept - files only: the folder also holds aew_tex\, the AEW preview's
    #  textures, and os.remove on it failed every inspect once it was an hour old, 2026-11-09; a tidy-up must never
    #  stop a preview)
    for root in (PREVIEWS, os.path.join(PREVIEWS, 'aew_tex')):
        for f in (os.listdir(root) if os.path.isdir(root) else []):
            p = os.path.join(root, f)
            try:
                if os.path.isfile(p) and time.time() - os.path.getmtime(p) > 3600:
                    os.remove(p)
            except OSError:
                pass
    out = os.path.join(PREVIEWS, '%s_%s.png' % (re.sub(r'[^A-Za-z0-9_]+', '_', os.path.basename(str(req['id'])))[-60:],
                                               time.strftime('%H%M%S')))
    try:
        return preview.render_source(view, out, title='')
    except Exception as e:   # noqa: BLE001  (a preview must never stop reading the meshes)
        log('preview: %s' % e)
        return None


def aew_view(src):
    """an AEW wrestler (source_aew.load) in render_source's terms: 2K's axes (-Y up, the face toward -Z, +X his left;
    AEW: Z up, the face toward +Y), each mesh's colour image as a file, the hair drawn by its colour's alpha"""
    import numpy as np
    sys.path.insert(0, os.path.join(HERE, 'k26'))
    import source_aew
    from aew_bridge import AX
    T = AX * np.array([1.0, -1.0, -1.0])[:, None]
    tdir = os.path.join(PREVIEWS, 'aew_tex')
    os.makedirs(tdir, exist_ok=True)
    out = []
    for i, me in enumerate(src['meshes']):
        tex = {}
        im = (getattr(me, 'images', None) or {}).get('color')
        if im is not None:
            p = os.path.join(tdir, '%s_%d.png' % (src['name'], i))
            if not os.path.exists(p) or time.time() - os.path.getmtime(p) > 3600:
                # (colour and alpha shrunk apart: Pillow premultiplies RGBA, and AEW's skin keeps a mask in its
                #  alpha - shrunk together, the skin came out in coloured blotches)
                im2 = im.convert('RGBA')
                k = min(1.0, 512.0 / max(im2.size))
                sz = (max(1, int(im2.size[0] * k)), max(1, int(im2.size[1] * k)))
                from PIL import Image
                rgb = im2.convert('RGB').resize(sz, Image.BILINEAR)
                rgb.putalpha(im2.getchannel('A').resize(sz, Image.BILINEAR))
                rgb.save(p)
            tex['color'] = p
        out.append(source_aew.SourceMesh(**dict(me.__dict__, positions=np.asarray(me.positions) @ T.T,
                                                normals=np.asarray(me.normals) @ T.T, textures=tex,
                                                kind='gear' if me.kind == 'hair' else me.kind, source_path=None,
                                                index=i)))
    return dict(src, meshes=out)


def cmd_source_preview(req):
    src = load_source(req)
    return {'preview': source_preview(req, src)}


def cmd_inspect(req):
    src = load_source(req)
    meshes = []
    for m in src['meshes']:
        meshes.append({'name': m.name, 'material': m.material, 'kind': m.kind, 'verts': int(len(m.positions)),
                       'tris': int(len(m.tris)), 'variant': bool(getattr(m, 'variant', False)),
                       'textures': sorted(set(list((m.textures or {}).keys()) + list((getattr(m, 'images', None) or {}).keys())))})
    return {'name': src['name'], 'meshes': meshes, 'attires': [os.path.basename(os.path.dirname(a)) + '/' + os.path.basename(a)
                                                               for a in src.get('attires') or []],
            'female': bool(src.get('female', False)), 'preview': source_preview(req, src)}


def aew_name(req):
    """an AEW wrestler's shown name: M0054_02_Adam_Cole -> Adam Cole (None for 2K)"""
    if req.get('source') != 'aew':
        return None
    parts = str(req['id']).split('_')
    return ' '.join(parts[2:]) if len(parts) > 2 else None


def manifest(mod_id, name, base, female, pac=None):
    """the superstar mod's manifest.txt (docs/SVRMOD_FORMAT.md 3.1); pac: the built ch.pac - over the old 192-page
    window it says needs=2.1.6 (the big-model memory), as the Mod Maker and its converters write it"""
    lines = ['type=superstar', 'id=%s' % mod_id, 'name=%s' % name[:31], 'short=%s' % name[:31], 'base=%s' % base,
             'author=', 'version=1.0', 'made_with=PortIT 1.1.0', 'ratings=80,80,80,80,80,80,80', 'call=48']
    if pac:
        try:
            import model_budget
            need = model_budget.needs(pac)
        except Exception:   # noqa: BLE001
            need = None
        if need:
            lines.append('needs=%s' % need)
    return '\n'.join(lines) + '\n'


def export_svrmod(mod_dir, out_dir=None):
    """a mod folder -> <id>.svrmod (a zip of the folder, stored entries, manifest.txt at its root: the format the
    launcher installs and the Mod Maker opens and edits - docs/SVRMOD_FORMAT.md 1) and, when the port has an HD
    texture pack, <id>_HD_textures.zip (a 'Texture Packs' folder: texture packs are no .svrmod type) -> paths"""
    import zipfile
    mod_dir = os.path.normpath(mod_dir)
    mid = os.path.basename(mod_dir)
    out_dir = out_dir or os.path.dirname(mod_dir)
    res = {}
    path = os.path.join(out_dir, mid + '.svrmod')
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_STORED) as z:
        for root, dirs, files in os.walk(mod_dir):
            dirs[:] = sorted(d for d in dirs if d != 'texture_pack' and not d.startswith('.'))
            for f in sorted(files):
                if f == 'portit_report.txt' or f.startswith('.'):
                    continue
                full = os.path.join(root, f)
                rel = os.path.relpath(full, mod_dir).replace(os.sep, '/')
                if rel == 'manifest.txt':
                    z.writestr(rel, _with_needs(full, os.path.join(mod_dir, 'ch.pac')))
                else:
                    z.write(full, rel)
    res['svrmod'] = path
    tp = os.path.join(mod_dir, 'texture_pack')
    if os.path.isdir(tp) and os.listdir(tp):
        zpath = os.path.join(out_dir, mid + '_HD_textures.zip')
        with zipfile.ZipFile(zpath, 'w', zipfile.ZIP_STORED) as z:
            for f in sorted(os.listdir(tp)):
                z.write(os.path.join(tp, f), '%s HD/%s' % (mid, f))
        res['texture_zip'] = zpath
    return res


def _with_needs(manifest_path, pac):
    """the manifest's text, with needs= added when a superstar's ch.pac is over the old window and it has none
    (folders ported before PortIT wrote it, 2026-11-09)"""
    text = open(manifest_path, encoding='utf-8', errors='replace').read()
    keys = {ln.split('=', 1)[0] for ln in text.splitlines() if '=' in ln}
    if 'type=superstar' in text.splitlines() and 'needs' not in keys and os.path.isfile(pac):
        try:
            import model_budget
            need = model_budget.needs(pac)
        except Exception:   # noqa: BLE001
            need = None
        if need:
            text = text.rstrip('\n') + '\nneeds=%s\n' % need
    return text


def cmd_export_svrmod(req):
    """{mod_dir} -> {svrmod, texture_zip?}: an already ported mod folder saved as .svrmod"""
    return export_svrmod(req['mod_dir'], req.get('out'))


def cmd_port(req):
    import convert
    import preview
    t0 = time.time()
    out = req['out']
    os.makedirs(out, exist_ok=True)
    work = os.path.join(out, '_work')
    os.makedirs(work, exist_ok=True)
    os.environ['PORTIT_GAME'] = req['game']
    req = resolve_cak(req)
    if req['source'] == '2k26' and 'mattel' in str(req.get('attire', '')).lower():
        raise RuntimeError('This is a Mattel action-figure model: its toy-like face (tiny eyes) has no human face for '
                           'the face rig to fit. Choose another attire or wrestler.')
    if req['source'] in ('2k26', 'aew') and not req.get('classic'):
        # 2K26 / AEW (through k26/aew_bridge.py): the proven pipeline of the working Rock / Brock ports (k26/rock_model.py), fed by PortIT's reader
        sys.path.insert(0, os.path.join(HERE, 'k26'))
        import port_2k
        import preview
        bridge = None
        if req['source'] == 'aew':
            import aew_bridge
            log('reading the AEW wrestler...')
            bridge = aew_bridge.Bridge(req['path'], req['id'], work, log)
            att = bridge.attire
            req = dict(req, female=bridge.female)
        else:
            att = req.get('attire') or (port_2k_attires(req['id']) or ['Default_Attire'])[0]
            att = os.path.dirname(att) if att.lower().endswith(('.mcd', '.mdl')) else att
        budget = int(req.get('budget', 190))
        # (the HD pack's size: 8192 / 4096 / 2048 wide - PortIT 2026-11-10, people's packs reached ~1 GB at 8192)
        import full_port
        full_port.PACK_SIZE[0] = int(req.get('hd_size') or 4096) if int(req.get('hd_size') or 4096) in (2048, 4096, 8192) else 4096
        mod_id = req.get('mod_id') or 'portit_' + time.strftime('%Y%m%d_%H%M%S')
        log('porting with the Rock / Brock pipeline (%s)...' % ('full detail + texture pack' if budget >= port_2k.FULL_BUDGET
                                                               else '%d pages' % budget))
        pac, brep, report, info = port_2k.build(req['id'], att, work, req['game'], budget=budget, log=log,
                                                female=bool(req.get('female', False)), pack_prefix=mod_id + '_hd',
                                                leave_out=set(req.get('leave_out') or []), bridge=bridge)
        final = os.path.join(out, mod_id)
        os.makedirs(final, exist_ok=True)
        shutil.copy(pac, os.path.join(final, 'ch.pac'))
        # (the full route's texture pack: installed into the game's enabled texture pack by 'install')
        if info.get('pack'):
            tp = os.path.join(final, 'texture_pack')
            os.makedirs(tp, exist_ok=True)
            for f in info['pack']:
                shutil.copy(f, os.path.join(tp, os.path.basename(f)))
        with open(os.path.join(final, 'manifest.txt'), 'w', encoding='utf-8') as f:
            f.write(manifest(mod_id, req.get('mod_name') or aew_name(req) or os.path.basename(req['id']), '125', False,
                             os.path.join(final, 'ch.pac')))
        pv = preview.render(os.path.join(final, 'ch.pac'), os.path.join(out, mod_id + '_preview.png'))
        a = brep['attires'][0]
        report.append('built: %d meshes, %d memory pages' % (a['meshes'], a['pages']))
        exp = export_svrmod(final, out)
        report.append('saved as %s (the Mod Maker opens it to edit the name, stats, moves...)'
                      % os.path.basename(exp['svrmod']) +
                      ('; HD textures: %s (unzip into the game\'s Texture Packs folder)' % os.path.basename(exp['texture_zip'])
                       if exp.get('texture_zip') else ''))
        report.append('took %.0f s' % (time.time() - t0))
        with open(os.path.join(final, 'portit_report.txt'), 'w', encoding='utf-8') as f:
            f.write('\n'.join(report))
        return {'out': final, 'pac': os.path.join(final, 'ch.pac'), 'pages': a['pages'], 'report': report, 'preview': pv,
                'checks': info.get('checks'), 'texture_pack': [os.path.basename(f) for f in info.get('pack', [])],
                'svrmod': exp['svrmod'], 'texture_zip': exp.get('texture_zip')}
    log('reading the source...')
    src = load_source(req)
    female = bool(req.get('female', src.get('female', False)))
    template = req.get('template') or ('ch109' if female else 'ch104')
    log('converting (pose, weights, face, textures)...')
    model, report = convert.convert(src, req['game'], work, female=female, template=template,
                                    face=req.get('face', True), skip_variants=req.get('skip_variants', True), log=log)
    log('building the SvR model (simplified to fit %d pages)...' % req.get('budget', 190))
    pac, brep, factor = convert.build(model, work, int(req.get('budget', 190)), log=log)
    mod_id = req.get('mod_id') or 'portit_' + time.strftime('%Y%m%d_%H%M%S')
    final = os.path.join(out, mod_id)
    os.makedirs(final, exist_ok=True)
    shutil.copy(pac, os.path.join(final, 'ch.pac'))
    with open(os.path.join(final, 'manifest.txt'), 'w', encoding='utf-8') as f:
        f.write(manifest(mod_id, req.get('mod_name') or src['name'], template.replace('ch', '').lstrip('0') or '0', female,
                         os.path.join(final, 'ch.pac')))
    log('rendering the preview...')
    pv = preview.render(os.path.join(final, 'ch.pac'), os.path.join(out, mod_id + '_preview.png'))
    a = brep['attires'][0]
    report.append('built: %d meshes, %d of %d memory pages (detail kept: %d%%)' % (a['meshes'], a['pages'],
                  int(req.get('budget', 190)), round(100 * min(1, factor))))
    report += ['builder: ' + w for w in brep.get('warnings', [])]
    exp = export_svrmod(final, out)
    report.append('saved as %s (the Mod Maker opens it to edit the name, stats, moves...)' % os.path.basename(exp['svrmod']))
    report.append('took %.0f s' % (time.time() - t0))
    with open(os.path.join(final, 'portit_report.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(report))
    return {'out': final, 'pac': os.path.join(final, 'ch.pac'), 'pages': a['pages'], 'report': report, 'preview': pv,
            'svrmod': exp['svrmod']}


def cmd_preview(req):
    import preview
    return {'preview': preview.render(req['pac'], req['out'])}


def cmd_install(req):
    src, game, mod_id = req['mod_dir'], req['game'], req['mod_id']
    dest = os.path.join(game, 'Mods', 'Superstars', mod_id)
    if os.path.exists(dest):
        bak = os.path.join(game, 'Mods', '_portit_backup', mod_id + '_' + time.strftime('%Y%m%d_%H%M%S'))
        os.makedirs(os.path.dirname(bak), exist_ok=True)
        shutil.move(dest, bak)
    shutil.copytree(src, dest, ignore=shutil.ignore_patterns('texture_pack'))
    out = {'installed': dest}
    out.update(copy_texture_pack(src, game, mod_id))
    return out


def copy_texture_pack(src, game, mod_id):
    """the HD texture pack of a full-route port: into the first pack the game has enabled (svr2011.toml
    native_texture_packs), else 'PortIT HD' - which then has to be ticked in the launcher's Texture packs list"""
    out = {}
    tp = os.path.join(src, 'texture_pack')
    if os.path.isdir(tp) and os.listdir(tp):
        sys.path.insert(0, os.path.join(HERE, 'k26'))
        import full_port
        pack = full_port.enabled_pack(game)
        out['pack_enabled'] = bool(pack)
        pack = pack or 'PortIT HD'
        pdir = os.path.join(game, 'Texture Packs', pack)
        os.makedirs(pdir, exist_ok=True)
        for f in os.listdir(pdir):          # (this mod's older pack files: replaced)
            if f.startswith(mod_id + '_hd_'):
                os.remove(os.path.join(pdir, f))
        for f in os.listdir(tp):
            shutil.copy(os.path.join(tp, f), os.path.join(pdir, f))
        out['pack'] = pdir
    return out


def cmd_stars(req):
    """the superstars an attire can go to (the Mod Maker's Attires page): names, their attires, entrance gear, slots
    taken by installed attire mods"""
    sys.path.insert(0, os.path.join(HERE, 'portit'))
    import attires
    return {'stars': attires.stars(req['game'])}


def cmd_install_attire(req):
    """the port as an attire mod: Mods/Attires/<id> (manifest + attire.pac), the HD pack like a superstar install"""
    sys.path.insert(0, os.path.join(HERE, 'portit'))
    import attires
    src = req['mod_dir']
    return attires.install(req, lambda s, g, _id: copy_texture_pack(s, g, os.path.basename(os.path.normpath(src))))


BIGMODEL_DLL = os.path.join(HERE, 'bigmodel', 'xinput1_4.dll')


def _sha(p):
    import hashlib
    with open(p, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def cmd_bigmodel(req):
    """the big-model DLL in the SvR game folder (people's request 2026-11-10: install it from PortIT).
    {game, action: 'status' | 'install' | 'remove'} -> {installed: 'portit' | 'other' | 'none', active, version, note}
    The DLL is xinput1_4.dll beside svr2011.exe: the game loads it on start; it raises a wrestler's model memory
    window from 768 KB to 4096 KB, so full-detail ports and their HD texture pack can be used. Another mod's
    xinput1_4.dll is never overwritten without a copy kept (xinput1_4.dll.before_portit)."""
    game = req['game']
    if not os.path.isfile(os.path.join(game, 'svr2011.exe')):
        raise ValueError('svr2011.exe is not in %s - choose the SvR 2011 game folder' % game)
    dst = os.path.join(game, 'xinput1_4.dll')
    keep = dst + '.before_portit'
    action = req.get('action', 'status')
    if action in ('install', 'remove'):
        import subprocess
        out = subprocess.run(['tasklist'], capture_output=True, text=True).stdout.lower()
        if 'svr2011.exe' in out:
            raise RuntimeError('Close the game first (svr2011.exe is running): Windows keeps the DLL in use')
    if action == 'install':
        if os.path.isfile(dst) and _sha(dst) != _sha(BIGMODEL_DLL) and not os.path.exists(keep):
            shutil.copy2(dst, keep)          # (another mod's xinput1_4.dll: kept)
        shutil.copy2(BIGMODEL_DLL, dst)
    elif action == 'remove':
        if os.path.isfile(dst) and _sha(dst) == _sha(BIGMODEL_DLL):
            os.remove(dst)
            if os.path.exists(keep):
                os.replace(keep, dst)        # (the other mod's DLL back)
    state = 'none'
    if os.path.isfile(dst):
        state = 'portit' if _sha(dst) == _sha(BIGMODEL_DLL) else 'other'
    active, note = False, ''
    logf = os.path.join(game, 'bigmodel.log')
    if os.path.isfile(logf):
        import re
        txt = open(logf, encoding='utf-8', errors='replace').read()
        m = re.findall(r'vertex windows[^:]*: *\d+ *-> *(\d+) *KB', txt)
        active = bool(m) and int(m[-1]) > 768
        note = ('the last game start used it: %s KB a wrestler' % m[-1]) if m else 'its log shows no model memory line yet'
    version = ''
    rf = os.path.join(game, 'release_files.txt')
    if os.path.isfile(rf):
        version = open(rf, encoding='utf-8', errors='replace').readline().strip()
    return {'installed': state, 'active': active, 'version': version, 'note': note,
            'has_backup': os.path.exists(keep)}


def check_runtime():
    """NumPy and Pillow load, or a message that says what to do (PortIT 1.0.1: on some PCs Windows or the antivirus
    blocked or removed NumPy's DLLs - users got NumPy's long 'Importing the numpy C-extensions failed' text)"""
    try:
        import numpy  # noqa: F401
        from PIL import Image  # noqa: F401
    except ImportError as e:
        why = [ln.strip() for ln in str(e).splitlines() if ln.strip()]
        original = next((ln for ln in reversed(why) if 'Original error' in ln or 'DLL load failed' in ln), why[-1] if why else '')
        raise RuntimeError(
            'PortIT\'s Python could not load NumPy / Pillow - Windows or an antivirus blocked or removed one of its '
            'files.\n'
            '1. Close PortIT. Right-click the PortIT zip you downloaded > Properties > tick "Unblock" > OK, then unzip it '
            'again (with Windows\' own "Extract All") into a new folder, and run PortIT.exe from there.\n'
            '2. If it still fails: your antivirus may have quarantined a file in PortIT\\Engine\\python (often '
            'numpy.libs\\libscipy_openblas64_...dll). Restore it and add the PortIT folder as an exception.\n'
            '3. Windows 11 "Smart App Control" blocks unsigned files: see the user guide, "When something goes wrong".\n'
            'Python: %s\nError: %s' % (sys.executable, original)) from None


def main(inp, outp):
    req = json.load(open(inp, encoding='utf-8'))
    try:
        check_runtime()
        import dds_compat      # (sRGB DDS formats such as DXGI 78 opened as their plain twins - Pillow refuses them)
        dds_compat.install()
        res = {'list': cmd_list, 'inspect': cmd_inspect, 'port': cmd_port, 'preview': cmd_preview,
               'source_preview': cmd_source_preview, 'export_svrmod': cmd_export_svrmod,
               'install': cmd_install, 'stars': cmd_stars, 'install_attire': cmd_install_attire,
               'bigmodel': cmd_bigmodel}[req['command']](req)
        res['ok'] = True
    except Exception as e:
        traceback.print_exc()
        res = {'ok': False, 'error': '%s: %s' % (type(e).__name__, e)}
    with open(outp, 'w', encoding='utf-8') as f:
        json.dump(res, f)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
