# (PortIT copy of tools/k26_porter/bigmodel_install.py - the pipeline of the working Rock / Brock ports; only file paths changed, marked PortIT:)
r"""Put a model over the match's 192-page budget into a superstar mod, the way the port would if it let it.

    python -I bigmodel_install.py <game folder> <mod folder name> <big ch.pac>

The port (superstar_mods.cpp) rebuilds Mods\SuperstarOverlay\chNNN.pac from the mod's ch.pac only when the stamp in
chNNN.pac.src no longer matches the mod's ch.pac / manifest.txt (name, size, write time) - and that rebuild leaves a
model over 192 pages out. So the mod keeps its normal ch.pac (the stamp's source; what plays without the DLL), and
the overlay gets the big one, renamed like the port's RenameModel (entry names "<template id>..." -> the mod's slot)
with a current stamp. The game needs the DLL's model memory hook (ControllerRemap\bigmodel.c) for it, or the match
load crashes. Saving the mod again in the Mod Maker changes its ch.pac: run this again after.
The overlay file it replaces is kept as chNNN.pac.normal.
"""
import os
import shutil
import sys


def stamp(srcs):
    st = 'overlay 2\n'
    for p in srcs:
        if not os.path.exists(p):
            continue
        s = os.stat(p)
        ft = s.st_mtime_ns // 100 + 116444736000000000     # (std::filesystem file_clock on Windows = FILETIME)
        st += '%s %d %d\n' % (os.path.basename(p), s.st_size, ft)
    return st


def rename_model(d, slot):
    """port superstar_mods.cpp RenameModel(d, slot): every EMD entry name '<6 digits><attire><kind>' -> slot id"""
    d = bytearray(d)
    if len(d) < 0x4000 or d[:4] != b'EPK8':
        raise SystemExit('not a character model pac (EPK8)')
    b = b'%06d' % slot
    n, p = 0, 0x800
    while p + 12 <= 0x4000:
        if d[p:p + 4] == b'\0\0\0\0':
            break
        emd = d[p:p + 4] == b'EMD '
        entries = (d[p + 4] | d[p + 5] << 8) // 4
        p += 12
        for _ in range(entries):
            if p + 16 > 0x4000:
                break
            if emd and all(48 <= c <= 57 for c in d[p:p + 8]):
                d[p:p + 6] = b
                n += 1
            p += 16
    if not n:
        raise SystemExit('no model entries renamed')
    return bytes(d), n


def main(argv):
    game, mod, big = argv
    sup = os.path.join(game, 'Mods', 'Superstars')
    slot = None
    for line in open(os.path.join(sup, 'slots.txt'), encoding='utf-8', errors='replace'):
        name, _, sid = line.rstrip('\r\n').rpartition('\t')
        if name == mod:
            slot = int(sid)
    if slot is None:
        raise SystemExit('%s has no slot yet in slots.txt: start the game once with the mod first' % mod)
    srcs = [os.path.join(sup, mod, 'ch.pac'), os.path.join(sup, mod, 'manifest.txt')]
    out = os.path.join(game, 'Mods', 'SuperstarOverlay', 'ch%03d.pac' % slot)
    data, n = rename_model(open(big, 'rb').read(), slot)
    if os.path.exists(out) and not os.path.exists(out + '.normal'):
        shutil.copy2(out, out + '.normal')
    with open(out, 'wb') as f:
        f.write(data)
    with open(out + '.src', 'wb') as f:
        f.write(stamp(srcs).encode())
    print('%s: %d model entries renamed to %06d -> %s (stamp current for %s)' % (os.path.basename(big), n, slot, out, mod))


if __name__ == '__main__':
    main(sys.argv[1:])
