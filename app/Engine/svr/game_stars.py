"""The game's superstars and their attires - for installing a port as an attire (PortIT 2026-11-09).

The same rules as the Mod Maker's Attires page (port/modmaker/attire_page.cpp, port/docs/SUPERSTAR_MODS.md "Attire
mods"), read from the game's files:
  * the playable superstars: DLC_HD.pac's SSFA renders (ids 100 - 321), names from chEtc.pac CHAR/DAT records
    (260 bytes after a 4-byte header, the full name at +34);
  * a superstar's own attires: its model pac pac\\ch\\chNNN.pac, EMD entries "%06d" + attire digit + kind ('2' a model);
  * entrance gear: an EMD entry "%06d0" + kind other than '2' (Cena's, Undertaker's, Punk's, HBK's, Morrison's coat /
    hat / shirt) - new attires for them never finish loading a match, so only their own attires can be replaced;
  * attire mods: Mods\\Attires\\<folder>\\manifest.txt (type=attire, superstar=, attire=1-4 | new, name=,
    model=attire.pac, entrance=) - an attire taken by one (and not switched off) isn't free.
Pacs are EPAC (4-character names, 12-byte rows) or EPK8 (8-character names, 16-byte rows).
"""
import os
import struct

from svrfmt import pach_read, unbpe


def pac_index(path):
    """-> [(group, name, offset, size)] of an EPAC / EPK8 pac (its 0x4000-byte table)"""
    with open(path, 'rb') as f:
        head = f.read(0x4000)
    magic = head[:4]
    if magic not in (b'EPAC', b'EPK8'):
        raise ValueError('%s: not a pac' % path)
    wide = magic == b'EPK8'
    row = 16 if wide else 12
    out = []
    p = 0x800
    while p + 12 <= 0x4000:
        typ = head[p:p + 4]
        if typ == b'\0\0\0\0':
            break
        cnt = (struct.unpack_from('<I', head, p + 4)[0] & 0xFFFF) // (4 if wide else 3)
        p += 12
        for _ in range(cnt):
            nl = 8 if wide else 4
            name = head[p:p + nl].rstrip(b'\0').decode('latin-1')
            sec, sz = struct.unpack_from('<II', head, p + nl)
            out.append((typ.decode('latin-1'), name, 0x4000 + sec * 0x800, sz * 0x100))
            p += row
    return out


def pac_entry(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)


def star_names(game):
    """chEtc.pac CHAR/DAT -> {id: name}"""
    path = os.path.join(game, 'pac', 'ch', 'chEtc.pac')
    names = {}
    for grp, name, off, size in pac_index(path):
        if grp != 'CHAR' or not name.startswith('DAT'):
            continue
        data = pac_entry(path, off, size)
        try:
            ents = pach_read(data)
        except ValueError:
            continue
        for sid, blob in ents:
            r = unbpe(blob)
            if len(r) >= 4 + 66:
                nm = r[4 + 34:4 + 34 + 32].split(b'\0')[0].decode('latin-1', 'replace').strip()
                if nm:
                    names[sid] = nm
    return names


def own_attires(game, sid):
    """-> (attires the superstar's own pac has (1-4), wears entrance gear)"""
    path = os.path.join(game, 'pac', 'ch', 'ch%d.pac' % sid)
    pre = '%06d' % sid
    have, gear = set(), False
    try:
        idx = pac_index(path)
    except (OSError, ValueError):
        return [], False
    for grp, name, off, size in idx:
        if grp != 'EMD ' or len(name) != 8 or not name.startswith(pre) or not name[6:].isdigit():
            continue
        if name[7] == '2':
            have.add(int(name[6]) + 1)
        elif name[6] == '0':
            gear = True
    return sorted(a for a in have if 1 <= a <= 4), gear


def manifest_values(text):
    out = {}
    for line in text.splitlines():
        if '=' in line and not line.lstrip().startswith('#'):
            k, v = line.split('=', 1)
            out.setdefault(k.strip(), v.strip())
    return out


