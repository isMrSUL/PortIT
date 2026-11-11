"""Reference for the C# AEW port check: aew_bridge.Bridge + port_2k.build as the worker runs them (the bridge in the
port's work folder) -> as port_ref.py.
    python aewport_ref.py <out.json> <work dir> <game folder> <AEW paks> <wrestler folder>|<budget>..."""
import contextlib, io, json, os, sys, time
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import port_2k, aew_bridge  # noqa: E402

work, game, paks = sys.argv[2], sys.argv[3], sys.argv[4]
out = {}
for k, spec in enumerate(sys.argv[5:]):
    folder, budget = spec.split('|')
    d = os.path.join(work, 'py_%d' % k)
    log = []
    buf = io.StringIO()
    t0 = time.time()
    with contextlib.redirect_stdout(buf):
        bridge = aew_bridge.Bridge(paks, folder, d, log.append)
        pac, rep, report, info = port_2k.build(folder, bridge.attire, d, game, budget=int(budget), log=log.append, bridge=bridge)
    out[str(k)] = {'aew': [paks, folder], 'char': folder, 'attire': bridge.attire, 'budget': int(budget), 'dir': d, 'log': log,
                   'printed': buf.getvalue().splitlines(), 'report': report, 'pages': rep['attires'][0]['pages'],
                   'checks': info.get('checks'), 'pack': [os.path.basename(p) for p in info.get('pack', [])], 'secs': time.time() - t0}
    json.dump(out, open(sys.argv[1], 'w'))
    print(folder, budget, rep['attires'][0]['pages'], 'pages', '%.0f s' % (time.time() - t0), flush=True)
