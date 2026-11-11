"""Reference for the C# image check: each file opened as the engine opens it (Pillow + dds_compat), then the engine's
image operations, each as sha256 of the pixel bytes.
    python images_ref.py <out.json> <image files>..."""
import hashlib, json, os, sys
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, 'readers'), os.path.join(E, 'svr')]
import dds_compat  # noqa: E402
dds_compat.install()
from PIL import Image  # noqa: E402

def h(im): return [im.mode, im.size[0], im.size[1], hashlib.sha256(im.tobytes()).hexdigest()]
def resize_rgba(img, size, method=Image.BOX):       # (w13_char.resize_rgba)
    rgb = img.convert('RGB').resize(size, method)
    rgb.putalpha(img.getchannel('A').resize(size, method))
    return rgb

out = {}
for f in sys.argv[2:]:
    im = Image.open(f)
    im.load()
    if im.mode not in ('L', 'RGB', 'RGBA'):
        im = im.convert('RGBA')
    w, hh = im.size
    a = im.convert('RGBA')
    s2 = (max(1, int(w * 0.37)), max(1, int(hh * 0.53)))
    ops = {'open': h(im),
           'rgb_lanczos': h(a.convert('RGB').resize(s2, Image.LANCZOS)),
           'alpha_lanczos': h(a.getchannel('A').resize(s2, Image.LANCZOS)),
           'rgb_bilinear': h(a.convert('RGB').resize((w // 3 + 1, max(1, hh // 2)), Image.BILINEAR)),
           'apart_box': h(resize_rgba(a, (max(1, w // 2), max(1, hh // 2)))),
           'rgba_premul_lanczos': h(a.resize(s2, Image.LANCZOS)),
           'to_l': h(a.convert('L'))}
    if w <= 1024:
        ops['rgb_up_lanczos'] = h(a.convert('RGB').resize((w * 2 + 3, hh * 2 + 1), Image.LANCZOS))
    out[os.path.abspath(f)] = ops
json.dump(out, open(sys.argv[1], 'w'), indent=0)
print('reference written:', len(out), 'images')
