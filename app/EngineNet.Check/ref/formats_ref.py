"""Reference fingerprints from the Python engine for the C# formats check (EngineNet.Check: formats).
    python formats_ref.py <out.json> <pac>...
For each pac: every EPK8 entry and every nested PACH entry -> sha256 of the unpacked bytes, of the re-packed BPE
(compress=True, the engine's own encoder) for up to 40 entries, and of the rebuilt EPK8 (w13_char.write_epk8 with the
pac itself as the template)."""
import hashlib, json, os, sys
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, 'svr')]
import svrfmt, ch_tool, w13_char  # noqa: E402

def h(b): return hashlib.sha256(bytes(b)).hexdigest()

out = {}
for pac in sys.argv[2:]:
    d = open(pac, 'rb').read()
    hdr, groups = ch_tool.epk8_read(d)
    rec = {'entries': [], 'encode': []}
    flat = []
    def walk(path, blob):
        raw = svrfmt.unbpe(blob)
        rec['entries'].append([path, len(raw), h(raw)])
        flat.append((path, raw))
        if raw[:4] == b'PACH':
            for i, sub in svrfmt.pach_read(raw):
                walk('%s/%d' % (path, i), sub)
    ents = []
    for typ, es in groups:
        for name, blob in es:
            walk(name.rstrip(b'\0').decode('latin1'), blob)
            ents.append((typ, name, blob))
    for path, raw in [f for f in flat if f[1][:4] != b'PACH'][:40]:
        rec['encode'].append([path, h(svrfmt.bpe_encode(raw, compress=True)), h(svrfmt.bpe_encode(raw, compress=False))])
    tmp = pac + '.refcopy'
    w13_char.write_epk8(pac, ents, tmp)
    rec['epk8'] = h(open(tmp, 'rb').read())
    os.remove(tmp)
    out[os.path.abspath(pac)] = rec
json.dump(out, open(sys.argv[1], 'w'), indent=0)
print('reference written:', sum(len(r['entries']) for r in out.values()), 'entries,', sum(len(r['encode']) for r in out.values()), 'encodes')
