"""WWE 2K .mtls material files (2K20 - 2K25 mods) -> which texture each material uses.

PortIT 2026-11-10. Layout from the "WWE 2K MTLs" 010 template (Nenkai, MAKE MODDING FREE\\important knowledge):
  "MTLs" u32 version (5 or 6) u32 stringTableLength, strings ...
  v6: the material names (name:skinned) come first, then one "MTL!" block per material, in the same order; a block's
      string table holds its shader, then each parameter's name, and for a texture parameter its value right after it
      (colorMap, l_wrist_color, colorMap@ref ...).
  v5: one string table for every material: <name> then its shader (yCS_skin, yCh_gear ...), then its parameters the
      same way.
Only the texture names are read (that is all PortIT needs): {material: {parameter: texture name}}."""
import re

TEX_PARAMS = ('colorMap', 'g_nrmMap', 'normalMap', 'flowMap', 'g_comboMap', 'roughnessMap', 'metalMap', 'flushMap')


def _strings(b):
    return [s.decode('latin1') for s in re.findall(rb'[\x20-\x7e]{2,}', b)]


def _params(strings):
    out = {}
    for i, s in enumerate(strings[:-1]):
        if s in TEX_PARAMS and not strings[i + 1].endswith('@ref') and strings[i + 1] not in TEX_PARAMS:
            out.setdefault(s, strings[i + 1])
    return out


def read(path):
    """-> {material name (lower case, no ':skinned'): {parameter: texture name}}"""
    d = open(path, 'rb').read()
    if d[:4] != b'MTLs':
        return {}
    out = {}
    blocks = d.split(b'MTL!')
    if len(blocks) > 1:                                         # v6
        names = [re.sub(r'(?i):skinned$', '', s) for s in _strings(blocks[0])[1:] if s.lower().endswith(':skinned')]
        for name, blk in zip(names, blocks[1:]):
            out[name.lower()] = _params(_strings(blk))
        return out
    st = _strings(d[12:])                                       # v5
    starts = [i - 1 for i, s in enumerate(st) if s.startswith(('yC', 'yc')) and i > 0]
    for k, i in enumerate(starts):
        end = starts[k + 1] if k + 1 < len(starts) else len(st)
        out[re.sub(r'(?i):skinned$', '', st[i]).lower()] = _params(st[i + 1:end])
    return out


def color_maps(folder):
    """every .mtls under folder -> {material: colour texture name} (default_* = the game's own, left out)"""
    import glob
    import os
    out = {}
    for p in glob.glob(os.path.join(folder, '**', '*.mtls'), recursive=True):
        try:
            for name, prm in read(p).items():
                c = prm.get('colorMap')
                if c and not c.lower().startswith('default'):
                    out.setdefault(name, c)
        except (OSError, ValueError):
            pass
    return out


if __name__ == '__main__':
    import sys
    for name, prm in read(sys.argv[1]).items():
        print('%-24s %s' % (name, prm))
