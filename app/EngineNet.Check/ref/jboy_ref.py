"""Reference for the C# JBOY check: every JBOY model in the given pacs (nested PACH too) -> its counts, the sha256 of
jboy.write(jboy.read(d)) and of its triangle list (strip_to_tris over every strip).
    python jboy_ref.py <out.json> <pac>..."""
import hashlib, json, os, struct, sys
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, 'svr')]
import svrfmt, ch_tool, jboy  # noqa: E402

def h(b): return hashlib.sha256(bytes(b)).hexdigest()
out = {}
for pac in sys.argv[2:]:
    _, groups = ch_tool.epk8_read(open(pac, 'rb').read())
    recs = []
    def walk(path, blob):
        raw = svrfmt.unbpe(blob)
        if raw[:4] == b'PACH':
            for i, sub in svrfmt.pach_read(raw):
                walk('%s/%d' % (path, i), sub)
        elif raw[:4] == b'JBOY':
            m = jboy.read(raw)
            tri = bytearray()
            for s in m.meshes:
                for prim, idx in s.strips:
                    for t in jboy.strip_to_tris(idx):
                        tri += struct.pack('<3i', *t)
            recs.append([path, len(m.meshes), sum(len(s.verts) for s in m.meshes), len(m.nodes), len(m.textures),
                         h(jboy.write(m)), h(tri)])
    for typ, es in groups:
        for name, blob in es:
            walk(name.rstrip(b'\0').decode('latin1'), blob)
    out[os.path.abspath(pac)] = recs
json.dump(out, open(sys.argv[1], 'w'), indent=0)
print('reference written:', sum(len(r) for r in out.values()), 'JBOY models')
