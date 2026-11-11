"""Reference for the C# pack check: jboy_2k.extract on each pack (the engine's own cache) -> the folder, the sha256 of
models.bin and of every file under the wrestler folder except the pointer files; the pointer files' paths are written
to <list.txt> for the model check (mcd_ref.py).
    python pack_ref.py <out.json> <list.txt> <pac>..."""
import hashlib, json, os, sys
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, 'readers')]
import jboy_2k  # noqa: E402

def h(p): return hashlib.sha256(open(p, 'rb').read()).hexdigest()
out, ptrs = {}, []
for pac in sys.argv[3:]:
    char = jboy_2k.extract(pac, log=lambda *a: None)
    root = os.path.dirname(char)
    files = {}
    for dp, dn, fn in os.walk(char):
        for f in fn:
            p = os.path.join(dp, f)
            rel = os.path.relpath(p, char)
            if f.lower().endswith('.mdl'):
                ptrs.append(p)
                files[rel] = json.loads(open(p, 'rb').read()[4:].decode('utf-8'))['part']
            else:
                files[rel] = h(p)
    out[os.path.abspath(pac)] = {'key': jboy_2k._key(pac), 'name': os.path.basename(char), 'models': h(os.path.join(root, 'models.bin')), 'files': files}
json.dump(out, open(sys.argv[1], 'w'), indent=0)
open(sys.argv[2], 'w', encoding='utf-8').write('\n'.join(ptrs))
print('reference written:', len(out), 'packs,', len(ptrs), 'parts')
