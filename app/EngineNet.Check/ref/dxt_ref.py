"""Reference for the C# DXT check: w13_char.dds_encode (Pillow's DXT encoder) on each image, DXT5 (as RGBA) and DXT1
(alpha set to 255) -> the header (128 bytes, hex), the file size and the top level's error against the source
(root mean square over RGB, and over A for DXT5), decoded by Pillow.
    python dxt_ref.py <out.json> <image files>..."""
import io, json, os, sys
import numpy as np
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, 'readers'), os.path.join(E, 'svr')]
import dds_compat  # noqa: E402
dds_compat.install()
from PIL import Image  # noqa: E402
import w13_char as W  # noqa: E402

out = {}
for f in sys.argv[2:]:
    src = Image.open(f).convert('RGBA')
    rec = {}
    for fc in ('DXT5', 'DXT1'):
        s = src if fc == 'DXT5' else src.convert('RGB').convert('RGBA')
        d = W.dds_encode(s, fc)
        top = np.asarray(Image.open(io.BytesIO(d)).convert('RGBA')).astype(np.float64)
        a = np.asarray(s).astype(np.float64)
        rec[fc] = {'header': d[:128].hex(), 'size': len(d),
                   'rmse_rgb': float(np.sqrt(((top[..., :3] - a[..., :3]) ** 2).mean())),
                   'rmse_a': float(np.sqrt(((top[..., 3] - a[..., 3]) ** 2).mean()))}
    out[os.path.abspath(f)] = rec
    print(os.path.basename(f), {k: round(v['rmse_rgb'], 3) for k, v in rec.items()}, flush=True)
json.dump(out, open(sys.argv[1], 'w'), indent=0)
