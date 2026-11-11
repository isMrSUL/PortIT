"""Reference for the C# port check: port_2k.build end to end (face paint, green test, setup, the attempts loop,
rock_model, fix_model, the builder, the limits, checks, the full route's texture pack) -> its log, printed lines,
report, page count, checks, card meshes, pack files; model.json / ch.pac left in each folder.
    python port_ref.py <out.json> <work dir> <game folder> <char folder>|<attire>|<budget>..."""
import contextlib, io, json, os, sys, time
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import port_2k  # noqa: E402

work, game = sys.argv[2], sys.argv[3]
out = {}
for k, spec in enumerate(sys.argv[4:]):
    char, att, budget = spec.split('|')
    d = os.path.join(work, 'py_%d' % k)
    log = []
    buf = io.StringIO()
    t0 = time.time()
    with contextlib.redirect_stdout(buf):
        pac, rep, report, info = port_2k.build(char, att, d, game, budget=int(budget), log=log.append)
    out[str(k)] = {'char': char, 'attire': att, 'budget': int(budget), 'dir': d, 'log': log, 'printed': buf.getvalue().splitlines(),
                   'report': report, 'pages': rep['attires'][0]['pages'], 'checks': info.get('checks'),
                   'pack': [os.path.basename(p) for p in info.get('pack', [])], 'secs': time.time() - t0}
    json.dump(out, open(sys.argv[1], 'w'))
    print(os.path.basename(char), att, budget, rep['attires'][0]['pages'], 'pages', '%.0f s' % (time.time() - t0), flush=True)
