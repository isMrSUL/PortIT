"""Attire mods (2026-11-09): a ported model as an attire of a superstar the game has - the Mod Maker's Attires page
(port/modmaker/attire_page.cpp) in PortIT. Same format and rules as the Mod Maker and the game (superstar_mods.cpp):

  <game>/Mods/Attires/<id>/manifest.txt   type=attire, id=, superstar=<game id>, attire=<1-4>|new, name=, model=attire.pac,
                                          author=, version=, entrance=<entrance number> (optional), made_with=
  <game>/Mods/Attires/<id>/attire.pac     a character model pac (its first attire is used; the game renames its
                                          EMD entries to the superstar's id and attire itself)

- a superstar's own attires: its model pacs' EMD entries "%06d%d2" (id, attire digit 0-3, kind 2 = a model): the game's
  pac\\ch\\chNNN.pac and the installed DLC's ch<id>_dlcNN.pac;
- entrance gear (attire 1 has another EMD kind: Undertaker, Punk, Cena, HBK, Morrison): a NEW attire for them never
  loads - only their own attires can be replaced;
- installed, switched-on attire mods (no "disabled" file) take their attire=1-4; two on one attire clash (the first
  folder A-Z keeps it);
- four attires at most; the model must fit the game's model page budget, like a superstar mod's.
"""
import os
import re
import shutil
import struct
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'svr'))
import svrfmt  # noqa: E402


def _pac_names(path):
    """the entry names of an EPAC / EPK8 pac from its header only (no data read): [(group, name)]"""
    try:
        with open(path, 'rb') as f:
            d = f.read(0x4000)
    except OSError:
        return []
    out = []
    if d[:4] == b'EPK8':
        p = 0x800
        while p + 12 <= 0x4000:
            typ = d[p:p + 4]
            if typ == b'\0\0\0\0':
                break
            cnt = struct.unpack_from('<H', d, p + 4)[0]
            p += 12
            for _ in range(cnt):
                out.append((typ.decode('latin1'), d[p:p + 8].decode('latin1')))
                p += 16
    elif d[:4] == b'EPAC':
        p = 0x800
        while p + 12 <= 0x4000:
            typ = d[p:p + 4]
            if typ == b'\0\0\0\0':
                break
            cnt = (struct.unpack_from('<I', d, p + 4)[0] & 0xFFFF) // 3
            p += 12
            for _ in range(cnt):
                out.append((typ.decode('latin1'), d[p:p + 4].decode('latin1')))
                p += 12
    return out


def _char_names(chetc, names):
    """chEtc.pac CHAR / DAT* records: id -> the superstar's full name (record + 34, 32 bytes)"""
    try:
        _, groups, _ = svrfmt.epac_read(open(chetc, 'rb').read())
    except (OSError, ValueError):
        return
    for typ, ents in groups:
        if typ != b'CHAR':
            continue
        for name, blob in ents:
            if not name.startswith(b'DAT'):
                continue
            try:
                for i, data in svrfmt.pach_read(blob):
                    r = svrfmt.unbpe(data)
                    if len(r) >= 4 + 66:
                        names[i] = r[38:70].split(b'\0')[0].decode('latin1').strip()
            except Exception:
                continue


def _dlc_root(game):
    return os.path.join(game, 'UserData', '0000000000000000', '5451085D', '00000002')


def _model_pacs(game, sid, dlc):
    out = []
    if sid >= 100:
        out.append(os.path.join(game, 'pac', 'ch', 'ch%d.pac' % sid))
    out += dlc.get(sid, [])
    return out


def _mod_attires(game, sid, skip=None):
    """attire -> the installed attire mod that asks for it (first folder A-Z), switched-on ones only"""
    root = os.path.join(game, 'Mods', 'Attires')
    out = {}
    if not os.path.isdir(root):
        return out
    for folder in sorted(os.listdir(root)):
        d = os.path.join(root, folder)
        if folder == skip or not os.path.isdir(d) or os.path.exists(os.path.join(d, 'disabled')):
            continue
        m = _manifest(os.path.join(d, 'manifest.txt'))
        if m.get('type') != 'attire' or m.get('superstar') != str(sid):
            continue
        try:
            a = int(m.get('attire', ''))
        except ValueError:
            continue
        if 1 <= a <= 4 and a not in out:
            out[a] = m.get('name') or folder
    return out


def _manifest(path):
    m = {}
    try:
        for line in open(path, encoding='utf-8', errors='replace'):
            k, sep, v = line.rstrip('\r\n').partition('=')
            if sep:
                m[k.strip()] = v.strip()
    except OSError:
        pass
    return m


