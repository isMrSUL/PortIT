"""Reference for the C# fix_model check: full_port.fix_model on the model.json files rockmodel_ref.py left (each copied
first: <dir>/fix_in.json stays as it was, <dir>/fix_py.json is fixed), with the wrestler's card meshes from
port_2k.setup -> the cards and the lines it logged.
    python fixmodel_ref.py <out.json> <rock_ref.json> <game folder> [max mesh triangles] [gear_head 0/1]
(the last two: a stress run - split_big at a low size, the AEW branches (GEAR_HEAD, inner lips) on 2K models)"""
import contextlib, io, json, os, shutil, sys, time
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, x) for x in ('k26', 'svr', 'readers', 'portit')]
import port_2k, full_port as FP  # noqa: E402

ref = json.load(open(sys.argv[2]))
game = sys.argv[3]
template = os.path.join(game, 'pac', 'ch', 'ch125.pac')
os.environ['PORTIT_GAME'] = game
FP.install(port_2k, lambda *a: None)
if len(sys.argv) > 4:
    FP.MAX_MESH_TRIS = int(sys.argv[4])
if len(sys.argv) > 5:
    FP.GEAR_HEAD[0] = sys.argv[5] == '1'
tag = '' if len(sys.argv) <= 4 else '_stress'
out = {}
for k, r in ref.items():
    d = r['dir']
    os.environ['PORTIT_WORK'] = d
    FP.QUICK_READ[0] = True
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            R, info = port_2k.setup(r['char'], r['attire'])
    finally:
        FP.QUICK_READ[0] = False
    cards = port_2k.card_meshes(info)
    shutil.copy(os.path.join(d, 'model.json'), os.path.join(d, 'fix_in.json'))
    shutil.copy(os.path.join(d, 'model.json'), os.path.join(d, 'fix_py%s.json' % tag))
    lines = []
    t0 = time.time()
    FP.fix_model(os.path.join(d, 'fix_py%s.json' % tag), cards, template, lines.append)
    out[k] = {'char': r['char'], 'dir': d, 'cards': cards, 'log': lines, 'secs': time.time() - t0, 'py': 'fix_py%s.json' % tag,
              'max_tris': FP.MAX_MESH_TRIS, 'gear_head': FP.GEAR_HEAD[0]}
    json.dump(out, open(sys.argv[1], 'w'))
    print(os.path.basename(r['char']), len(cards), 'cards', '%.0f s' % (time.time() - t0), flush=True)
