"""Reference for the C# XXH3 check: xxh3.xxh3_64 on bytes(i * 131 % 251 for i in range(n)) for n = 0..2100 and on a
4 MB block, and full_port.pac_texture_hashes on the given full-route pacs.
    python hash_ref.py <out.json> <pac>..."""
import json, os, sys, tempfile
E = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Engine')
sys.path[:0] = [os.path.join(E, 'k26'), os.path.join(E, 'svr'), os.path.join(E, 'readers'), os.path.join(E, 'portit')]
import xxh3, full_port  # noqa: E402
data = bytes(i * 131 % 251 for i in range(4 << 20))
out = {'lengths': ['%016x' % xxh3.xxh3_64(data[:n]) for n in range(2101)], 'big': '%016x' % xxh3.xxh3_64(data), 'pacs': {}}
for pac in sys.argv[2:]:
    with tempfile.TemporaryDirectory() as d:
        out['pacs'][os.path.abspath(pac)] = full_port.pac_texture_hashes(pac, d)
json.dump(out, open(sys.argv[1], 'w'))
print('reference written')