def stars(game):
    """every superstar an attire can go to: [{id, name, dlc, attires: [1-4 the game has], gear, mods: {attire: name}}]"""
    names = {}
    _char_names(os.path.join(game, 'pac', 'ch', 'chEtc.pac'), names)
    ids = set()
    for typ, name in _pac_names(os.path.join(game, 'pac', 'DLC_HD.pac')):
        if typ == 'SSFA':
            try:
                i = int(name)
            except ValueError:
                continue
            if 100 <= i <= 321:
                ids.add(i)
    # the installed DLC packages: their model pacs (DLC superstars 51-58, extra attires of disc superstars) and names
    dlc, dlc_names = {}, {}
    root = _dlc_root(game)
    if os.path.isdir(root):
        for pkg in sorted(os.listdir(root)):
            chdir = os.path.join(root, pkg, 'pac', 'ch')
            _char_names(os.path.join(chdir, 'chEtc.pac'), dlc_names)
            if not os.path.isdir(chdir):
                continue
            for f in sorted(os.listdir(chdir)):
                m = re.fullmatch(r'ch(\d+)_dlc(\d+)\.pac', f, re.I)
                if m and 0 < int(m.group(1)) < 1000:
                    dlc.setdefault(int(m.group(1)), []).append(os.path.join(chdir, f))
    out = []
    for sid in sorted(ids | {i for i in dlc if i < 100}):
        own, gear = set(), False
        for pac in _model_pacs(game, sid, dlc):
            for typ, n in _pac_names(pac):
                if typ != 'EMD ' or len(n) != 8 or n[:6] != '%06d' % sid or not n[6].isdigit():
                    continue
                if n[7] == '2' and 0 <= int(n[6]) <= 3:
                    own.add(int(n[6]) + 1)
                if n[6] == '0' and n[7] != '2':
                    gear = True
        # (a DLC superstar: its package's own name - the disc's table has placeholders "DLC 02"...)
        name = (dlc_names.get(sid) or names.get(sid)) if sid < 100 else (names.get(sid) or dlc_names.get(sid))
        name = name or 'Superstar %d' % sid
        out.append({'id': sid, 'name': name + (' (DLC)' if sid < 100 else ''), 'dlc': sid < 100,
                    'attires': sorted(own), 'gear': gear,
                    'mods': {str(k): v for k, v in _mod_attires(game, sid).items()}})
    out.sort(key=lambda s: s['name'].lower())
    return out


def _mod_folder(game, sid, attire):
    """the folder of the first switched-on attire mod (A-Z) on that superstar's attire, or None"""
    root = os.path.join(game, 'Mods', 'Attires')
    if not os.path.isdir(root):
        return None
    for folder in sorted(os.listdir(root)):
        d = os.path.join(root, folder)
        if not os.path.isdir(d) or os.path.exists(os.path.join(d, 'disabled')):
            continue
        m = _manifest(os.path.join(d, 'manifest.txt'))
        if m.get('type') == 'attire' and m.get('superstar') == str(sid) and m.get('attire') == str(attire):
            return folder
    return None


def mod_id(name, star_name):
    s = ((name or 'attire') + '_' + star_name).lower()
    s = re.sub(r'[^a-z0-9]+', '_', s).strip('_')
    return s or 'attire'


def install(req, copy_pack):
    """req: mod_dir (a PortIT output with ch.pac), game, star, star_name, slot ('new' or 1-4), name, author,
    version, entrance (optional number). copy_pack(src_mod_dir, game, mod_id) installs the HD texture pack."""
    src, game = req['mod_dir'], req['game']
    pac = os.path.join(src, 'ch.pac')
    if not os.path.isfile(pac):
        raise ValueError('the port has no ch.pac - port first')
    with open(pac, 'rb') as f:
        if f.read(4) != b'EPK8':
            raise ValueError('ch.pac is not a character model pac')
    sid = int(req['star'])
    slot = str(req.get('slot') or 'new')
    if slot != 'new' and not ('1' <= slot <= '4' and len(slot) == 1):
        raise ValueError('attire must be 1-4 or new')
    mid = mod_id(req.get('name', ''), req.get('star_name', str(sid)))
    dest = os.path.join(game, 'Mods', 'Attires', mid)
    out = {'id': mid, 'warnings': []}
    # (the rules again here, not only in the dialog - the Mod Maker's and the game's: a new attire never loads for a
    #  superstar with entrance gear, four attires at most, two mods on one attire clash)
    star = next((x for x in stars(game) if x['id'] == sid), None)
    if star is None:
        raise ValueError('superstar %d is not in this game' % sid)
    taken = {int(k): v for k, v in star['mods'].items()}
    taken.pop(None, None)
    if slot == 'new':
        if star['gear']:
            raise ValueError('%s wears entrance gear: new attires for them do not load in the game - replace attire %s '
                             'instead' % (star['name'], '1-%d' % len(star['attires']) if len(star['attires']) > 1 else '1'))
        own_or_mod = set(star['attires']) | {a for a, n in taken.items() if _mod_folder(game, sid, a) != mid}
        if all(a in own_or_mod for a in range(1, 5)):
            raise ValueError('%s has no free attire (4 at most): replace one instead' % star['name'])
    else:
        a = int(slot)
        if star['gear'] and a not in star['attires']:
            raise ValueError('%s wears entrance gear: only their own attires (%s) can be replaced'
                             % (star['name'], ', '.join(map(str, star['attires']))))
        if a in taken and _mod_folder(game, sid, a) != mid:
            out['warnings'].append('attire %d of %s is already taken by the attire mod "%s": the launcher moves the newer '
                                   'one to the next free attire, or asks to switch one off' % (a, star['name'], taken[a]))
    if os.path.exists(dest):
        bak = os.path.join(game, 'Mods', '_portit_backup', 'attire_' + mid + '_' + time.strftime('%Y%m%d_%H%M%S'))
        os.makedirs(os.path.dirname(bak), exist_ok=True)
        shutil.move(dest, bak)
        out['backup'] = bak
    os.makedirs(dest)
    lines = ['type=attire', 'id=' + mid, 'superstar=%d' % sid, 'attire=' + slot, 'name=' + req.get('name', ''),
             'model=attire.pac', 'author=' + req.get('author', ''), 'version=' + (req.get('version') or '1.0'),
             'made_with=PortIT']
    ent = str(req.get('entrance') or '').strip()
    if ent.isdigit() and int(ent) > 0:
        lines.append('entrance=' + ent)
    with open(os.path.join(dest, 'manifest.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    shutil.copy(pac, os.path.join(dest, 'attire.pac'))
    out['installed'] = dest
    out.update(copy_pack(src, game, mid) or {})
    return out
