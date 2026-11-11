"""Reference for the C# builder check: the engine's builder (rock_build + svr11_build, run in-process as
full_port.build_pac runs it) on each model.json -> <out dir>/<n>_py.pac, its report, and every picture handed to the
DXT encoder (<n>_enc<k>.png, in call order) - so the sheets can be compared exactly, before the (different) encoders.
    python build_ref.py <out dir> <model.json>:<full 0|1>..."""
import json, os, sys
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import full_port as FP  # noqa: E402
import rock_build as RB  # noqa: E402
import w13_char as W  # noqa: E402
import io, contextlib
out_dir = sys.argv[1]
os.makedirs(out_dir, exist_ok=True)
plain_enc = W.dds_encode
o_sheet = RB.OneCell.sheet
refs = []
for k, spec in enumerate(sys.argv[2:]):
    path, full = spec.rsplit(':', 1)
    full = full == '1'
    pac = os.path.join(out_dir, '%d_py.pac' % k)
    calls = []

    def enc(img, fourcc, calls=calls, k=k):
        p = os.path.join(out_dir, '%d_enc%d.png' % (k, len(calls)))
        img.convert('RGBA').save(p)
        calls.append([p, fourcc])
        return plain_enc(img, fourcc)
    W.dds_encode = enc
    RB.B.W.dds_encode = enc
    RB.B.COLOR_SIZE, RB.B.NORMAL_SIZE = ((FP.PAC_COLOR, FP.PAC_NORMAL) if full else ((2048, 1024), (1024, 1024)))
    if full:      # (as full_port.build_pac: the bump sheet must not get the colour's alpha)
        def sheet(self, images, size, fill):
            if tuple(fill) != (128, 128, 255, 255):
                return o_sheet(self, images, size, fill)
            keep = RB._LASH.get('png')
            RB._LASH['png'] = None
            try:
                return o_sheet(self, images, size, fill)
            finally:
                RB._LASH['png'] = keep
        RB.OneCell.sheet = sheet
    else:
        RB.OneCell.sheet = o_sheet
    RB.B.model_budget.LIMIT = (1 << 30) if full else 1600
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        RB.main(['rock_build.py', path, pac])
    rep = json.loads(buf.getvalue())
    refs.append({'case': k, 'model': path, 'full': full, 'pac': pac, 'report': rep, 'encoded': calls})
    print(os.path.basename(os.path.dirname(os.path.dirname(path))), 'full' if full else 'classic', rep.get('ok'), [a.get('pages') for a in rep.get('attires', [])], len(calls), 'pictures encoded')
json.dump(refs, open(os.path.join(out_dir, 'build_ref.json'), 'w'), indent=1)
