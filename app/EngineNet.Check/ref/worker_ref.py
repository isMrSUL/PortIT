"""Reference for the C# worker check: Engine/worker.py run as PortIT runs it (python -I -B worker.py req resp, one
process a request) on each request of requests.json ("{ROOT}" in a request -> root) -> the answers and the lines
it printed.
    python worker_ref.py <out.json> <root> <requests.json>"""
import json, os, subprocess, sys, time
W = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine', 'worker.py')
out_path, root, reqs = sys.argv[1], sys.argv[2], json.load(open(sys.argv[3], encoding='utf-8'))
os.makedirs(os.path.join(root, '_jobs'), exist_ok=True)
out = {}
for name, req in reqs.items():
    req = json.loads(json.dumps(req).replace('{ROOT}', json.dumps(root)[1:-1]))
    for k in req.pop('_setup', []):          # (fresh copies of folders a request writes into)
        src, dst = k
        import shutil
        shutil.rmtree(dst.replace('{ROOT}', root), ignore_errors=True)
        shutil.copytree(src, dst.replace('{ROOT}', root))
    rq, rs = [os.path.join(root, '_jobs', name + x) for x in ('_req.json', '_resp.json')]
    json.dump(req, open(rq, 'w', encoding='utf-8'))
    if os.path.exists(rs):
        os.remove(rs)
    t = time.time()
    p = subprocess.run([sys.executable, '-I', '-B', W, rq, rs], capture_output=True, text=True, encoding='utf-8')
    r = json.load(open(rs, encoding='utf-8')) if os.path.exists(rs) else {'ok': False, 'error': 'no response: ' + p.stderr[-800:]}
    out[name] = {'request': req, 'response': r, 'stdout': p.stdout.splitlines(), 'secs': time.time() - t}
    json.dump(out, open(out_path, 'w', encoding='utf-8'))
    print(name, 'ok' if r.get('ok') else 'FAILED ' + str(r.get('error'))[:300], '%.0f s' % (time.time() - t), flush=True)