def mod_attires(game, sid, skip_folder=None):
    """attire mods (switched on) on this superstar -> {attire: mod name}, folders A-Z (as the game takes them)"""
    root = os.path.join(game, 'Mods', 'Attires')
    out = {}
    if not os.path.isdir(root):
        return out
    for d in sorted(os.listdir(root)):
        p = os.path.join(root, d)
        if d == skip_folder or not os.path.isdir(p) or os.path.exists(os.path.join(p, 'disabled')):
            continue
        try:
            m = manifest_values(open(os.path.join(p, 'manifest.txt'), encoding='utf-8', errors='replace').read())
        except OSError:
            continue
        if m.get('type') != 'attire' or m.get('superstar') != str(sid):
            continue
        try:
            a = int(m.get('attire', ''))
        except ValueError:
            continue
        if 1 <= a <= 4:
            out.setdefault(a, m.get('name') or d)
    return out


def stars(game):
    """the playable superstars, sorted by name: [{id, name, own, gear, mods}]"""
    names = star_names(game)
    ids = set()
    for grp, name, off, size in pac_index(os.path.join(game, 'pac', 'DLC_HD.pac')):
        if grp == 'SSFA' and name.strip().isdigit():
            sid = int(name)
            if 100 <= sid <= 321:
                ids.add(sid)
    out = []
    for sid in ids:
        own, gear = own_attires(game, sid)
        out.append({'id': sid, 'name': names.get(sid, 'Superstar %d' % sid), 'own': own, 'gear': gear,
                    'mods': {str(k): v for k, v in mod_attires(game, sid).items()}})
    out.sort(key=lambda s: s['name'])
    return out


def next_free(game, sid, skip_folder=None):
    """the lowest attire neither the superstar's pac has nor a mod takes (0: none; none with entrance gear)"""
    own, gear = own_attires(game, sid)
    if gear:
        return 0
    mods = mod_attires(game, sid, skip_folder)
    return next((a for a in range(1, 5) if a not in own and a not in mods), 0)


def install_attire(game, pac, folder, sid, slot, name='', entrance=None, author='PortIT', version='1.0'):
    """a port's ch.pac as an attire of superstar sid: Mods\\Attires\\<folder>\\attire.pac + manifest.txt.
    slot: 1-4 (replace / take that attire) or 'new' (the next free one). -> (folder path, warnings)"""
    import shutil
    import time
    own, gear = own_attires(game, sid)
    warn = []
    mods = mod_attires(game, sid, folder)
    if slot == 'new':
        if gear:
            raise ValueError('this superstar wears entrance gear: new attires for them do not load in the game - '
                             'replace attire %s instead' % ('1-%d' % len(own) if len(own) > 1 else '1'))
        if not next_free(game, sid, folder):
            raise ValueError('no free attire (4 at most): replace one instead')
    else:
        slot = int(slot)
        if not 1 <= slot <= 4:
            raise ValueError('attires are 1 to 4')
        if gear and slot not in own:
            raise ValueError('this superstar wears entrance gear: only their own attires (%s) can be replaced'
                             % ', '.join(map(str, own)))
        if slot in mods:
            warn.append('attire %d is already taken by the attire mod "%s" - the launcher moves the newer one to the '
                        'next free attire, or asks to switch one off' % (slot, mods[slot]))
    dest = os.path.join(game, 'Mods', 'Attires', folder)
    if os.path.exists(dest):
        bak = os.path.join(game, 'Mods', '_portit_backup', 'attire_' + folder + '_' + time.strftime('%Y%m%d_%H%M%S'))
        os.makedirs(os.path.dirname(bak), exist_ok=True)
        shutil.move(dest, bak)
    os.makedirs(dest)
    shutil.copy(pac, os.path.join(dest, 'attire.pac'))
    lines = ['type=attire', 'superstar=%d' % sid, 'attire=%s' % slot, 'name=%s' % (name or '')[:31],
             'model=attire.pac']
    if entrance not in (None, '', -1):
        lines.append('entrance=%d' % int(entrance))
    lines += ['author=%s' % author, 'version=%s' % version, '']
    with open(os.path.join(dest, 'manifest.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    return dest, warn
