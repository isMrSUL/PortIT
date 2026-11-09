# (PortIT copy of tools/k26_porter/rock_build.py - the pipeline of the working Rock / Brock ports; only file paths changed, marked PortIT:)
"""Run the Mod Maker's model builder (Mod Maker Tools\\svr11_build.py, imported, not changed) on a model.json from
rock_model.py, with its grid packer swapped for one cell: every mesh already points into one drawn sheet
(the builder would otherwise give the skin and the eye shader a cell each, halving the sheet).
The colour alpha stays as drawn (rock_model: sweat mask, lash cut-out; the builder would set a flat 77).

    python -I rock_build.py <model.json> <out ch.pac> [report.json] [--no-limit]
--no-limit: no 192-page check (for the big-model route: SuperstarOverlay + the DLL's model memory hook; such a
pac crashes the match load without them).
"""
import json
import os
import sys

from PIL import Image

TOOLS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'svr')   # PortIT: the bundled tools
sys.path.insert(0, TOOLS)
import svr11_build as B  # noqa: E402
import w13_char as W  # noqa: E402

_LASH = {}


class OneCell:
    def __init__(self, keys):
        self.keys = list(dict.fromkeys(keys))
        self.cols = self.rows = 1

    def cell(self, key):
        return 0, 0

    def uv(self, key, u, v):
        return u, v

    def sheet(self, images, size, fill):
        pick = [k for k in self.keys if k.endswith('|S') and images.get(k) is not None] or \
               [k for k in self.keys if images.get(k) is not None]
        if not pick:
            return Image.new('RGBA', size, fill)
        out = W.resize_rgba(images[pick[0]].convert('RGBA'), size, Image.LANCZOS)
        if size == B.COLOR_SIZE and _LASH.get('png'):
            # (the colour alpha as drawn: sweat on the skin, the lashes' cut-out - not the builder's flat 77)
            src = Image.open(_LASH['png']).convert('RGBA')
            out.putalpha(src.getchannel('A').resize(size, Image.LANCZOS))
        return out


def main(argv):
    spec = json.load(open(argv[1], encoding='utf-8'))
    extra = spec.get('_rock') or {}
    _LASH['cell'] = extra.get('lash_cell')
    _LASH['png'] = os.path.join(os.path.dirname(os.path.abspath(argv[1])), spec['attires'][0]['textures']['color'])
    B.Atlas = OneCell
    if '--no-limit' in argv:
        argv = [a for a in argv if a != '--no-limit']
        B.model_budget.LIMIT = 1 << 30
    return B.main(argv)


if __name__ == '__main__':
    sys.exit(main(sys.argv))
