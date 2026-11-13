// portit-check: the C# engine against the Python engine's reference results on the same files.
//   portit-check formats <ref.json>     (ref\formats_ref.py: BPE decode / encode and EPK8 rebuild, every entry)
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortIT.Engine.Formats;
using PortIT.Engine.Imaging;
using PortIT.Engine.Port;
using PortIT.Engine.Readers;
using PortIT.Engine.Build;

static string H(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
static string HF(IEnumerable<double> v) { var l = v.ToArray(); var b = new byte[8 * l.Length]; for (int i = 0; i < l.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(8 * i), l[i]); return H(b); }
static string HL(IEnumerable<long> v) { var l = v.ToArray(); var b = new byte[8 * l.Length]; for (int i = 0; i < l.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(8 * i), l[i]); return H(b); }
static double Rms(Img a, Img b)
{
    double e = 0;
    for (int q = 0; q < a.Px.Length; q++) { double dd = a.Px[q] - b.Px[q]; e += dd * dd; }
    return Math.Sqrt(e / a.Px.Length);
}
static string NpDtype(string dt) => dt is "i1" or "u1" ? "|" + dt : dt;
static string Py(object? v) => v switch
{
    null => "null",
    bool b => b ? "true" : "false",
    string t => PyEsc(t),
    long l => l.ToString(),
    double d => PyFloat(d),
    List<object?> l => "[" + string.Join(", ", l.Select(Py)) + "]",
    Dictionary<string, object?> m => "{" + string.Join(", ", m.Select(kv => PyEsc(kv.Key) + ": " + Py(kv.Value))) + "}",
    _ => PyEsc(v.ToString() ?? ""),
};
static string PyEsc(string t)
{
    var sb = new System.Text.StringBuilder("\"");
    foreach (char c in t)
    {
        if (c == '"') sb.Append("\\\""); else if (c == '\\') sb.Append("\\\\");
        else if (c == '\n') sb.Append("\\n"); else if (c == '\r') sb.Append("\\r"); else if (c == '\t') sb.Append("\\t");
        else if (c < 0x20 || c > 0x7E) sb.Append($"\\u{(int)c:x4}");
        else sb.Append(c);
    }
    return sb.Append('"').ToString();
}
static string PyFloat(double d)
{
    if (double.IsNaN(d)) return "NaN";
    if (double.IsInfinity(d)) return d > 0 ? "Infinity" : "-Infinity";
    if (d == Math.Floor(d) && Math.Abs(d) < 1e16) return d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    var r = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    if (r.Contains('E'))
    {
        // (Python repr: 1e-05, 1.5e+20)
        var parts = r.Split('E');
        int e = int.Parse(parts[1]);
        r = parts[0] + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00");
    }
    return r;
}
static string[] HI(Img im) => [im.Mode, im.W.ToString(), im.H.ToString(), H(im.Px)];

if (args.Length < 2) { Console.WriteLine("usage: portit-check <check> <ref>"); return 2; }
// (PORTIT_CULTURE=es-ES: run as on a Spanish Windows - decimal commas - user reports 2026-11-13)
if (Environment.GetEnvironmentVariable("PORTIT_CULTURE") is { Length: > 0 } cul)
{
    var ci = new System.Globalization.CultureInfo(cul);
    System.Globalization.CultureInfo.DefaultThreadCurrentCulture = ci; System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = ci;
    System.Globalization.CultureInfo.CurrentCulture = ci; System.Globalization.CultureInfo.CurrentUICulture = ci;
    Console.WriteLine("culture: " + ci.Name + " (decimal '" + ci.NumberFormat.NumberDecimalSeparator + "')");
}
int bad = 0, ok = 0;
var t0 = System.Diagnostics.Stopwatch.StartNew();
switch (args[0])
{
    case "kraken":
    {
        // (args[1..]: folders; every Oodle block of each .utoc/.ucas and every OODL/KRKN member of each .pac is unpacked by
        //  the oo2core DLL and by Readers/Kraken.cs, and the bytes are compared; args "every=N" checks one block in N)
        int every = 1;
        var roots = new List<string>();
        foreach (var a in args.Skip(1)) if (a.StartsWith("every=")) every = int.Parse(a[6..]); else roots.Add(a);
        long bytesIn = 0, bytesOut = 0;
        var codecs = new SortedDictionary<string, int>();
        void One(byte[] src, int csz, int usz, string what)
        {
            var a = new byte[usz + 64];
            long got = Oodle.Decompress(src, csz, a, usz);
            var b = new byte[usz];
            string mine;
            try { Kraken.Decode(src.AsSpan(0, csz), b); mine = "ok"; }
            catch (Exception ex) { mine = ex.GetType().Name + ": " + ex.Message; }
            string key = csz >= 2 ? $"b0={src[0]:X2} codec={src[1] & 0x7F}" : "short";
            codecs[key] = codecs.GetValueOrDefault(key) + 1;
            if (got != usz) { if (mine == "ok") { bad++; Console.WriteLine($"BAD {what}: the DLL failed ({got}) but C# decoded it"); } else ok++; return; }
            if (mine != "ok") { bad++; Console.WriteLine($"BAD {what}: {mine}"); return; }
            if (!a.AsSpan(0, usz).SequenceEqual(b))
            {
                int i = 0; while (a[i] == b[i]) i++;
                bad++; Console.WriteLine($"BAD {what}: bytes differ from {i} of {usz}");
                return;
            }
            ok++; bytesIn += csz; bytesOut += usz;
        }
        foreach (var root in roots)
        {
            foreach (var utoc in Directory.EnumerateFiles(root, "*.utoc", SearchOption.AllDirectories).Order())
            {
                using var c = new IoContainer(utoc);
                using var ucas = File.OpenRead(Path.ChangeExtension(utoc, ".ucas"));
                int n = 0;
                for (int bi = 0; bi < c.Blocks.Count; bi++)
                {
                    var (boff, csz, usz, meth) = c.Blocks[bi];
                    if (c.Methods[meth] == "None" || bi % every != 0) continue;
                    var raw = new byte[csz];
                    ucas.Seek(boff, SeekOrigin.Begin);
                    ucas.ReadExactly(raw);
                    One(raw, csz, usz, $"{Path.GetFileName(utoc)} block {bi}");
                    n++;
                }
                Console.WriteLine($"{Path.GetFileName(utoc)}: {n} blocks ({c.Methods.Count - 1} methods: {string.Join(",", c.Methods.Skip(1))})  ok {ok} bad {bad}");
            }
            foreach (var pac in Directory.EnumerateFiles(root, "*.pac", SearchOption.AllDirectories).Order())
            {
                var b = File.ReadAllBytes(pac);
                int n = 0;
                var oodl = "OODL"u8.ToArray();
                for (int o = b.AsSpan().IndexOf(oodl); o >= 0;)
                {
                    if (o + 24 <= b.Length && b.AsSpan(o + 12, 4).SequenceEqual("KRKN"u8))
                    {
                        int usize = BitConverter.ToInt32(b, o + 16), csize = BitConverter.ToInt32(b, o + 20);
                        if (o + 24 + csize <= b.Length) { One(b.AsSpan(o + 24, csize).ToArray(), csize, usize, $"{pac} member @{o}"); n++; }
                    }
                    int nx = b.AsSpan(o + 4).IndexOf(oodl);
                    o = nx < 0 ? -1 : o + 4 + nx;
                }
                if (n > 0) Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(pac))}/{Path.GetFileName(pac)}: {n} Oodle members  ok {ok} bad {bad}");
            }
        }
        foreach (var kv in codecs) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        Console.WriteLine($"compressed {bytesIn:N0} -> {bytesOut:N0} bytes compared");
        break;
    }
    case "pacextract":
    {
        // (args[1]: a folder of chNNNNN.pac files; args[2]: a work cache folder) - each character pack is extracted into
        //  args[2] and compared, file by file, with PortIT's own cache of it (made earlier, by the oo2core DLL)
        foreach (var pac in Directory.EnumerateFiles(args[1], "ch*.pac", SearchOption.AllDirectories).Order())
        {
            string key = YukesPack.Key(pac), old = Path.Combine(YukesPack.CacheRoot, key);
            string got = YukesPack.Extract(pac, cacheRoot: args[2]);
            string mine = Path.Combine(args[2], key);
            if (!Directory.Exists(old)) { Console.WriteLine($"{pac}: extracted ({Directory.EnumerateFiles(mine, "*", SearchOption.AllDirectories).Count()} files), no old cache to compare"); continue; }
            var a = Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(old, f)).Order().ToList();
            var b = Directory.EnumerateFiles(mine, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(mine, f)).Order().ToList();
            int same = 0, diff = 0;
            foreach (var f in a.Union(b))
            {
                var pa = Path.Combine(old, f); var pb = Path.Combine(mine, f);
                if (File.Exists(pa) && File.Exists(pb) && File.ReadAllBytes(pa).AsSpan().SequenceEqual(File.ReadAllBytes(pb))) same++;
                else { diff++; Console.WriteLine($"   DIFF {f} (old {File.Exists(pa)}, new {File.Exists(pb)})"); }
            }
            Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(pac))}/{Path.GetFileName(pac)}: {same} files same, {diff} different");
            if (diff == 0) ok++; else bad++;
        }
        var oo = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Where(m => m.ModuleName.StartsWith("oo2core", StringComparison.OrdinalIgnoreCase)).Select(m => m.FileName).ToList();
        Console.WriteLine("oo2core loaded: " + (oo.Count == 0 ? "no" : string.Join(", ", oo)));
        break;
    }
    case "formats":
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var pac in doc.RootElement.EnumerateObject())
        {
            var data = File.ReadAllBytes(pac.Name);
            var arc = Epk8.Read(data);
            var flat = new Dictionary<string, byte[]>();
            var mine = new List<(string Path, int Len, string Hash)>();
            void Walk(string path, byte[] blob)
            {
                var raw = Bpe.Unpack(blob);
                mine.Add((path, raw.Length, H(raw)));
                flat[path] = raw;
                if (Pach.IsPach(raw))
                    foreach (var e in Pach.Read(raw)) Walk($"{path}/{e.Id}", e.Data);
            }
            var ents = new List<(byte[], byte[])>();
            foreach (var g in arc.Groups)
                foreach (var e in g.Entries) { Walk(e.NameText, e.Data); ents.Add((e.Name, e.Data)); }
            var refEnts = pac.Value.GetProperty("entries").EnumerateArray().ToList();
            if (refEnts.Count != mine.Count) { Console.WriteLine($"  {pac.Name}: {mine.Count} entries, Python {refEnts.Count}"); bad++; }
            for (int i = 0; i < Math.Min(refEnts.Count, mine.Count); i++)
            {
                var r = refEnts[i];
                if (r[0].GetString() != mine[i].Path || r[1].GetInt32() != mine[i].Len || r[2].GetString() != mine[i].Hash)
                { Console.WriteLine($"  DIFF unpack {mine[i].Path}: {mine[i].Len} vs Python {r[1].GetInt32()}"); bad++; }
                else ok++;
            }
            foreach (var r in pac.Value.GetProperty("encode").EnumerateArray())
            {
                var raw = flat[r[0].GetString()!];
                if (H(Bpe.Encode(raw, true)) != r[1].GetString()) { Console.WriteLine($"  DIFF bpe pack {r[0]}"); bad++; } else ok++;
                if (H(Bpe.Encode(raw, false)) != r[2].GetString()) { Console.WriteLine($"  DIFF bpe stored {r[0]}"); bad++; } else ok++;
                // (and our own packing unpacks back to the same bytes)
                if (!Bpe.Decode(Bpe.Encode(raw, true)).AsSpan().SequenceEqual(raw)) { Console.WriteLine($"  round trip {r[0]}"); bad++; } else ok++;
            }
            if (H(Epk8.Write(data, ents)) != pac.Value.GetProperty("epk8").GetString()) { Console.WriteLine($"  DIFF epk8 rebuild {pac.Name}"); bad++; }
            else ok++;
            Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(pac.Name))}/{Path.GetFileName(pac.Name)}: {mine.Count} entries checked");
        }
        break;
    }
    case "jboy":
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var pac in doc.RootElement.EnumerateObject())
        {
            var models = new List<(string Path, byte[] Raw)>();
            void Walk(string path, byte[] blob)
            {
                var raw = Bpe.Unpack(blob);
                if (Pach.IsPach(raw)) foreach (var e in Pach.Read(raw)) Walk($"{path}/{e.Id}", e.Data);
                else if (Jboy.IsJboy(raw)) models.Add((path, raw));
            }
            foreach (var g in Epk8.Read(File.ReadAllBytes(pac.Name)).Groups)
                foreach (var e in g.Entries) Walk(e.NameText, e.Data);
            var refs = pac.Value.EnumerateArray().ToList();
            if (refs.Count != models.Count) { Console.WriteLine($"  {pac.Name}: {models.Count} models, Python {refs.Count}"); bad++; }
            for (int i = 0; i < Math.Min(refs.Count, models.Count); i++)
            {
                var r = refs[i];
                var m = Jboy.Read(models[i].Raw);
                var tri = new MemoryStream();
                foreach (var s in m.Meshes)
                    foreach (var (_, idx) in s.Strips)
                        foreach (var (a, b, c) in Jboy.StripToTris(idx))
                        { tri.Write(BitConverter.GetBytes(a)); tri.Write(BitConverter.GetBytes(b)); tri.Write(BitConverter.GetBytes(c)); }
                var got = new object[] { models[i].Path, m.Meshes.Count, m.Meshes.Sum(s => s.Verts.Count), m.Nodes.Count, m.Textures.Count,
                                         H(m.Write()), H(tri.ToArray()) };
                bool same = r[0].GetString() == (string)got[0] && r[1].GetInt32() == (int)got[1] && r[2].GetInt32() == (int)got[2]
                            && r[3].GetInt32() == (int)got[3] && r[4].GetInt32() == (int)got[4];
                if (!same) { Console.WriteLine($"  DIFF counts {got[0]}"); bad++; } else ok++;
                if (r[5].GetString() != (string)got[5]) { Console.WriteLine($"  DIFF rewrite {got[0]}"); bad++; } else ok++;
                if (r[6].GetString() != (string)got[6]) { Console.WriteLine($"  DIFF triangles {got[0]}"); bad++; } else ok++;
            }
            Console.WriteLine($"{Path.GetFileName(pac.Name)}: {models.Count} models checked");
        }
        break;
    }
    case "images":
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var f in doc.RootElement.EnumerateObject())
        {
            Img im;
            try { im = ImageIO.Load(f.Name); }
            catch (Exception e) { Console.WriteLine($"  {Path.GetFileName(f.Name)}: {e.Message}"); bad++; continue; }
            int w = im.W, hh = im.H;
            var a = im.ToRgba();
            int w2 = Math.Max(1, (int)(w * 0.37)), h2 = Math.Max(1, (int)(hh * 0.53));
            var mine = new Dictionary<string, Func<Img>>
            {
                ["open"] = () => im,
                ["rgb_lanczos"] = () => Resample.Resize(a.ToRgb(), w2, h2, Filter.Lanczos),
                ["alpha_lanczos"] = () => Resample.Resize(a.Channel(3), w2, h2, Filter.Lanczos),
                ["rgb_bilinear"] = () => Resample.Resize(a.ToRgb(), w / 3 + 1, Math.Max(1, hh / 2), Filter.Bilinear),
                ["apart_box"] = () => Resample.ResizeApart(a, Math.Max(1, w / 2), Math.Max(1, hh / 2)),
                ["rgba_premul_lanczos"] = () => Resample.ResizeRgbaPremultiplied(a, w2, h2, Filter.Lanczos),
                ["to_l"] = () => a.ToL(),
                ["rgb_up_lanczos"] = () => Resample.Resize(a.ToRgb(), w * 2 + 3, hh * 2 + 1, Filter.Lanczos),
            };
            var diffs = new List<string>();
            foreach (var op in f.Value.EnumerateObject())
            {
                var r = op.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32().ToString() : x.GetString()!).ToArray();
                var got = HI(mine[op.Name]());
                if (r.SequenceEqual(got)) ok++;
                else { diffs.Add($"{op.Name} ({string.Join(" ", got.Take(3))} vs Python {string.Join(" ", r.Take(3))})"); bad++; }
            }
            Console.WriteLine($"{Path.GetFileName(f.Name)} {im.Mode} {w}x{hh}: " + (diffs.Count == 0 ? "all same" : "DIFF " + string.Join(", ", diffs)));
        }
        break;
    }
    case "dxt":
    {
        // (our DXT encoder: the same layout and header as the engine's Pillow files, and an error no worse than theirs
        //  (+ 2 %, + 0.05 levels); decoded with our Pillow-exact decoder)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var f in doc.RootElement.EnumerateObject())
        {
            var src = ImageIO.Load(f.Name).ToRgba();
            var line = new List<string>();
            foreach (var fc in new[] { "DXT5", "DXT1" })
            {
                var r = f.Value.GetProperty(fc);
                var s = fc == "DXT5" ? src : src.ToRgb().ToRgba();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var d = DdsWriter.Encode(s, fc);
                double secs = sw.Elapsed.TotalSeconds;
                var top = Dds.Decode(d);
                double er = 0, ea = 0;
                for (int i = 0; i < s.W * s.H; i++)
                {
                    for (int c = 0; c < 3; c++) { double e = top.Px[4 * i + c] - s.Px[4 * i + c]; er += e * e; }
                    double q = top.Px[4 * i + 3] - s.Px[4 * i + 3]; ea += q * q;
                }
                er = Math.Sqrt(er / (3.0 * s.W * s.H)); ea = Math.Sqrt(ea / (s.W * (double)s.H));
                double pr = r.GetProperty("rmse_rgb").GetDouble(), pa = r.GetProperty("rmse_a").GetDouble();
                bool layout = Convert.ToHexStringLower(d.AsSpan(0, 128)) == r.GetProperty("header").GetString() && d.Length == r.GetProperty("size").GetInt32();
                bool quality = er <= pr * 1.02 + 0.05 && ea <= pa * 1.02 + 0.05;
                if (layout) ok++; else { bad++; line.Add($"{fc} LAYOUT DIFF ({d.Length} vs {r.GetProperty("size").GetInt32()})"); }
                if (quality) ok++; else bad++;
                line.Add($"{fc} error {er:F3} (Pillow {pr:F3}){(fc == "DXT5" ? $", alpha {ea:F3} (Pillow {pa:F3})" : "")}{(quality ? "" : " WORSE")} {secs:F1}s");
            }
            Console.WriteLine($"{Path.GetFileName(f.Name)}: " + string.Join("; ", line));
        }
        break;
    }
    case "hash":
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        var data = new byte[4 << 20];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 131 % 251);
        var lens = doc.RootElement.GetProperty("lengths").EnumerateArray().Select(x => x.GetString()).ToList();
        int lenBad = 0;
        for (int n = 0; n < lens.Count; n++) if (TexturePackHash.Hex(data.AsSpan(0, n)) != lens[n]) lenBad++;
        if (lenBad == 0) ok += lens.Count; else { bad += lenBad; Console.WriteLine($"  {lenBad} lengths differ"); }
        if (TexturePackHash.Hex(data) == doc.RootElement.GetProperty("big").GetString()) ok++; else { bad++; Console.WriteLine("  4 MB differs"); }
        Console.WriteLine($"XXH3 on lengths 0-{lens.Count - 1} and 4 MB: {(lenBad == 0 ? "all same" : "DIFF")}");
        foreach (var pac in doc.RootElement.GetProperty("pacs").EnumerateObject())
        {
            var mine = TexturePackHash.PacTextureHashes(File.ReadAllBytes(pac.Name));
            foreach (var kv in pac.Value.EnumerateObject())
                if (mine.TryGetValue(kv.Name, out var v) && v == kv.Value.GetString()) ok++;
                else { bad++; Console.WriteLine($"  DIFF {kv.Name}"); }
            Console.WriteLine($"{Path.GetFileName(pac.Name)}: color {mine.GetValueOrDefault("color")}, normal {mine.GetValueOrDefault("normal")}");
        }
        break;
    }
    case "mcd":
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        double worst = 0;
        foreach (var f in doc.RootElement.EnumerateObject())
        {
            var r = f.Value;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; if (diffs.Count < 8) diffs.Add(what); } }
            Mcd m;
            try { m = Mcd.Load(f.Name); }
            catch (Exception e)
            {
                bool pyErr = r.TryGetProperty("error", out _);
                Same("error " + e.Message, pyErr);
                Console.WriteLine($"{f.Name}: C# {e.GetType().Name} {e.Message}, Python {(pyErr ? r.GetProperty("error").GetString() : "read it")}");
                continue;
            }
            if (r.TryGetProperty("error", out var pe)) { Same("Python failed: " + pe.GetString(), false); Console.WriteLine($"{f.Name}: DIFF {string.Join("; ", diffs)}"); continue; }
            Same("version", r.GetProperty("version").GetInt32() == m.Version);
            Same("bbox", r.GetProperty("bbox").GetString() == HF(m.Bbox));
            Same("strings", r.GetProperty("strings").GetString() == H(System.Text.Encoding.Latin1.GetBytes(string.Join("\n", m.Strings))));
            var bf = r.GetProperty("bone_f").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            Same("bone_f", bf[0] == m.BoneF0 && bf[1] == m.BoneF1);
            var bones = r.GetProperty("bones").EnumerateArray().ToList();
            Same("bone count", bones.Count == m.Bones.Count);
            for (int i = 0; i < Math.Min(bones.Count, m.Bones.Count); i++)
            {
                var b = bones[i]; var mb = m.Bones[i];
                Same($"bone {i}", b[0].GetString() == mb.Name && b[1].GetInt32() == mb.Parent && b[2].GetString() == HF(mb.T) && b[3].GetString() == HF(mb.Euler)
                     && b[4].GetInt32() == mb.Flag && b[5].GetInt32() == mb.Jiggle
                     && (b[6].ValueKind == JsonValueKind.Null ? mb.JiggleData == null : mb.JiggleData != null && b[6].GetString() == HF(mb.JiggleData.Select(x => (double)x))));
            }
            var world = r.GetProperty("world").EnumerateArray().ToList();
            for (int i = 0; i < Math.Min(world.Count, m.Bones.Count); i++)
            {
                var wv = world[i].EnumerateArray().Select(x => x.GetDouble()).ToArray();
                double d = 0;
                for (int k = 0; k < 16; k++) d = Math.Max(d, Math.Abs(wv[k] - m.Bones[i].World[k / 4, k % 4]));
                worst = Math.Max(worst, d);
                Same($"world {i} ({d:E1})", d == 0);   // (exact: the bone matrices feed the port)
            }
            var objs = r.GetProperty("objects").EnumerateArray().ToList();
            Same("objects", objs.Count == m.Objects.Count && objs.Select((o, i) => o[0].GetString() == m.Objects[i].Name && o[1].GetUInt32() == m.Objects[i].Hash).All(x => x));
            Same("gtpt", r.GetProperty("gtpt").GetArrayLength() == m.Gtpt.Count);
            var meshes = r.GetProperty("meshes").EnumerateArray().ToList();
            Same("mesh count", meshes.Count == m.Meshes.Count);
            for (int k = 0; k < Math.Min(meshes.Count, m.Meshes.Count); k++)
            {
                var j = meshes[k]; var mb = m.Meshes[k];
                Same($"mesh {k} material {mb.Material} vs {j.GetProperty("material").GetString()}", j.GetProperty("material").GetString() == mb.Material);
                Same($"mesh {k} object", j.GetProperty("object").GetString() == mb.Object);
                Same($"mesh {k} bbox", j.GetProperty("bbox").GetString() == HF(mb.Bbox) && j.GetProperty("vertices").GetInt32() == mb.Vertices);
                var sts = j.GetProperty("streams").EnumerateArray().ToList();
                Same($"mesh {k} stream count", sts.Count == mb.Streams.Count);
                for (int q = 0; q < Math.Min(sts.Count, mb.Streams.Count); q++)
                {
                    var a = sts[q]; var st = mb.Streams[q];
                    Same($"mesh {k} stream {st.Sem} {st.Format} {st.Dtype}", a[0].GetString() == st.Sem && a[1].GetString() == st.Format && a[2].GetString() == st.Type
                         && a[3].GetString() == NpDtype(st.Dtype) && a[4][0].GetInt32() == st.N && a[4][1].GetInt32() == st.C && a[5].GetString() == HF(st.V));
                }
                var sk = j.GetProperty("skin");
                Same($"mesh {k} skin", sk[0][1].GetInt32() == mb.Influences && sk[1].GetString() == HL(mb.SkinBones.Select(x => (long)x)) && sk[2].GetString() == HF(mb.SkinWeights));
                Same($"mesh {k} remap", j.GetProperty("remap").GetString() == HL(mb.Remap.Select(x => (long)x)) && j.GetProperty("stats").GetString() == H(mb.Stats));
                var shs = j.GetProperty("shapes").EnumerateArray().ToList();
                Same($"mesh {k} shapes", shs.Count == mb.Shapes.Count && shs.Select((x, i) => x[0].GetString() == mb.Shapes[i].Name && x[1].GetString() == HL(mb.Shapes[i].Index)
                     && x[2].GetString() == HF(mb.Shapes[i].Delta) && x[3].GetString() == HL(mb.Shapes[i].Word1.Select(w => (long)w))).All(x => x));
                var uv = m.Uvs(k);
                var ju = j.GetProperty("uvs");
                Same($"mesh {k} uvs", ju.ValueKind == JsonValueKind.Null ? uv == null : uv != null && ju.GetString() == HF(uv.Select(x => (double)x)));
            }
            var lods = r.GetProperty("lods").EnumerateArray().ToList();
            Same("lod count", lods.Count == m.Lods.Count);
            for (int l = 0; l < Math.Min(lods.Count, m.Lods.Count); l++)
            {
                var row = lods[l].EnumerateArray().ToList();
                for (int k = 0; k < Math.Min(row.Count, m.Lods[l].Count); k++)
                {
                    var x = row[k]; var lm = m.Lods[l][k];
                    var subs = x[2].EnumerateArray().Select(sb => string.Join(",", sb.EnumerateArray().Select(v => v.GetInt64()))).ToList();
                    Same($"lod {l} mesh {k}", x[0].GetInt32() == lm.Mesh && x[1].GetString() == HL(lm.Triangles.Select(t => (long)t))
                         && subs.SequenceEqual(lm.Subsets.Select(sb => string.Join(",", sb))));
                }
            }
            Same("trailer", r.GetProperty("trailer").GetString() == H(m.Trailer));
            Same("female", r.GetProperty("female").GetBoolean() == Mcd.LooksFemale(f.Name));
            if (diffs.Count > 0) Console.WriteLine($"{f.Name}: DIFF {string.Join("; ", diffs)}");
        }
        Console.WriteLine($"bone world matrices: largest difference {worst:E2}");
        break;
    }
    case "pack":
    {
        // (2K19 - 2K22 packs: unpacked by C# into a test folder - the same models.bin and textures as the Python cache)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        string root = Path.Combine(Path.GetTempPath(), "portit_check_pac_" + Environment.ProcessId);
        foreach (var pac in doc.RootElement.EnumerateObject())
        {
            var r = pac.Value;
            var diffs = new List<string>();
            string key = YukesPack.Key(pac.Name);
            if (key == r.GetProperty("key").GetString()) ok++; else { bad++; diffs.Add($"cache key {key} vs {r.GetProperty("key").GetString()}"); }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string chr = YukesPack.Extract(pac.Name, null, _ => { }, root);
            double secs = sw.Elapsed.TotalSeconds;
            if (Path.GetFileName(chr) == r.GetProperty("name").GetString()) ok++; else { bad++; diffs.Add("folder name"); }
            if (H(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(chr)!, "models.bin"))) == r.GetProperty("models").GetString()) ok++; else { bad++; diffs.Add("models.bin"); }
            var mine = Directory.EnumerateFiles(chr, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(chr, f), f => f);
            foreach (var f in r.GetProperty("files").EnumerateObject())
            {
                if (!mine.TryGetValue(f.Name, out var path)) { bad++; diffs.Add("missing " + f.Name); continue; }
                bool same = f.Name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
                    ? JsonDocument.Parse(File.ReadAllBytes(path).AsMemory(4)).RootElement.GetProperty("part").GetString() == f.Value.GetString()
                    : H(File.ReadAllBytes(path)) == f.Value.GetString();
                if (same) ok++; else { bad++; diffs.Add(f.Name); }
            }
            if (mine.Count != r.GetProperty("files").EnumerateObject().Count()) { bad++; diffs.Add($"{mine.Count} files"); }
            Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(pac.Name))}/{Path.GetFileName(pac.Name)}: {mine.Count} files, {secs:F1} s{(diffs.Count == 0 ? ", all same" : " DIFF " + string.Join(", ", diffs.Take(6)))}");
        }
        try { Directory.Delete(root, true); } catch (IOException) { }
        break;
    }
    case "misc":
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        var root = doc.RootElement;
        var data = new byte[300];
        for (int i = 0; i < 300; i++) data[i] = (byte)(i * 37 % 251);
        var city = root.GetProperty("city").EnumerateArray().Select(x => x.GetString()).ToList();
        int cb = 0;
        for (int n = 0; n < city.Count; n++) if (CityHash.Hash64(data.AsSpan(0, n)).ToString("x16") != city[n]) cb++;
        foreach (var kv in root.GetProperty("import").EnumerateObject()) if (CityHash.ImportId(kv.Name).ToString("x16") != kv.Value.GetString()) cb++;
        if (cb == 0) ok += city.Count + 3; else bad += cb;
        Console.WriteLine($"CityHash64 on lengths 0-299 and import ids: {(cb == 0 ? "all same" : cb + " DIFF")}");
        int mb = 0, mo = 0;
        foreach (var kv in root.GetProperty("mtls").EnumerateObject())
        {
            List<(string, Dictionary<string, string>)> mine;
            if (kv.Name.StartsWith("colormaps:")) mine = Mtls.ColorMaps(kv.Name["colormaps:".Length..]).Select(x => (x.Key, new Dictionary<string, string> { ["c"] = x.Value })).ToList();
            else mine = Mtls.Read(kv.Name);
            var refl = kv.Value.EnumerateArray().ToList();
            bool same = refl.Count == mine.Count;
            for (int i = 0; same && i < refl.Count; i++)
            {
                same = refl[i][0].GetString() == mine[i].Item1;
                if (!same) break;
                if (kv.Name.StartsWith("colormaps:")) same = refl[i][1].GetString() == mine[i].Item2["c"];
                else
                {
                    var rp = refl[i][1].EnumerateObject().Select(x => (x.Name, x.Value.GetString())).ToList();
                    same = rp.Count == mine[i].Item2.Count && rp.Zip(mine[i].Item2).All(z => z.First.Name == z.Second.Key && z.First.Item2 == z.Second.Value);
                }
            }
            if (same) mo++; else { mb++; Console.WriteLine($"  DIFF mtls {kv.Name}"); }
        }
        ok += mo; bad += mb;
        Console.WriteLine($".mtls: {mo} files / folders same, {mb} different");
        var cons = new Dictionary<string, IoContainer>();
        foreach (var c in root.GetProperty("containers").EnumerateArray())
        {
            var path = c[0].GetString()!;
            var ic = new IoContainer(path);
            cons[path] = ic;
            string names = H(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", ic.Names.Select(n => $"{n.Index}={n.Name}"))));
            if (ic.Names.Count == c[1].GetInt32() && names == c[2].GetString()) ok++; else { bad++; Console.WriteLine($"  DIFF container names {Path.GetFileName(path)}"); }
        }
        Console.WriteLine($"{cons.Count} IoStore containers: names checked");
        string tmp = Path.Combine(Path.GetTempPath(), "portit_check_aew_" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        foreach (var w in root.GetProperty("aew").EnumerateArray())
        {
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; diffs.Add(what); } }
            var ic = cons[w.GetProperty("utoc").GetString()!];
            string full = w.GetProperty("name").GetString()!;
            int idx = ic.Names.First(n => n.Name == full).Index;
            var raw = ic.Read(idx);
            Same("container bytes", H(raw) == w.GetProperty("raw").GetString());
            string p = Path.Combine(tmp, Path.GetFileName(full));
            File.WriteAllBytes(p, raw);
            var m = AewMesh.Read(p);
            Same("props", H(System.Text.Encoding.UTF8.GetBytes(System.Text.RegularExpressions.Regex.Replace(Py(m.Props), @"<(\w+Property): [^>]*>", "<$1: error>"))) == w.GetProperty("props").GetString());
            if (Environment.GetEnvironmentVariable("PORTIT_CHECK_DUMP") is { Length: > 0 } dump) File.WriteAllText(Path.Combine(dump, w.GetProperty("wrestler").GetString() + ".cs.txt"), Py(m.Props));
            Same("bounds", HF(m.Bounds.Select(x => (double)x)) == w.GetProperty("bounds").GetString());
            var mats = w.GetProperty("materials").EnumerateArray().ToList();
            Same("materials", mats.Count == m.Materials.Count && mats.Select((x, i) => x[0].GetInt32() == m.Materials[i].Index && x[1].GetString() == m.Materials[i].Slot
                 && (x[2].ValueKind == JsonValueKind.Null ? m.Materials[i].Imported == null : x[2].GetString() == m.Materials[i].Imported)
                 && x[3].GetString() == HF(m.Materials[i].UvDensity.Select(v => (double)v))).All(b => b));
            var bones = w.GetProperty("bones").EnumerateArray().ToList();
            Same("bones", bones.Count == m.Bones.Count && bones.Select((x, i) => x[0].GetString() == m.Bones[i].Name && x[1].GetInt32() == m.Bones[i].Parent
                 && x[2].GetString() == HF(m.Bones[i].Q.Select(v => (double)v)) && x[3].GetString() == HF(m.Bones[i].T.Select(v => (double)v))
                 && x[4].GetString() == HF(m.Bones[i].S.Select(v => (double)v))).All(b => b));
            var lod = m.Lods[0];
            var secs = w.GetProperty("sections").EnumerateArray().ToList();
            Same("sections", secs.Count == lod.Sections.Count && secs.Select((x, i) =>
            {
                var sc = lod.Sections[i];
                return x[0].GetInt32() == sc.Flag && x[1].GetInt32() == sc.Material && x[2].GetInt32() == sc.BaseIndex && x[3].GetInt32() == sc.Triangles
                       && x[4].GetInt32() == sc.BaseVertex && x[5].GetInt32() == sc.Vertices && x[6].GetInt32() == sc.MaxInfluences && x[7].GetString() == HL(sc.BoneMap.Select(b => (long)b));
            }).All(b => b));
            Same("indices", HL(lod.Indices) == w.GetProperty("indices").GetString());
            Same("positions", HF(lod.Positions.Select(v => (double)v)) == w.GetProperty("positions").GetString());
            Same("tangents", HF(lod.TangentX.Select(v => (double)v)) == w.GetProperty("tx").GetString() && HF(lod.TangentZ.Select(v => (double)v)) == w.GetProperty("tz").GetString());
            Same("uvs", HF(lod.Uvs.Select(v => (double)v)) == w.GetProperty("uvs").GetString());
            Same("skin", HL(lod.SkinSlots.Select(v => (long)v)) == w.GetProperty("slots").GetString() && HF(lod.SkinWeights.Select(v => (double)v)) == w.GetProperty("weights").GetString());
            Same("global bones", HL(AewMesh.GlobalBones(lod).Select(v => (long)v)) == w.GetProperty("global").GetString());
            int nt = 0;
            foreach (var t in w.GetProperty("textures").EnumerateArray())
            {
                string tn = t[0].GetString()!;
                var hit = cons.Values.SelectMany(c => c.Names.Select(n => (c, n))).First(x => x.n.Name == tn);
                string tp = Path.Combine(tmp, Path.GetFileName(tn));
                File.WriteAllBytes(tp, hit.c.Read(hit.n.Index));
                string ub = tn[..^7] + ".ubulk";
                var ubh = hit.c.Names.FirstOrDefault(n => n.Name.Equals(ub, StringComparison.OrdinalIgnoreCase));
                if (ubh.Name != null) File.WriteAllBytes(tp[..^7] + ".ubulk", hit.c.Read(ubh.Index));
                var tx = AewTexture.Read(tp);
                var mips = t[4].EnumerateArray().ToList();
                Same($"texture {Path.GetFileName(tn)} header", tx.Fmt == t[1].GetString() && tx.W == t[2].GetInt32() && tx.H == t[3].GetInt32() && mips.Count == tx.Mips.Count
                     && mips.Select((x, i) => x[0].GetUInt32() == tx.Mips[i].Flags && x[1].GetInt32() == tx.Mips[i].Size && x[2].GetInt32() == tx.Mips[i].W
                                         && x[3].GetInt32() == tx.Mips[i].H && x[4].GetString() == tx.Mips[i].Src).All(b => b));
                var mb0 = tx.MipBytes(0);
                Same($"texture {Path.GetFileName(tn)} mip 0", H(mb0) == t[5].GetString());
                var un = AewTexture.UntilePs4(tx.Fmt, tx.Mips[0].W, tx.Mips[0].H, mb0);
                Same($"texture {Path.GetFileName(tn)} untiled", H(un) == t[6].GetString());
                string pix;
                try { pix = H(AewTexture.ToImage(tx.Fmt, tx.Mips[0].W, tx.Mips[0].H, un).ToRgba().Px); } catch (Exception) { pix = "error"; }
                Same($"texture {Path.GetFileName(tn)} pixels", pix == t[7].GetString());
                nt++;
            }
            Console.WriteLine($"AEW {w.GetProperty("wrestler").GetString()}: mesh {lod.Positions.Length / 3} vertices, {nt} textures{(diffs.Count == 0 ? ", all same" : " DIFF " + string.Join("; ", diffs))}");
        }
        foreach (var c in cons.Values) c.Dispose();
        try { Directory.Delete(tmp, true); } catch (IOException) { }
        break;
    }
    case "qem":
    {
        // (args[1]: the folder qem_ref.py wrote - its inputs per case and the Python results)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[1], "qem_ref.json")));
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var b = File.ReadAllBytes(Path.Combine(args[1], $"case_{c.GetProperty("case").GetInt32()}.bin"));
            int o = 0;
            int I() { int v = BitConverter.ToInt32(b, o); o += 4; return v; }
            int n = I(), m = I(), k = I(), target = I(), lockB = I();
            double[] D(int cnt) { var a = new double[cnt]; Buffer.BlockCopy(b, o, a, 0, 8 * cnt); o += 8 * cnt; return a; }
            var V = D(3 * n);
            var F = new int[3 * m]; Buffer.BlockCopy(b, o, F, 0, 12 * m); o += 12 * m;
            var A = D(n * k); var imp = D(n);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (keep, tris) = PortIT.Engine.Port.Qem.Simplify(V, F, target, null, imp, A, k, 2.0, lockBorders: lockB != 0);
            double secs = sw.Elapsed.TotalSeconds;
            bool sk = HL(keep.Select(x => (long)x)) == c.GetProperty("keep").GetString(), st = HL(tris.Select(x => (long)x)) == c.GetProperty("tris").GetString();
            if (sk) ok++; else bad++;
            if (st) ok++; else bad++;
            Console.WriteLine($"{c.GetProperty("what").GetString()}: kept {keep.Length} (Python {c.GetProperty("kept").GetInt32()}), {tris.Length / 3} triangles (Python {c.GetProperty("ntris").GetInt32()}) - {(sk && st ? "same" : "DIFF")}; C# {secs:F1} s, Python {c.GetProperty("secs").GetDouble():F1} s");
        }
        break;
    }
    case "build":
    {
        // (args[1]: the folder build_ref.py wrote; C# builds each model.json and its pac is compared part by part:
        //  models and every other part byte for byte, textures by layout and by error against the Python ones)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[1], "build_ref.json")));
        string stock = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Engine", "svr", "prop_bones_stock.json"));
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            string model = c.GetProperty("model").GetString()!;
            bool full = c.GetProperty("full").GetBoolean();
            string mine = Path.Combine(args[1], $"{c.GetProperty("case").GetInt32()}_cs.pac");
            var encoded = new List<(Img, string)>();
            DdsWriter.OnEncode = (im, fc) => encoded.Add((im.ToRgba(), fc));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rep = SvrBuilder.Run(model, mine, full, stock);
            DdsWriter.OnEncode = null;
            double secs = sw.Elapsed.TotalSeconds;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; diffs.Add(what); } }
            var pyRep = c.GetProperty("report");
            Same("ok", (bool)rep["ok"]! == pyRep.GetProperty("ok").GetBoolean());
            if (!(bool)rep["ok"]!) { Console.WriteLine($"{model}: C# error {rep["error"]}"); continue; }
            var pyPages = pyRep.GetProperty("attires").EnumerateArray().Select(a => a.GetProperty("pages").GetInt32()).ToList();
            var csPages = rep["attires"]!.AsArray().Select(a => (int)a!["pages"]!).ToList();
            Same($"pages {string.Join(",", csPages)} vs {string.Join(",", pyPages)}", pyPages.SequenceEqual(csPages));
            var pyEnc = c.GetProperty("encoded").EnumerateArray().ToList();
            Same($"pictures encoded {encoded.Count} vs {pyEnc.Count}", encoded.Count == pyEnc.Count);
            for (int q = 0; q < Math.Min(encoded.Count, pyEnc.Count); q++)
            {
                var pim = ImageIO.Load(pyEnc[q][0].GetString()!).ToRgba();
                bool eq = pyEnc[q][1].GetString() == encoded[q].Item2 && pim.W == encoded[q].Item1.W && pim.H == encoded[q].Item1.H && pim.Px.AsSpan().SequenceEqual(encoded[q].Item1.Px);
                Same($"picture {q} ({encoded[q].Item2} {encoded[q].Item1.W}x{encoded[q].Item1.H}) before encoding", eq);
            }
            var pa = Epk8.Read(File.ReadAllBytes(c.GetProperty("pac").GetString()!));
            var ca = Epk8.Read(File.ReadAllBytes(mine));
            Same("header", pa.Header.AsSpan(0, 4).SequenceEqual(ca.Header.AsSpan(0, 4)));
            var pe = pa.Groups[0].Entries.Where(e => e.Name.Any(b => b != 0)).ToList(); var ce = ca.Groups[0].Entries.Where(e => e.Name.Any(b => b != 0)).ToList();
            Same("entries", pe.Count == ce.Count && pe.Zip(ce).All(z => z.First.NameText == z.Second.NameText));
            double worst = 0;
            for (int i = 0; i < Math.Min(pe.Count, ce.Count); i++)
            {
                var pk = Pach.Read(Bpe.Unpack(pe[i].Data)).ToDictionary(x => x.Id, x => Bpe.Unpack(x.Data));
                var ck = Pach.Read(Bpe.Unpack(ce[i].Data)).ToDictionary(x => x.Id, x => Bpe.Unpack(x.Data));
                Same($"entry {i} parts", pk.Keys.OrderBy(k => k).SequenceEqual(ck.Keys.OrderBy(k => k)));
                foreach (var (id, pd) in pk)
                {
                    if (!ck.TryGetValue(id, out var cd)) continue;
                    if (Bundle.IsBundle(pd))
                    {
                        var pt = SvrBuilder.TexBundleRead(pd); var ct = SvrBuilder.TexBundleRead(cd);
                        Same($"part 0x{id:x} texture names", pt.Select(t => t.Item1).SequenceEqual(ct.Select(t => t.Item1)));
                        for (int t = 0; t < Math.Min(pt.Count, ct.Count); t++)
                        {
                            var (nm, pdds) = pt[t]; var cdds = ct[t].Item2;
                            if (pdds.AsSpan().SequenceEqual(cdds)) { ok++; continue; }
                            // (re-encoded by our DXT encoder: the same header and size, and the same picture within DXT error)
                            Same($"0x{id:x} {nm} header/size", pdds.Length == cdds.Length && pdds.AsSpan(0, 128).SequenceEqual(cdds.AsSpan(0, 128)));
                            // (each encoder against the picture both were given - proven identical above: ours no worse)
                            var a1 = Dds.Decode(pdds).ToRgba(); var a2 = Dds.Decode(cdds).ToRgba();
                            var src = encoded.Select(x => x.Item1).FirstOrDefault(x => x.W == a2.W && x.H == a2.H && Rms(x, a2) < 12) ?? a2;
                            double ePy = Rms(a1, src), eCs = Rms(a2, src);
                            worst = Math.Max(worst, eCs);
                            Same($"0x{id:x} {nm} encoding error {eCs:F2} vs Python's {ePy:F2}", eCs <= ePy * 1.02 + 0.05);
                        }
                    }
                    else Same($"part 0x{id:x} ({(Jboy.IsJboy(pd) ? "model" : "data")}) bytes", pd.AsSpan().SequenceEqual(cd));
                }
            }
            Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(model)))} {(full ? "full" : "classic")}: pages {string.Join(",", csPages)}, C# {secs:F1} s, our texture error at most {worst:F2}{(diffs.Count == 0 ? ", everything else byte-identical" : " DIFF " + string.Join("; ", diffs.Take(8)))}");
        }
        break;
    }
    case "fbx":
    {
        // (args[1]: fbx_ref.json; args[2]: a work folder for C#'s textures)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var c in doc.RootElement.EnumerateObject())
        {
            var r = c.Value;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; if (diffs.Count < 10) diffs.Add(what); } }
            string dir = Path.Combine(args[2], "cs_" + c.Name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var rf = new PortIT.Engine.Port.RockFbx();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (meshes, bones) = rf.Build(r.GetProperty("char").GetString()!, r.GetProperty("attire").GetString()!, dir);
            double secs = sw.Elapsed.TotalSeconds;
            var pyRep = r.GetProperty("report").EnumerateArray().Select(x => x.GetString()).ToList();
            Same($"report ({rf.Report.Count} vs {pyRep.Count} lines)", pyRep.SequenceEqual(rf.Report));
            if (!pyRep.SequenceEqual(rf.Report))
                for (int i = 0; i < Math.Max(pyRep.Count, rf.Report.Count); i++)
                    if (i >= pyRep.Count || i >= rf.Report.Count || pyRep[i] != rf.Report[i]) { diffs.Add($"line {i}: C# '{(i < rf.Report.Count ? rf.Report[i] : "")}' vs '{(i < pyRep.Count ? pyRep[i] : "")}'"); break; }
            var pb = r.GetProperty("bones").EnumerateArray().ToList();
            Same("bone count", pb.Count == bones.Count);
            double worst = 0;
            for (int i = 0; i < Math.Min(pb.Count, bones.Count); i++)
            {
                var b = pb[i];
                int? par = b[2].ValueKind == JsonValueKind.Null ? null : b[2].GetInt32();
                var wv = b[3].EnumerateArray().Select(x => x.GetDouble()).ToArray();
                bool exact = wv.SequenceEqual(bones[i].World);
                for (int q = 0; q < 16; q++) worst = Math.Max(worst, Math.Abs(wv[q] - bones[i].World[q]));
                Same($"bone {i} {bones[i].Src}", b[0].GetString() == bones[i].Name && b[1].GetString() == bones[i].Src && par == bones[i].Parent && exact);
            }
            var pm = r.GetProperty("meshes").EnumerateArray().ToList();
            Same("mesh count", pm.Count == meshes.Count);
            for (int i = 0; i < Math.Min(pm.Count, meshes.Count); i++)
            {
                var j = pm[i]; var m = meshes[i];
                Same($"{m.Name} name/material/texture", j.GetProperty("name").GetString() == m.Name && j.GetProperty("material").GetString() == m.Material
                     && (j.GetProperty("texture").ValueKind == JsonValueKind.Null ? m.Texture == null : j.GetProperty("texture").GetString() == m.Texture));
                Same($"{m.Name} verts", j.GetProperty("verts").GetString() == HF(m.Verts));
                Same($"{m.Name} normals", j.GetProperty("normals").GetString() == HF(m.Normals));
                Same($"{m.Name} uv", j.GetProperty("uv").GetString() == HF(m.Uv));
                Same($"{m.Name} tris", j.GetProperty("tris").GetString() == HL(m.Tris.Select(x => (long)x)));
                var pw = j.GetProperty("weights").EnumerateArray().ToList();
                Same($"{m.Name} weights", pw.Count == m.Weights.Count && pw.Select((x, q) => x[0].GetInt32() == m.Weights[q].Bone && x[1].GetString() == HF(m.Weights[q].W)).All(b => b));
                var ls = j.GetProperty("lip_src");
                Same($"{m.Name} lip sides", ls.ValueKind == JsonValueKind.Null ? m.LipSrc == null : m.LipSrc != null && ls.GetString() == HL(m.LipSrc.Select(x => (long)x)));
                var js = j.GetProperty("jaw_src");
                Same($"{m.Name} jaw amounts", js.ValueKind == JsonValueKind.Null ? m.JawSrc == null : m.JawSrc != null && js.GetString() == HF(m.JawSrc));
            }
            foreach (var t in r.GetProperty("textures").EnumerateObject())
            {
                var f = Path.Combine(dir, "textures", t.Name);
                Same($"texture {t.Name}", File.Exists(f) && H(ImageIO.Load(f).ToRgba().Px) == t.Value.GetString());
            }
            Console.WriteLine($"{Path.GetFileName(r.GetProperty("char").GetString())} {r.GetProperty("attire").GetString()}: {meshes.Count} meshes, {bones.Count} bones, C# {secs:F1} s{(diffs.Count == 0 ? ", all same" : " DIFF " + string.Join("; ", diffs))}");
        }
        break;
    }
    case "rng":
    {
        // (args[1]: pop:size pairs; prints the first 8 picks of ok = arange(pop) * 3 + 7 and their sum, as NumPy's choice)
        foreach (var a in args.Skip(1))
        {
            var ps = a.Split(':').Select(long.Parse).ToArray();
            var idx = PortIT.Engine.Util.NpRandom.Seed0().ChooseIndices(ps[0], ps[1]);
            var vals = idx.Select(i => i * 3 + 7).ToArray();
            Console.WriteLine($"{ps[0]} {ps[1]} [{string.Join(", ", vals.Take(8))}] {vals.Sum()}");
        }
        break;
    }
    case "rockmodel":
    {
        // (args[1]: rock_ref.json; args[2]: the template ch125.pac; args[3]: a work folder for C#'s output)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var c in doc.RootElement.EnumerateObject())
        {
            var r = c.Value;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; if (diffs.Count < 12) diffs.Add(what); } }
            string dir = Path.Combine(args[3], "cs_" + c.Name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var rm = new PortIT.Engine.Port.RockModel();
            var st = r.GetProperty("settings");
            (int, int, int, int) Box(JsonElement e) => (e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());
            rm.Cells = st.GetProperty("cells").EnumerateObject().ToDictionary(x => x.Name, x => Box(x.Value));
            rm.Sheet = (st.GetProperty("sheet")[0].GetInt32(), st.GetProperty("sheet")[1].GetInt32());
            rm.TmplMouthCell = (st.GetProperty("mouth_cell")[0].GetInt32(), st.GetProperty("mouth_cell")[1].GetInt32());
            rm.Shine = st.GetProperty("shine").EnumerateObject().ToDictionary(x => x.Name, x => (x.Value[0].GetDouble(), x.Value[1].GetDouble()));
            rm.Sweat = st.GetProperty("sweat").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32());
            rm.Solid = st.GetProperty("solid").EnumerateObject().ToDictionary(x => x.Name, x => (x.Value[0].GetInt32(), x.Value[1].GetInt32(), x.Value[2].GetInt32()));
            rm.Kind = st.GetProperty("kind").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!);
            rm.Target = st.GetProperty("target").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32());
            rm.DropMeshes = st.GetProperty("drop").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
            rm.Cutout = st.GetProperty("cutout").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
            rm.TexFirst = st.GetProperty("tex_first").EnumerateArray().Select(x => x.GetString()!).ToList();
            rm.NormalFlipG = st.GetProperty("flip_g").GetBoolean();
            rm.CornerSmooth = st.GetProperty("corner_smooth").GetInt32();
            rm.TemplateMouth = st.GetProperty("template_mouth").GetBoolean();
            rm.Fbx.NotGear = st.GetProperty("not_gear").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
            rm.Prepare = (ms, bs) =>
            {
                int head = bs.FindIndex(b => b.Src == "J_Head");
                foreach (var m in ms) if (m.Weights.Count == 0) { var one = new double[m.N]; Array.Fill(one, 1.0); m.Weights.Add((head, one)); }
                return ms;
            };
            rm.TieDebug = [];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (spec, report) = rm.Build(r.GetProperty("char").GetString()!, r.GetProperty("attire").GetString()!, dir, args[2]);
            double secs = sw.Elapsed.TotalSeconds;
            // (k26_face lists the face bones from a Python set of names: its order changes with every Python run -
            //  hash randomisation - so that line is compared as a set of items)
            static string? Canon(string? l) => l != null && l.StartsWith("face fit: bones placed by the stretch")
                ? l[..(l.IndexOf(": ", 10) + 2)] + string.Join(", ", l[(l.IndexOf(": ", 10) + 2)..].Split(", ").Order(StringComparer.Ordinal)) : l;
            void Lines(string what, List<string?> py0, List<string> cs0)
            {
                var py = py0.Select(Canon).ToList(); var cs = cs0.Select(x => Canon(x)!).ToList();
                Same($"{what} ({cs.Count} vs {py.Count} lines)", py.SequenceEqual(cs));
                for (int i = 0; i < Math.Max(py.Count, cs.Count); i++)
                    if (i >= py.Count || i >= cs.Count || py[i] != cs[i]) { diffs.Add($"{what} line {i}: C# '{(i < cs.Count ? cs[i] : "")}' vs '{(i < py.Count ? py[i] : "")}'"); break; }
            }
            Lines("report", r.GetProperty("report").EnumerateArray().Select(x => x.GetString()).ToList(), report);
            Lines("printed", r.GetProperty("printed").EnumerateArray().Select(x => x.GetString()).ToList(), rm.Fbx.Report.Concat(rm.Log).ToList());
            var py = JsonNode.Parse(File.ReadAllText(Path.Combine(r.GetProperty("dir").GetString()!, "model.json")))!.AsObject();
            var cs = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "model.json")))!.AsObject();
            // joints
            var pj = py["joints"]!.AsObject(); var cj = cs["joints"]!.AsObject();
            Same("joint names", pj.Select(kv => kv.Key).Order(StringComparer.Ordinal).SequenceEqual(cj.Select(kv => kv.Key).Order(StringComparer.Ordinal)));
            double jworst = 0; int jdiff = 0;
            foreach (var (k, v) in pj)
            {
                if (cj[k] is not JsonArray cv) continue;
                var pv = v!.AsArray();
                for (int q = 0; q < 3; q++) { double d = Math.Abs(pv[q]!.GetValue<double>() - cv[q]!.GetValue<double>()); if (d > 0) jdiff++; jworst = Math.Max(jworst, d); }
            }
            Same($"joints ({jdiff} values differ, most {jworst:G3})", jdiff == 0);
            Same("_rock", py["_rock"]!["lash_cell"]?.ToJsonString() == cs["_rock"]!["lash_cell"]?.ToJsonString()
                 && Enumerable.Range(0, 3).All(q => py["_rock"]!["origin_cm"]![q]!.GetValue<double>() == cs["_rock"]!["origin_cm"]![q]!.GetValue<double>()));
            var pm = py["attires"]![0]!["meshes"]!.AsArray(); var cmz = cs["attires"]![0]!["meshes"]!.AsArray();
            Same($"mesh count ({cmz.Count} vs {pm.Count})", pm.Count == cmz.Count);
            int edge = 0; double worst = 0; int wOrder = 0, wTie = 0;
            for (int i = 0; i < Math.Min(pm.Count, cmz.Count); i++)
            {
                var a = pm[i]!.AsObject(); var b = cmz[i]!.AsObject();
                string nm = a["name"]!.GetValue<string>();
                Same($"{nm} name/kind/bones/material", a["name"]!.ToJsonString() == b["name"]!.ToJsonString() && a["kind"]!.ToJsonString() == b["kind"]!.ToJsonString()
                     && a["bones"]!.ToJsonString() == b["bones"]!.ToJsonString() && (a["material"]?.ToJsonString() ?? "") == (b["material"]?.ToJsonString() ?? ""));
                Same($"{nm} triangles", a["triangles"]!.ToJsonString() == b["triangles"]!.ToJsonString());
                foreach (var f in new[] { "positions", "normals", "uvs" })
                {
                    var pa = a[f]!.AsArray(); var ca = b[f]!.AsArray();
                    bool same = pa.Count == ca.Count;
                    int nd = 0;
                    if (same)
                        for (int v = 0; v < pa.Count; v++)
                            for (int q = 0; q < pa[v]!.AsArray().Count; q++)
                            {
                                double d = Math.Abs(pa[v]![q]!.GetValue<double>() - ca[v]![q]!.GetValue<double>());
                                if (d > 0) { nd++; worst = Math.Max(worst, d); }
                            }
                    edge += nd;
                    // (a value one rounding step apart: a LAPACK last bit landing on the 5th / 6th decimal's edge)
                    Same($"{nm} {f} ({pa.Count} vs {ca.Count}, {nd} values differ)", same && (nd == 0 || worst <= 1.0001e-5));
                }
                var pw = a["weights"]!.AsArray(); var cw = b["weights"]!.AsArray();
                bool wsame = pw.Count == cw.Count;
                int wd = 0;
                if (wsame)
                    for (int v = 0; v < pw.Count; v++)
                    {
                        var lp = pw[v]!.AsArray().Select(x => (x![0]!.GetValue<int>(), x[1]!.GetValue<double>())).ToList();
                        var lc = cw[v]!.AsArray().Select(x => (x![0]!.GetValue<int>(), x[1]!.GetValue<double>())).ToList();
                        if (lp.SequenceEqual(lc)) continue;
                        // (the same weights, listed in another order: only where two are equal - NumPy's argsort is not stable)
                        bool tied = rm.TieDebug!.TryGetValue(nm, out var tv) && tv.Contains(v);
                        if (lp.Order().SequenceEqual(lc.Order()) && tied) wOrder++;
                        else if (tied) wTie++;     // (equal source weights: NumPy's pick)
                        else wd++;
                    }
                Same($"{nm} weights ({pw.Count} vs {cw.Count}, {wd} vertices differ)", wsame && wd == 0);
            }
            foreach (var s in new[] { "color", "normal" })
                Same($"{s}.png pixels", H(ImageIO.Load(Path.Combine(dir, s + ".png")).ToRgba().Px) == r.GetProperty(s).GetString());
            Console.WriteLine($"{Path.GetFileName(r.GetProperty("char").GetString())} {r.GetProperty("attire").GetString()}: {cmz.Count} meshes, C# {secs:F1} s vs Python {r.GetProperty("secs").GetDouble():F0} s;"
                + $" {edge} rounding-edge values (most {worst:G3}), {wOrder} tie vertices' weights in another order, {wTie} tie vertices with another pick{(diffs.Count == 0 ? ", all same" : " DIFF " + string.Join("; ", diffs))}");
        }
        break;
    }
    case "fixmodel":
    {
        // (args[1]: fix_ref.json; args[2]: the template ch125.pac) - C# fixes a copy of the same fix_in.json
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var c in doc.RootElement.EnumerateObject())
        {
            var r = c.Value;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; if (diffs.Count < 12) diffs.Add(what); } }
            string dir = r.GetProperty("dir").GetString()!;
            string cs = Path.Combine(dir, "fix_cs.json");
            File.Copy(Path.Combine(dir, "fix_in.json"), cs, true);
            var cards = r.GetProperty("cards").EnumerateArray().Select(x => x.GetString()!).ToList();
            var log = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fp = new PortIT.Engine.Port.FullPort();
            if (r.TryGetProperty("max_tris", out var mt)) fp.MaxMeshTris = mt.GetInt32();
            if (r.TryGetProperty("gear_head", out var gh)) fp.GearHead = gh.GetBoolean();
            fp.FixModel(cs, cards, args[2], log.Add);
            double secs = sw.Elapsed.TotalSeconds;
            var pyLog = r.GetProperty("log").EnumerateArray().Select(x => x.GetString()).ToList();
            Same($"log ({log.Count} vs {pyLog.Count} lines)", pyLog.SequenceEqual(log));
            for (int i = 0; i < Math.Max(pyLog.Count, log.Count); i++)
                if (i >= pyLog.Count || i >= log.Count || pyLog[i] != log[i]) { diffs.Add($"log line {i}: C# '{(i < log.Count ? log[i] : "")}' vs '{(i < pyLog.Count ? pyLog[i] : "")}'"); break; }
            var a = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, r.TryGetProperty("py", out var pyf) ? pyf.GetString()! : "fix_py.json")))!;
            var b = JsonNode.Parse(File.ReadAllText(cs))!;
            var pm = a["attires"]![0]!["meshes"]!.AsArray(); var cm = b["attires"]![0]!["meshes"]!.AsArray();
            Same($"meshes ({cm.Count} vs {pm.Count})", pm.Count == cm.Count);
            for (int i = 0; i < Math.Min(pm.Count, cm.Count); i++)
            {
                var ma = pm[i]!.AsObject(); var mb = cm[i]!.AsObject();
                string nm = ma["name"]!.GetValue<string>();
                Same($"{nm} keys", ma.Select(kv => kv.Key).Order().SequenceEqual(mb.Select(kv => kv.Key).Order()));
                foreach (var (key, va) in ma)
                {
                    var vb = mb[key];
                    int nd = 0; double worst = 0; string? first = null;
                    void Walk(JsonNode? x, JsonNode? y, string path)
                    {
                        if (x is JsonArray xa && y is JsonArray ya)
                        {
                            if (xa.Count != ya.Count) { nd++; first ??= path + " length"; return; }
                            for (int q = 0; q < xa.Count; q++) Walk(xa[q], ya[q], path + "[" + q + "]");
                        }
                        else if (x is JsonObject xo && y is JsonObject yo)
                        {
                            if (xo.ToJsonString() != yo.ToJsonString()) { nd++; first ??= path; }
                        }
                        else if (x is JsonValue xv && y is JsonValue yv)
                        {
                            if (xv.GetValueKind() is JsonValueKind.Number && yv.GetValueKind() is JsonValueKind.Number)
                            {
                                double d = Math.Abs(xv.GetValue<double>() - yv.GetValue<double>());
                                if (d > 0) { nd++; worst = Math.Max(worst, d); first ??= path; }
                            }
                            else if (xv.ToJsonString() != yv.ToJsonString()) { nd++; first ??= path; }
                        }
                        else if (x?.ToJsonString() != y?.ToJsonString()) { nd++; first ??= path; }
                    }
                    Walk(va, vb, key);
                    Same($"{nm} {key} ({nd} differ, most {worst:G3}, first {first})", nd == 0);
                }
            }
            Console.WriteLine($"{Path.GetFileName(r.GetProperty("char").GetString())}: {cm.Count} meshes, {cards.Count} cards, C# {secs:F1} s vs Python {r.GetProperty("secs").GetDouble():F0} s{(diffs.Count == 0 ? ", all same" : " DIFF " + string.Join("; ", diffs))}");
        }
        break;
    }
    case "shuffle":
    {
        // (args[1]: {n: default_rng(7).shuffle(list(range(n)))} from NumPy)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var c in doc.RootElement.EnumerateObject())
        {
            var ids = Enumerable.Range(0, int.Parse(c.Name)).ToList();
            PortIT.Engine.Util.NpRandom.Seed7().Shuffle(ids);
            bool same = ids.SequenceEqual(c.Value.EnumerateArray().Select(x => x.GetInt32()));
            if (same) ok++; else bad++;
            Console.WriteLine($"shuffle {c.Name}: {(same ? "same" : "DIFF")}");
        }
        break;
    }
    case "setup":
    {
        // (args[1]: setup_ref.json; args[2]: a work folder)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var c in doc.RootElement.EnumerateObject())
        {
            var r = c.Value;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; if (diffs.Count < 12) diffs.Add(what); } }
            string chr = r.GetProperty("char").GetString()!, att = r.GetProperty("attire").GetString()!;
            string dir = Path.Combine(args[2], "cs_" + c.Name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var log = new List<string>(); var printed = new List<string>();
            var R = new PortIT.Engine.Port.RockModel();
            var P2 = new PortIT.Engine.Port.Port2k();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            R.NormalFlipG = !PortIT.Engine.Port.Port2k.YukesPack(chr);
            Same("flip0", R.NormalFlipG == r.GetProperty("flip0").GetBoolean());
            R.TexFirst = PortIT.Engine.Port.Port2k.FacePaint(chr, PortIT.Engine.Port.Port2k.AttireDir(chr, att), dir, log.Add);
            var pyTf = r.GetProperty("tex_first").EnumerateArray().Select(x => x.GetString()!).ToList();
            Same($"tex_first ({string.Join(" | ", R.TexFirst.Select(Path.GetFileName))})", R.TexFirst.Count == pyTf.Count
                 && R.TexFirst.Zip(pyTf).All(z => Path.GetFullPath(z.First).Replace(Path.GetFullPath(dir), "W") == Path.GetFullPath(z.Second).Replace(Path.GetFullPath(r.GetProperty("dir").GetString()!), "W")));
            PortIT.Engine.Port.Port2k.CheckGreen(R, chr, att, log.Add);
            Same("flip_g", R.NormalFlipG == r.GetProperty("flip_g").GetBoolean());
            var info = P2.Setup(R, chr, att, dir, false, printed.Add);
            double secs = sw.Elapsed.TotalSeconds;
            void Lines(string what, List<string?> py, List<string> cs)
            {
                Same($"{what} ({cs.Count} vs {py.Count} lines)", py.SequenceEqual(cs));
                for (int i = 0; i < Math.Max(py.Count, cs.Count); i++)
                    if (i >= py.Count || i >= cs.Count || py[i] != cs[i]) { diffs.Add($"{what} line {i}: C# '{(i < cs.Count ? cs[i] : "")}' vs '{(i < py.Count ? py[i] : "")}'"); break; }
            }
            Lines("log", r.GetProperty("log").EnumerateArray().Select(x => x.GetString()).ToList(), log);
            Lines("printed", r.GetProperty("printed").EnumerateArray().Select(x => x.GetString()).ToList(), printed);
            foreach (var f in new[] { "head_color.png", "head_nrm.png" })
            {
                var pv = r.GetProperty("painted").GetProperty(f);
                string fp = Path.Combine(dir, "_facepaint", "Textures", f);
                Same($"painted {f}", pv.ValueKind == JsonValueKind.Null ? !File.Exists(fp) : File.Exists(fp) && H(ImageIO.Load(fp).ToRgba().Px) == pv.GetString());
            }
            string Box((int, int, int, int) b) => $"{b.Item1},{b.Item2},{b.Item3},{b.Item4}";
            var pc = r.GetProperty("cells").EnumerateObject().ToDictionary(x => x.Name, x => string.Join(",", x.Value.EnumerateArray().Select(v => v.GetInt32())));
            Same("cells", pc.Count == R.Cells.Count && pc.All(kv => R.Cells.TryGetValue(kv.Key, out var b) && Box(b) == kv.Value));
            Same("shine", r.GetProperty("shine").EnumerateObject().All(x => R.Shine.TryGetValue(x.Name, out var v) && v.Base == x.Value[0].GetDouble() && v.K == x.Value[1].GetDouble())
                 && r.GetProperty("shine").EnumerateObject().Count() == R.Shine.Count);
            Same("solid", r.GetProperty("solid").EnumerateObject().All(x => R.Solid.TryGetValue(x.Name, out var v) && v.R == x.Value[0].GetInt32() && v.G == x.Value[1].GetInt32() && v.B == x.Value[2].GetInt32())
                 && r.GetProperty("solid").EnumerateObject().Count() == R.Solid.Count);
            Same("kind", r.GetProperty("kind").EnumerateObject().All(x => R.Kind.GetValueOrDefault(x.Name) == x.Value.GetString()) && r.GetProperty("kind").EnumerateObject().Count() == R.Kind.Count);
            Same("drop", r.GetProperty("drop").EnumerateArray().Select(x => x.GetString()!).SequenceEqual(R.DropMeshes.Order(StringComparer.Ordinal)));
            Same("cutout", r.GetProperty("cutout").EnumerateArray().Select(x => x.GetString()!).SequenceEqual(R.Cutout.Order(StringComparer.Ordinal)));
            Same("not_gear", r.GetProperty("not_gear").EnumerateArray().Select(x => x.GetString()!).SequenceEqual(R.Fbx.NotGear.Order(StringComparer.Ordinal)));
            Same("solid_alpha", r.GetProperty("solid_alpha").EnumerateArray().Select(x => x.GetString()!).SequenceEqual(P2.SolidAlpha.Order(StringComparer.Ordinal)));
            Same("meshes", r.GetProperty("meshes").EnumerateArray().Select(x => (x[0].GetString()!, x[1].GetInt32())).SequenceEqual(info.Meshes));
            var cards = PortIT.Engine.Port.Port2k.CardMeshes(info);
            Same("cards", r.GetProperty("cards").EnumerateArray().Select(x => x.GetString()!).SequenceEqual(cards));
            Same("card colours", r.GetProperty("card_rgb").EnumerateObject().All(x => info.Cards.TryGetValue(x.Name, out var v) && v.R == x.Value[0].GetInt32() && v.G == x.Value[1].GetInt32() && v.B == x.Value[2].GetInt32())
                 && r.GetProperty("card_rgb").EnumerateObject().Count() == info.Cards.Count);
            foreach (var (key, sc) in new[] { ("targets", 1.0), ("targets73", 0.73) })
            {
                var t = PortIT.Engine.Port.Port2k.Targets(info, sc);
                Same(key, r.GetProperty(key).EnumerateObject().All(x => t.GetValueOrDefault(x.Name, -1) == x.Value.GetInt32()) && r.GetProperty(key).EnumerateObject().Count() == t.Count);
            }
            Same("strand pieces", r.GetProperty("pieces").EnumerateObject().All(x => PortIT.Engine.Port.Port2k.StrandPieces(info.RawMeshes.First(m => m.Name == x.Name)) == x.Value.GetInt32()));
            foreach (var x in r.GetProperty("hair_maps").EnumerateObject())
            {
                if (x.Value.ValueKind == JsonValueKind.Null) continue;
                string ad = PortIT.Engine.Port.Port2k.AttireDir(chr, att);
                var rai = R.FindTex(chr, ad, System.Text.RegularExpressions.Regex.Replace(x.Name, "(?i)_alpha$", ""), "_rai") ?? R.FindTex(chr, ad, x.Name, "_rai");
                Same($"hair map {x.Name}", rai != null && H(ImageIO.Load(Path.Combine(Path.GetDirectoryName(rai)!, x.Name + "_color.png")).ToRgba().Px) == x.Value.GetString());
            }
            foreach (var x in r.GetProperty("thin").EnumerateObject())
            {
                var src = info.RawMeshes.First(m => m.Name == x.Name);
                var mm = new PortIT.Engine.Port.RockFbx.Mesh { Name = src.Name, Verts = src.Verts, Normals = src.Normals, Uv = src.Uv, Tris = src.Tris, Weights = src.Weights.ToList() };
                var (nc, kc) = PortIT.Engine.Port.Port2k.ThinCards(mm, 900);
                var v = x.Value;
                Same($"thin {x.Name} ({nc}/{kc} vs {v[0].GetInt32()}/{v[1].GetInt32()})", nc == v[0].GetInt32() && kc == v[1].GetInt32() && HF(mm.Verts) == v[2].GetString()
                     && HL(mm.Tris.Select(q => (long)q)) == v[3].GetString() && HF(mm.Uv) == v[4].GetString()
                     && v[5].EnumerateArray().Select((e, q) => e[0].GetInt32() == mm.Weights[q].Bone && e[1].GetString() == HF(mm.Weights[q].W)).All(b => b));
            }
            var flat = new PortIT.Engine.Port.FullPort().FlatCards(info.RawMeshes, cards.ToHashSet(), _ => { });
            Same("flat cards", r.GetProperty("flat").EnumerateArray().Select(x => x.GetString()!).SequenceEqual(flat.Order(StringComparer.Ordinal)));
            Console.WriteLine($"{Path.GetFileName(chr)} {att}: {cards.Count} cards, painted {(r.GetProperty("painted").GetProperty("head_color.png").ValueKind != JsonValueKind.Null ? "yes" : "no")}, C# {secs:F1} s{(diffs.Count == 0 ? ", all same" : " DIFF " + string.Join("; ", diffs))}");
        }
        break;
    }
    case "fullside":
    {
        // (args[1]: full_ref.json; args[2]: the game folder; args[3]: a work folder)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        var root = doc.RootElement;
        var diffs = new List<string>();
        void Same(string what, bool eq) { if (eq) ok++; else { bad++; diffs.Add(what); } }
        string template = Path.Combine(args[2], "pac", "ch", "ch125.pac");
        foreach (var c in root.GetProperty("choice").EnumerateObject())
        {
            var nk = c.Name.Split('_').Select(long.Parse).ToArray();
            Same($"choice {c.Name}", PortIT.Engine.Util.NpRandom.Seed1().ChooseIndices(nk[0], nk[1]).SequenceEqual(c.Value.EnumerateArray().Select(x => x.GetInt64())));
        }
        var fp = new PortIT.Engine.Port.FullPort();
        foreach (var c in root.GetProperty("limits").EnumerateObject())
        {
            var v = c.Value;
            Same($"limits {c.Name}", fp.PacLimits(c.Name).SequenceEqual(v.GetProperty("limits").EnumerateArray().Select(x => x.GetString())));
            var (heavy, most) = fp.HeavyMeshes(c.Name);
            Same($"heavy {c.Name}", most == v.GetProperty("most").GetInt32() && heavy.Select(h => $"{h.V},{h.I}").SequenceEqual(v.GetProperty("heavy").EnumerateArray().Select(x => $"{x[0].GetInt64()},{x[1].GetInt64()}")));
            var low = new PortIT.Engine.Port.FullPort { PartPages = 20, SafeIndices = 1000 };
            var ll = low.PacLimits(c.Name, 50); var pl = v.GetProperty("limits_low").EnumerateArray().Select(x => x.GetString()).ToList();
            Same($"limits low {c.Name} ({ll.Count} vs {pl.Count}){(ll.SequenceEqual(pl) ? "" : ": C# '" + ll.FirstOrDefault() + "' vs '" + pl.FirstOrDefault() + "'")}", ll.SequenceEqual(pl));
            Same($"heavy low {c.Name}", low.HeavyMeshes(c.Name).Heavy.Select(h => $"{h.V},{h.I}").SequenceEqual(v.GetProperty("heavy_low").EnumerateArray().Select(x => $"{x[0].GetInt64()},{x[1].GetInt64()}")));
        }
        foreach (var c in root.GetProperty("checks").EnumerateObject())
        {
            var (fo, go, notes) = fp.Checks(c.Name, template);
            var v = c.Value;
            var pn = v[2].EnumerateArray().Select(x => x.GetString()).ToList();
            Same($"checks {Path.GetFileName(Path.GetDirectoryName(c.Name))}: face {fo} vs {v[0].GetInt32()}, gear {go} vs {v[1].GetInt32()}, notes {notes.Count} vs {pn.Count}{(notes.SequenceEqual(pn) ? "" : ": C# '" + string.Join(" | ", notes) + "' vs '" + string.Join(" | ", pn) + "'")}",
                 fo == v[0].GetInt32() && go == v[1].GetInt32() && notes.SequenceEqual(pn));
        }
        int k0 = 0;
        foreach (var c in root.GetProperty("layout").EnumerateObject())
        {
            var parts = c.Name.Split('|');
            string dir = Path.Combine(args[3], "lay_" + k0++);
            Directory.CreateDirectory(dir);
            var R = new PortIT.Engine.Port.RockModel();
            var P2 = new PortIT.Engine.Port.Port2k();
            P2.Setup(R, parts[0], parts[1], dir, true, _ => { });
            var log = new List<string>();
            fp.Layout(R, parts[0], PortIT.Engine.Port.Port2k.AttireDir(parts[0], parts[1]), new HashSet<string>(), log.Add);
            var v = c.Value;
            var pc = v.GetProperty("cells").EnumerateObject().Select(x => x.Name + "=" + string.Join(",", x.Value.EnumerateArray().Select(q => q.GetInt32()))).ToList();
            var cc = R.Cells.Select(kv => $"{kv.Key}={kv.Value.X},{kv.Value.Y},{kv.Value.W},{kv.Value.H}").ToList();
            Same($"layout {Path.GetFileName(parts[0])}{(pc.Order().SequenceEqual(cc.Order()) ? "" : ": C# " + string.Join(" ", cc) + " vs " + string.Join(" ", pc))}", pc.Order().SequenceEqual(cc.Order())
                 && R.TmplMouthCell.X == v.GetProperty("mouth")[0].GetInt32() && R.TmplMouthCell.Y == v.GetProperty("mouth")[1].GetInt32()
                 && R.Sheet.W == v.GetProperty("sheet")[0].GetInt32() && log.SequenceEqual(v.GetProperty("log").EnumerateArray().Select(x => x.GetString())));
        }
        foreach (var c in root.GetProperty("pack").EnumerateObject())
        {
            var v = c.Value;
            int size = int.Parse(c.Name.Split('_')[1]);
            string od = Path.Combine(args[3], "cspack_" + c.Name);
            if (Directory.Exists(od)) Directory.Delete(od, true);
            var seen = new List<string>();
            PortIT.Engine.Imaging.DdsWriter.OnLevels = (im, cells) => seen.Add($"{H(im.ToRgba().Px)} {im.W} {im.H} {string.Join(";", (cells ?? []).Select(q => $"{q.X},{q.Y},{q.W},{q.H}"))}");
            var log = new List<string>();
            var pk = new PortIT.Engine.Port.FullPort { PackSize = size };
            var pacs = root.GetProperty("limits").EnumerateObject().Select(x => x.Name).ToList();
            var files = pk.MakePack(v.GetProperty("work").GetString()!, pacs[0], od, "portit_hd_t", [(1536, 768, 64, 64), (1024, 512, 333, 257)], log.Add);
            PortIT.Engine.Imaging.DdsWriter.OnLevels = null;
            var ps = v.GetProperty("seen").EnumerateArray().Select(x => $"{x[0].GetString()} {x[1].GetInt32()} {x[2].GetInt32()} {string.Join(";", x[3].EnumerateArray().Select(q => string.Join(",", q.EnumerateArray().Select(z => z.GetInt32()))))}").ToList();
            Same($"pack {c.Name} pictures / cells", seen.SequenceEqual(ps));
            Same($"pack {c.Name} files", files.Select(Path.GetFileName).SequenceEqual(v.GetProperty("files").EnumerateArray().Select(x => x.GetString())));
            // (the log line carries the file's MB: our DXT5 files are the same size - same header, same block count)
            Same($"pack {c.Name} log", log.SequenceEqual(v.GetProperty("log").EnumerateArray().Select(x => x.GetString())));
        }
        foreach (var c in root.GetProperty("window").EnumerateObject())
        {
            var log = new List<string>();
            int pages = PortIT.Engine.Port.FullPort.WindowPages(c.Value.GetProperty("game").GetString()!, log.Add);
            Same($"window {c.Name}", pages == c.Value.GetProperty("pages").GetInt32() && log.SequenceEqual(c.Value.GetProperty("log").EnumerateArray().Select(x => x.GetString())));
        }
        foreach (var c in root.GetProperty("toml").EnumerateObject())
        {
            var v = c.Value;
            string g = Path.Combine(args[3], "cstoml_" + c.Name);
            if (Directory.Exists(g)) Directory.Delete(g, true);
            Directory.CreateDirectory(g);
            if (v.GetProperty("text").ValueKind != JsonValueKind.Null) File.WriteAllText(Path.Combine(g, "svr2011.toml"), v.GetProperty("text").GetString(), new System.Text.UTF8Encoding(false));
            bool on = PortIT.Engine.Port.FullPort.EnablePack(g, "PortIT HD");
            string res = File.ReadAllText(Path.Combine(g, "svr2011.toml"));
            string keep = Path.Combine(g, "svr2011.toml.before_portit");
            string? kept = File.Exists(keep) ? File.ReadAllText(keep) : null;
            Same($"toml {c.Name}{(res == v.GetProperty("result").GetString() ? "" : ": C# " + JsonSerializer.Serialize(res) + " vs " + JsonSerializer.Serialize(v.GetProperty("result").GetString()))}",
                 on == v.GetProperty("on").GetBoolean() && res == v.GetProperty("result").GetString()
                 && kept == (v.GetProperty("kept").ValueKind == JsonValueKind.Null ? null : v.GetProperty("kept").GetString())
                 && PortIT.Engine.Port.FullPort.EnabledPack(g) == (v.GetProperty("enabled").ValueKind == JsonValueKind.Null ? null : v.GetProperty("enabled").GetString()));
        }
        Console.WriteLine(diffs.Count == 0 ? "all same" : "DIFF " + string.Join("\n  ", diffs));
        break;
    }
    case "port":
    {
        // (args[1]: port_ref.json; args[2]: the game folder; args[3]: a work folder; args[4]: optional case numbers "0,2")
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        var only = args.Length > 4 ? args[4].Split(',').ToHashSet() : null;
        static bool Timing(string? l) => l != null && (l.StartsWith("read the 2K model: ") || l.StartsWith("model built: ") || l.StartsWith("weights fixed: ") || l.StartsWith("ch.pac built: "));
        static string? Canon(string? l) => l != null && l.StartsWith("face fit: bones placed by the stretch")
            ? l[..(l.IndexOf(": ", 10) + 2)] + string.Join(", ", l[(l.IndexOf(": ", 10) + 2)..].Split(", ").Order(StringComparer.Ordinal)) : l;
        foreach (var c in doc.RootElement.EnumerateObject())
        {
            if (only != null && !only.Contains(c.Name)) continue;
            var r = c.Value;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; if (diffs.Count < 14) diffs.Add(what); } }
            void Lines(string what, List<string?> py, List<string?> cs)
            {
                Same($"{what} ({cs.Count} vs {py.Count} lines)", py.SequenceEqual(cs));
                for (int i = 0; i < Math.Max(py.Count, cs.Count); i++)
                    if (i >= py.Count || i >= cs.Count || py[i] != cs[i]) { diffs.Add($"{what} line {i}: C# '{(i < cs.Count ? cs[i] : "")}' vs '{(i < py.Count ? py[i] : "")}'"); break; }
            }
            string dir = Path.Combine(args[3], "cs_" + c.Name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var log = new List<string>(); var printed = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PortIT.Engine.Port.AewBridge? bridge = null;
            if (r.TryGetProperty("aew", out var aew))
                bridge = new PortIT.Engine.Port.AewBridge(aew[0].GetString()!, aew[1].GetString()!, dir, log.Add);
            var res = new PortIT.Engine.Port.Port2k().Build(r.GetProperty("char").GetString()!, r.GetProperty("attire").GetString()!, dir, args[2],
                                                            r.GetProperty("budget").GetInt32(), log: log.Add, print: printed.Add, bridge: bridge);
            double secs = sw.Elapsed.TotalSeconds;
            // (texture pack names carry the hash of the pac's own DXT data - our encoder's blocks differ from Pillow's
            //  (piece 7): the hash masked; its match with our own pac is checked below)
            static string? Mask(string? l) => l == null ? null : System.Text.RegularExpressions.Regex.Replace(l, "_(color|normal)_[0-9a-f]{16}[.]dds", "_$1_HASH.dds");
            Lines("log", r.GetProperty("log").EnumerateArray().Select(x => Mask(x.GetString())).Where(l => !Timing(l)).ToList(), log.Where(l => !Timing(l)).Select(Mask).ToList());
            if (res.Pack.Count > 0)
            {
                var hs = PortIT.Engine.Port.TexturePackHash.PacTextureHashes(File.ReadAllBytes(res.Pac));
                Same("pack names = our pac's hashes", res.Pack.Select(Path.GetFileName).SequenceEqual(new[] { $"portit_hd_color_{hs["color"]}.dds", $"portit_hd_normal_{hs["normal"]}.dds" }));
            }
            // (the AEW bridge's texture paths name each run's own work folder)
            string pyDir = r.GetProperty("dir").GetString()!;
            string? Here(string? l, string d) => l?.Replace(d, "WORK");
            Lines("printed", r.GetProperty("printed").EnumerateArray().Select(x => Canon(Here(x.GetString(), pyDir))).ToList(), printed.Select(l => Canon(Here(l, dir))).ToList());
            Lines("report", r.GetProperty("report").EnumerateArray().Select(x => Canon(x.GetString())).ToList(), res.Report.Select(Canon).ToList());
            int pages = res.Rep["attires"]![0]!["pages"]!.GetValue<int>();
            Same($"pages {pages} vs {r.GetProperty("pages").GetInt32()}", pages == r.GetProperty("pages").GetInt32());
            if (r.GetProperty("checks").ValueKind != JsonValueKind.Null)
            {
                var pc = r.GetProperty("checks");
                Same($"checks {res.Checks.FaceOver}/{res.Checks.GearOver} vs {pc.GetProperty("face_over").GetInt32()}/{pc.GetProperty("gear_over").GetInt32()}",
                     res.Checks.FaceOver == pc.GetProperty("face_over").GetInt32() && res.Checks.GearOver == pc.GetProperty("gear_over").GetInt32()
                     && res.Checks.Notes.SequenceEqual(pc.GetProperty("notes").EnumerateArray().Select(x => x.GetString())));
            }
            Same($"pack files ({res.Pack.Count})", res.Pack.Count == r.GetProperty("pack").GetArrayLength());
            var a = JsonNode.Parse(File.ReadAllText(Path.Combine(r.GetProperty("dir").GetString()!, "model.json")))!;
            var b = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "model.json")))!;
            var pm = a["attires"]![0]!["meshes"]!.AsArray(); var cm = b["attires"]![0]!["meshes"]!.AsArray();
            Same($"meshes ({cm.Count} vs {pm.Count})", pm.Count == cm.Count);
            int wdiff = 0, wtot = 0;
            var wnote = new List<string>();
            for (int i = 0; i < Math.Min(pm.Count, cm.Count); i++)
            {
                var ma = pm[i]!.AsObject(); var mb = cm[i]!.AsObject();
                string nm = ma["name"]!.GetValue<string>();
                Same($"{nm} name / kind / material", ma["name"]!.ToJsonString() == mb["name"]!.ToJsonString() && ma["kind"]?.ToJsonString() == mb["kind"]?.ToJsonString()
                     && (ma["material"]?.ToJsonString() ?? "") == (mb["material"]?.ToJsonString() ?? ""));
                foreach (var key in new[] { "positions", "normals", "uvs", "triangles" })
                {
                    var xa = ma[key]!.AsArray(); var xb = mb[key]!.AsArray();
                    int nd = 0;
                    if (xa.Count == xb.Count)
                        for (int v = 0; v < xa.Count; v++)
                            for (int q = 0; q < xa[v]!.AsArray().Count; q++)
                                if (xa[v]![q]!.GetValue<double>() != xb[v]![q]!.GetValue<double>()) nd++;
                    Same($"{nm} {key} ({xb.Count} vs {xa.Count}, {nd} values differ)", xa.Count == xb.Count && nd == 0);
                }
                var ba = ma["bones"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(); var bb = mb["bones"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                var wa = ma["weights"]!.AsArray(); var wb = mb["weights"]!.AsArray();
                int md = 0;
                for (int v = 0; v < Math.Min(wa.Count, wb.Count); v++)
                {
                    var la = wa[v]!.AsArray().Select(x => (ba[x![0]!.GetValue<int>()], x[1]!.GetValue<double>())).Order().ToList();
                    var lb = wb[v]!.AsArray().Select(x => (bb[x![0]!.GetValue<int>()], x[1]!.GetValue<double>())).Order().ToList();
                    if (!la.SequenceEqual(lb)) { md++; if (wnote.Count < 3) wnote.Add($"{nm}[{v}] C# {string.Join(" ", lb)} vs {string.Join(" ", la)}"); }
                }
                wdiff += md; wtot += wa.Count;
            }
            Console.WriteLine($"{Path.GetFileName(r.GetProperty("char").GetString())} {r.GetProperty("attire").GetString()} {r.GetProperty("budget").GetInt32()}: {pages} pages, C# {secs:F0} s vs Python {r.GetProperty("secs").GetDouble():F0} s;"
                + $" weights differ on {wdiff} of {wtot} vertices{(wnote.Count > 0 ? " (" + string.Join("; ", wnote) + ")" : "")}{(diffs.Count == 0 ? ", all else same" : " DIFF " + string.Join("; ", diffs))}");
        }
        break;
    }
    case "aewsrc":
    {
        // (args[1]: aewsrc_ref.json; args[2]: the AEW paks folder)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        foreach (var c in doc.RootElement.EnumerateObject())
        {
            var r = c.Value;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; if (diffs.Count < 12) diffs.Add(what); } }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var m = PortIT.Engine.Readers.SourceAew.Load(args[2], c.Name);
            double secs = sw.Elapsed.TotalSeconds;
            Same("female", m.Female == r.GetProperty("female").GetBoolean());
            var pb = r.GetProperty("bones").EnumerateArray().ToList();
            Same($"bones ({m.Bones.Count} vs {pb.Count})", pb.Count == m.Bones.Count);
            double worst = 0; int bd = 0;
            for (int i = 0; i < Math.Min(pb.Count, m.Bones.Count); i++)
            {
                var w = pb[i][2].EnumerateArray().Select(x => x.GetDouble()).ToArray();
                for (int q = 0; q < 16; q++) worst = Math.Max(worst, Math.Abs(w[q] - m.Bones[i].World[q]));
                if (pb[i][0].GetString() != m.Bones[i].Name || pb[i][1].GetInt32() != m.Bones[i].Parent || !w.SequenceEqual(m.Bones[i].World)) bd++;
            }
            Same($"bone names / parents / world ({bd} differ, most {worst:G3})", bd == 0);
            var pm = r.GetProperty("meshes").EnumerateArray().ToList();
            Same($"meshes ({m.Meshes.Count} vs {pm.Count})", pm.Count == m.Meshes.Count);
            for (int i = 0; i < Math.Min(pm.Count, m.Meshes.Count); i++)
            {
                var j = pm[i]; var s = m.Meshes[i];
                string nm = j.GetProperty("material").GetString()!;
                Same($"{nm} name '{s.Name}' vs '{j.GetProperty("name").GetString()}' / kind / cutout", j.GetProperty("name").GetString() == s.Name && j.GetProperty("material").GetString() == s.Material
                     && j.GetProperty("kind").GetString() == s.Kind && j.GetProperty("cutout").GetBoolean() == s.Cutout);
                Same($"{nm} positions", j.GetProperty("positions").GetString() == HF(s.Positions));
                Same($"{nm} normals", j.GetProperty("normals").GetString() == HF(s.Normals));
                Same($"{nm} uvs", j.GetProperty("uvs").GetString() == HF(s.Uvs));
                Same($"{nm} tris", j.GetProperty("tris").GetString() == HL(s.Tris.Select(x => (long)x)));
                Same($"{nm} bone_idx", j.GetProperty("bone_idx").GetString() == HL(s.BoneIdx));
                Same($"{nm} bone_w", j.GetProperty("bone_w").GetString() == HF(s.BoneW));
                var pi = j.GetProperty("images").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString());
                Same($"{nm} image roles ({string.Join(",", s.Images.Keys)} vs {string.Join(",", pi.Keys)})", pi.Keys.Order().SequenceEqual(s.Images.Keys.Order()));
                foreach (var (role, h) in pi)
                    if (s.Images.TryGetValue(role, out var im)) Same($"{nm} image {role}", H(im.ToRgba().Px) == h);
                var ps = j.GetProperty("scalars").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble());
                Same($"{nm} scalars", ps.Count == s.Scalars.Count && ps.All(kv => s.Scalars.TryGetValue(kv.Key, out var v) && v == kv.Value));
            }
            Console.WriteLine($"{c.Name}: {m.Meshes.Count} meshes, {m.Bones.Count} bones, C# {secs:F1} s{(diffs.Count == 0 ? ", all same" : " DIFF " + string.Join("; ", diffs))}");
        }
        break;
    }
    case "polygon":
    {
        // (args[1]: Pillow ImageDraw.polygon fills of random triangles)
        using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        int shown = 0;
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            int w = c.GetProperty("w").GetInt32(), h = c.GetProperty("h").GetInt32();
            var pts = c.GetProperty("pts").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList();
            var px = new byte[w * h]; Array.Fill(px, (byte)255);
            PortIT.Engine.Imaging.Draw.Polygon(px, w, h, pts, 7);
            var want = Convert.FromHexString(c.GetProperty("px").GetString()!);
            if (px.Zip(want).All(z => (z.First != 255) == (z.Second != 255))) ok++;
            else
            {
                bad++;
                if (shown++ < 4)
                {
                    int nd = px.Zip(want).Count(z => z.First != z.Second);
                    Console.WriteLine($"DIFF {string.Join(" ", pts.Select(p => $"({p.Item1:R},{p.Item2:R})"))}: {nd} px");
                }
            }
        }
        break;
    }
    case "worker":
    {
        // (args[1]: worker_ref.json; args[2]: requests.json; args[3]: the Python root; args[4]: a C# root; args[5]: optional names "a,b")
        var refDoc = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject();
        var reqs = JsonNode.Parse(File.ReadAllText(args[2]))!.AsObject();
        string pyRoot = args[3], csRoot = args[4];
        var only = args.Length > 5 ? args[5].Split(',').ToHashSet() : null;
        Directory.CreateDirectory(csRoot);
        string Esc(string x) => JsonSerializer.Serialize(x)[1..^1];
        // (paths made neutral; the run's clock: timestamps, 'took N s'; the pack names' DXT hash - piece 9)
        string Neutral(string x)
        {
            x = x.Replace(pyRoot, "ROOT").Replace(csRoot, "ROOT").Replace(Esc(pyRoot), "ROOT").Replace(Esc(csRoot), "ROOT");
            x = System.Text.RegularExpressions.Regex.Replace(x, @"_\d{8}_\d{6}", "_TIME");
            x = System.Text.RegularExpressions.Regex.Replace(x, @"_\d{6}\.png", "_HHMMSS.png");
            x = System.Text.RegularExpressions.Regex.Replace(x, @"took \d+ s", "took N s");
            x = System.Text.RegularExpressions.Regex.Replace(x, @"_(color|normal)_[0-9a-f]{16}\.dds", "_$1_HASH.dds");
            return x;
        }
        int Rgb(string f, out byte[] px) { var im = ImageIO.Load(f).ToRgb(); px = im.Px; return im.W; }
        foreach (var (name, reqNode) in reqs)
        {
            if (only != null && !only.Contains(name)) continue;
            var diffs = new List<string>();
            void Same(string what, bool eq) { if (eq) ok++; else { bad++; if (diffs.Count < 12) diffs.Add(what); } }
            var req = JsonNode.Parse(reqNode!.ToJsonString().Replace("{ROOT}", Esc(csRoot)))!.AsObject();
            if (req["_setup"] is JsonArray setup)
            {
                foreach (var pr in setup)
                {
                    string src = pr![0]!.GetValue<string>(), dst = pr[1]!.GetValue<string>();
                    if (Directory.Exists(dst)) Directory.Delete(dst, true);
                    void Copy(string a, string b) { Directory.CreateDirectory(b); foreach (var f in Directory.GetFiles(a)) File.Copy(f, Path.Combine(b, Path.GetFileName(f))); foreach (var d in Directory.GetDirectories(a)) Copy(d, Path.Combine(b, Path.GetFileName(d))); }
                    Copy(src, dst);
                }
                req.Remove("_setup");
            }
            var log = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new PortIT.Engine.Worker(log.Add).Run(req);
            double secs = sw.Elapsed.TotalSeconds;
            var py = refDoc[name]!["response"]!.AsObject();
            void Cmp(JsonNode? a, JsonNode? b, string path)
            {
                if (a is JsonObject ao && b is JsonObject bo)
                {
                    var keys = ao.Select(kv => kv.Key).Union(bo.Select(kv => kv.Key)).ToList();
                    foreach (var k in keys)
                    {
                        if (!ao.ContainsKey(k) || !bo.ContainsKey(k)) { Same($"{path}.{k} only in {(ao.ContainsKey(k) ? "Python" : "C#")}", false); continue; }
                        Cmp(ao[k], bo[k], path + "." + k);
                    }
                }
                else if (a is JsonArray aa && b is JsonArray ba)
                {
                    Same($"{path} length {ba.Count} vs {aa.Count}", aa.Count == ba.Count);
                    for (int i = 0; i < Math.Min(aa.Count, ba.Count); i++) Cmp(aa[i], ba[i], $"{path}[{i}]");
                }
                else if (a is JsonValue av && b is JsonValue bv && av.GetValueKind() == JsonValueKind.String && bv.GetValueKind() == JsonValueKind.String)
                {
                    string sa = av.GetValue<string>(), sb = bv.GetValue<string>();
                    if (sa.EndsWith(".png") && File.Exists(sa) && File.Exists(sb))
                    {
                        int wa = Rgb(sa, out var pa), wb = Rgb(sb, out var pb);
                        // (the pac preview's caption: Pillow's built-in font is not copied - its strip left out)
                        int skip = path.EndsWith("preview") && !name.StartsWith("inspect") && !name.StartsWith("srcprev") ? 22 * wa * 3 : 0;
                        int nd = 0;
                        if (pa.Length == pb.Length) for (int q = skip; q < pa.Length; q++) if (pa[q] != pb[q]) nd++;
                        Same($"{path} picture ({nd} bytes differ{(skip > 0 ? ", caption left out" : "")})", wa == wb && pa.Length == pb.Length && nd == 0);
                    }
                    else Same($"{path}: C# '{Neutral(sb)}' vs '{Neutral(sa)}'", Neutral(sa) == Neutral(sb));
                }
                else Same($"{path}: C# {b?.ToJsonString()} vs {a?.ToJsonString()}", Neutral(a?.ToJsonString() ?? "null") == Neutral(b?.ToJsonString() ?? "null"));
            }
            Cmp(py, res, "");
            // the files each wrote: .svrmod entries and manifest, installed folders' text files and file names
            foreach (var key in new[] { "svrmod", "texture_zip" })
                if (py[key] is JsonValue pz && res[key] is JsonValue cz && File.Exists(pz.GetValue<string>()) && File.Exists(cz.GetValue<string>()))
                {
                    using var za = System.IO.Compression.ZipFile.OpenRead(pz.GetValue<string>());
                    using var zb = System.IO.Compression.ZipFile.OpenRead(cz.GetValue<string>());
                    Same($"{key} entries ({string.Join(" ", zb.Entries.Select(e => Neutral(e.FullName)))} vs {string.Join(" ", za.Entries.Select(e => Neutral(e.FullName)))})",
                         za.Entries.Select(e => Neutral(e.FullName)).SequenceEqual(zb.Entries.Select(e => Neutral(e.FullName))));
                    Same($"{key} stored entries", zb.Entries.All(e => e.CompressedLength == e.Length));
                    var ma = za.GetEntry("manifest.txt"); var mb = zb.GetEntry("manifest.txt");
                    if (ma != null && mb != null)
                    {
                        string ta = new StreamReader(ma.Open()).ReadToEnd(), tb = new StreamReader(mb.Open()).ReadToEnd();
                        Same($"{key} manifest ({tb.Replace("\n", "|")} vs {ta.Replace("\n", "|")})", ta == tb);
                    }
                }
            foreach (var key in new[] { "installed", "out" })
                if (py[key] is JsonValue pi && res[key] is JsonValue ci && Directory.Exists(pi.GetValue<string>()) && Directory.Exists(ci.GetValue<string>()))
                {
                    string A = pi.GetValue<string>(), B = ci.GetValue<string>();
                    var fa = Directory.GetFiles(A, "*", SearchOption.AllDirectories).Select(f => Neutral(Path.GetRelativePath(A, f))).Order().ToList();
                    var fb = Directory.GetFiles(B, "*", SearchOption.AllDirectories).Select(f => Neutral(Path.GetRelativePath(B, f))).Order().ToList();
                    Same($"{key} files ({string.Join(" ", fb)} vs {string.Join(" ", fa)})", fa.SequenceEqual(fb));
                    foreach (var f in Directory.GetFiles(A, "*.txt", SearchOption.AllDirectories))
                    {
                        string g = Path.Combine(B, Path.GetRelativePath(A, f));
                        if (File.Exists(g) && !f.EndsWith("portit_report.txt")) Same($"{key} {Path.GetFileName(f)} text", File.ReadAllText(f) == File.ReadAllText(g));
                        if (File.Exists(g) && f.EndsWith("portit_report.txt")) Same($"{key} report text", Neutral(File.ReadAllText(f)) == Neutral(File.ReadAllText(g)));
                    }
                }
            if (name.StartsWith("install") || name.StartsWith("attire"))
            {
                string ga = Path.Combine(pyRoot, "fakegame"), gb = Path.Combine(csRoot, "fakegame");
                var la = Directory.GetFileSystemEntries(ga, "*", SearchOption.AllDirectories).Select(f => Neutral(Path.GetRelativePath(ga, f))).Order().ToList();
                var lb = Directory.GetFileSystemEntries(gb, "*", SearchOption.AllDirectories).Select(f => Neutral(Path.GetRelativePath(gb, f))).Order().ToList();
                Same($"fake game tree (C# only: {string.Join(" ", lb.Except(la))}; Python only: {string.Join(" ", la.Except(lb))})", la.SequenceEqual(lb));
                foreach (var f in new[] { "svr2011.toml", "svr2011.toml.before_portit" })
                    if (File.Exists(Path.Combine(ga, f)) || File.Exists(Path.Combine(gb, f)))
                        Same($"fake game {f}", File.Exists(Path.Combine(ga, f)) && File.Exists(Path.Combine(gb, f)) && File.ReadAllText(Path.Combine(ga, f)) == File.ReadAllText(Path.Combine(gb, f)));
            }
            Console.WriteLine($"{name}: C# {secs:F1} s vs Python {refDoc[name]!["secs"]!.GetValue<double>():F0} s{(diffs.Count == 0 ? ", all same" : " DIFF " + string.Join("; ", diffs))}");
        }
        break;
    }
    case "render":
    {
        // (args[1..]: pairs "<pac>|<Python's preview png>": our render of the same pac, the caption strip left out)
        foreach (var pair in args.Skip(1))
        {
            var pp = pair.Split('|');
            string mine = Path.Combine(Path.GetTempPath(), "portit_render_check.png");
            PortIT.Engine.Port.Preview.Render(pp[0], mine);
            var a = ImageIO.Load(pp[1]).ToRgb(); var b = ImageIO.Load(mine).ToRgb();
            int skip = 22 * a.W * 3, nd = 0;
            for (int q = skip; q < Math.Min(a.Px.Length, b.Px.Length); q++) if (a.Px[q] != b.Px[q]) nd++;
            bool same = a.W == b.W && a.H == b.H && nd == 0;
            if (same) ok++; else bad++;
            Console.WriteLine($"{pp[0]}: {(same ? "same picture (caption strip left out)" : $"{nd} bytes differ")}");
        }
        break;
    }
    case "hairrig":
    {
        // (args: a folder of stock chNNN.pac) - per pac: the parts, the hair model's (0x2710) bones, and the text parts
        foreach (var pac in Directory.EnumerateFiles(args[1], "ch*.pac").Order().Take(args.Length > 2 ? int.Parse(args[2]) : 999))
        {
            try
            {
                var arc = Epk8.Read(File.ReadAllBytes(pac));
                var blob = arc.Groups[0].Entries[0].Data;
                var ids = new List<string>();
                foreach (var e in Pach.Read(blob))
                {
                    ids.Add(e.Id.ToString("x4"));
                    var raw = Bpe.Unpack(e.Data);
                    if (e.Id == 0x2710 && Jboy.IsJboy(raw))
                    {
                        var j = Jboy.Read(raw);
                        Console.WriteLine($"{Path.GetFileName(pac)} 2710: {j.Nodes.Count} bones: " + string.Join(" ", j.Nodes.Select(n => n.Name)));
                        Console.WriteLine($"   meshes {j.Meshes.Count}, verts {j.Meshes.Sum(m => m.Verts.Count)}, shaders " + string.Join(",", j.Meshes.Select(m => m.Shader).Distinct()));
                    }
                    if (e.Id == 0x2710 && args.Length > 4 && Jboy.IsJboy(raw))
                    {
                        var j = Jboy.Read(raw);
                        foreach (var n in j.Nodes.Where(n => n.Name.StartsWith("kami_b0") || n.Name is "atama" or "kubi"))
                            Console.WriteLine($"   node {n.Name,-14} parent {(n.Parent < j.Nodes.Count ? j.Nodes[n.Parent].Name : n.Parent.ToString())} T [{string.Join(", ", n.T.Select(x => x.ToString("F4")))}] R [{string.Join(", ", n.R.Select(x => x.ToString("F4")))}] U40 {n.U40:x} U44 {n.U44} U48 [{string.Join(",", n.U48.Select(x => x.ToString("x")))}] S [{string.Join(", ", n.Sphere.Select(x => x.ToString("F3")))}]");
                        foreach (var m in j.Meshes) Console.WriteLine($"   mesh {m.Shader} verts {m.Verts.Count} palette [{string.Join(",", m.Palette.Select(p => p < j.Nodes.Count + 1 && p > 0 ? j.Nodes[p - 1].Name : p.ToString()))}] blocks {m.Weights.Count}");
                    }
                    if (e.Id is 0x3c or 0x3d && args.Length > 3)
                        Console.WriteLine($"   {e.Id:x4}: " + System.Text.Encoding.Latin1.GetString(raw).Replace("\r", "").Replace("\n", " | "));
                    if (e.Id is 0x28 or 0x32 && args.Length > 3)
                        Console.WriteLine($"   {e.Id:x4}: " + System.Text.Encoding.Latin1.GetString(raw).Replace("\r", "").Replace("\n", " | ")[..Math.Min(400, raw.Length)]);
                }
                Console.WriteLine($"{Path.GetFileName(pac)} parts: " + string.Join(",", ids));
            }
            catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(pac)}: {ex.Message}"); }
        }
        ok++;
        break;
    }
    case "meshsurvey":
    {
        // (args: folder of stock ch*.pac) - per (child, shader, U88, word +0x60, word +0x64, vfmt): how many meshes
        var count = new Dictionary<string, int>();
        foreach (var pac in Directory.EnumerateFiles(args[1], "ch*.pac"))
            try
            {
                foreach (var e in Pach.Read(Epk8.Read(File.ReadAllBytes(pac)).Groups[0].Entries[0].Data))
                {
                    var raw = Bpe.Unpack(e.Data);
                    if (!Jboy.IsJboy(raw)) continue;
                    foreach (var m in Jboy.Read(raw).Meshes)
                    {
                        string k = $"{e.Id:x4} {m.Shader,-12} U88 {m.U88:x3} w60 {m.U5c[1]} w64 {m.U5c[2]} vfmt {m.Vfmt}";
                        count[k] = count.GetValueOrDefault(k) + 1;
                    }
                }
            }
            catch (Exception) { }
        foreach (var kv in count.OrderBy(kv => kv.Key)) Console.WriteLine($"{kv.Value,6}  {kv.Key}");
        ok++;
        break;
    }
    case "ddsinfo":
    {
        // (args: pac or dds...) - each colour dds: size, mip count, format
        foreach (var f in args.Skip(1))
        {
            var list = new List<(string, byte[])>();
            if (f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) list.Add((Path.GetFileName(f), File.ReadAllBytes(f)));
            else
                foreach (var e in Pach.Read(Epk8.Read(File.ReadAllBytes(f)).Groups[0].Entries[0].Data))
                {
                    var raw = Bpe.Unpack(e.Data);
                    if (Bundle.IsBundle(raw)) foreach (var (n, d) in PortIT.Engine.Build.SvrBuilder.TexBundleRead(raw)) list.Add(($"{Path.GetFileName(f)}:{e.Id:x}:{n}", d));
                }
            foreach (var (n, d) in list)
                if (d.Length > 128 && d.AsSpan(0, 4).SequenceEqual("DDS "u8))
                    Console.WriteLine($"{n}: {BitConverter.ToInt32(d, 16)}x{BitConverter.ToInt32(d, 12)} mips {BitConverter.ToInt32(d, 28)} fourcc {System.Text.Encoding.ASCII.GetString(d, 84, 4)} flags {BitConverter.ToInt32(d, 8):x}");
        }
        ok++;
        break;
    }
    case "mipcheck":
    {
        // (args: pac out_prefix color.dds) - the face drawn with the HD colour at full size and box-shrunk 4x (a mip the
        // game may use), cut at 90 and at 8
        var sc = PortIT.Engine.Port.Viewer3D.Load(args[1], 0, args[3]);
        var full = sc.Tex!; int tw = sc.TW, th = sc.TH;
        int k = 4, w2 = tw / k, h2 = th / k;
        var small = new byte[w2 * h2 * 4];
        for (int y = 0; y < h2; y++) for (int x = 0; x < w2; x++) for (int c = 0; c < 4; c++)
        {
            int sum = 0;
            for (int dy = 0; dy < k; dy++) for (int dx = 0; dx < k; dx++) sum += full[4 * ((y * k + dy) * tw + x * k + dx) + c];
            small[4 * (y * w2 + x) + c] = (byte)(sum / (k * k));
        }
        foreach (var (name, tex, w, h, aref) in new[] { ("full90", full, tw, th, 90), ("mip90", small, w2, h2, 90), ("mip8", small, w2, h2, 8), ("full8", full, tw, th, 8) })
        {
            sc.Tex = tex; sc.TW = w; sc.TH = h; PortIT.Engine.Port.Viewer3D.AlphaRef = aref;
            var bg = PortIT.Engine.Port.Viewer3D.Draw(sc, 700, 800, new PortIT.Engine.Port.Viewer3D.Camera { Zoom = 4.5, PanY = 800 * 0.92 * 4.5 * 0.42 });
            var im = new Img(700, 800, 3);
            for (int i = 0; i < 700 * 800; i++) { im.Px[3 * i] = bg[4 * i + 2]; im.Px[3 * i + 1] = bg[4 * i + 1]; im.Px[3 * i + 2] = bg[4 * i]; }
            ImageIO.SavePng(im, $"{args[2]}_{name}.png");
        }
        ok++;
        break;
    }
    case "hairalpha":
    {
        // (args: pac) - the colour sheet's alpha under the hair meshes' (0x2710, cut-out) UVs: a histogram in 16 steps
        var arc = Epk8.Read(File.ReadAllBytes(args[1]));
        Img? col = null; var hairUv = new List<(float, float)>();
        foreach (var e in Pach.Read(arc.Groups[0].Entries[0].Data))
        {
            var raw = Bpe.Unpack(e.Data);
            if (Bundle.IsBundle(raw))
                foreach (var (name, dds) in PortIT.Engine.Build.SvrBuilder.TexBundleRead(raw))
                    if (name == (args.Length > 2 && args[2] == "normal" ? "normal" : "color")) col = ImageIO.Decode(dds).ToRgba();
            if (e.Id == 0x2710 && Jboy.IsJboy(raw))
                foreach (var m in Jboy.Read(raw).Meshes) if ((m.U88 & 0x110) != 0) hairUv.AddRange(m.Uvs.Select(u => (u.U, u.V)));
        }
        if (args.Length > 2 && args[2] != "normal") col = ImageIO.Decode(File.ReadAllBytes(args[2])).ToRgba();
        if (col == null) { Console.WriteLine("no colour"); break; }
        var hist = new int[16];
        // (texels inside the hair UVs' bounding boxes is too loose - sample around each UV point instead)
        foreach (var (u, v) in hairUv)
            for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++)
            {
                int x = Math.Clamp((int)((u - Math.Floor(u)) * col.W) + dx, 0, col.W - 1), y = Math.Clamp((int)((v - Math.Floor(v)) * col.H) + dy, 0, col.H - 1);
                hist[col.Px[4 * (y * col.W + x) + 3] / 16]++;
            }
        long tot = hist.Sum();
        Console.WriteLine($"{Path.GetFileName(args[1])} {col.W}x{col.H}: " + string.Join(" ", hist.Select((c, i) => $"{i * 16}:{100.0 * c / tot:F0}%")));
        ok++;
        break;
    }
    case "alphas":
    {
        if (!args[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("alphas: the first argument is the OUTPUT .png"); return 2; }
        // (args: out.png dds...) - each dds's alpha channel side by side (at most 1024 wide each), and the colour too
        var ims = args.Skip(2).Select(f => ImageIO.Decode(File.ReadAllBytes(f)).ToRgba()).ToList();
        int w = 1024, h = 512;
        var o = new Img(w * ims.Count, h * 2, 3);
        for (int k = 0; k < ims.Count; k++)
        {
            var im = ims[k];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = x * im.W / w, sy = y * im.H / h, si = 4 * (sy * im.W + sx);
                    int a = 3 * (y * o.W + k * w + x), c = 3 * ((y + h) * o.W + k * w + x);
                    o.Px[a] = o.Px[a + 1] = o.Px[a + 2] = im.Px[si + 3];
                    o.Px[c] = im.Px[si]; o.Px[c + 1] = im.Px[si + 1]; o.Px[c + 2] = im.Px[si + 2];
                }
            Console.WriteLine($"{Path.GetFileName(args[2 + k])}: {im.W}x{im.H}");
        }
        ImageIO.SavePng(o, args[1]);
        ok++;
        break;
    }
    case "meshparams":
    {
        // (args: pac child_hex) - each mesh of that child: shader, flags, params
        var arc = Epk8.Read(File.ReadAllBytes(args[1]));
        foreach (var e in Pach.Read(arc.Groups[0].Entries[0].Data))
        {
            if (e.Id != Convert.ToUInt32(args[2], 16)) continue;
            var j = Jboy.Read(Bpe.Unpack(e.Data));
            foreach (var m in j.Meshes)
            {
                Console.WriteLine($"mesh {m.Shader} verts {m.Verts.Count} U88 {m.U88:x} Vfmt {m.Vfmt} Ua0 {m.Ua0:x} U74 {m.U74} raw {Convert.ToHexString(m.Raw)}");
                if (args.Length > 3) continue;
                foreach (var p in m.Params) Console.WriteLine($"   {p.Name,-22} t{p.Type:x2} {Convert.ToHexString(p.Value)}");
            }
        }
        ok++;
        break;
    }
    case "hairtest":
    {
        // (args: pac out_prefix [color.dds]) - the hair test: rest pose check, then frames of the test motion
        var sc = PortIT.Engine.Port.Viewer3D.Load(args[1], 0, args.Length > 3 ? args[3] : null);
        var sim = PortIT.Engine.Port.Viewer3D.Sim(sc)!;
        Console.WriteLine($"chains {sim.Chains}");
        // rest: everything not hair must sit exactly where it was built
        PortIT.Engine.Port.Viewer3D.Pose(sc, sim);
        foreach (var grp in sc.Parts.GroupBy(p => p.Model))
        {
            double worst = 0;
            foreach (var p in grp) for (int i = 0; i < p.V.Length; i++) worst = Math.Max(worst, Math.Abs(p.PV![i] - p.V[i]));
            Console.WriteLine($"model {grp.Key:x}: rest pose off by at most {worst:F4} units (hair settles under gravity)");
        }
        if (sc.Nodes.TryGetValue(0x2710, out var hn))
        {
            int ia = hn.FindIndex(n => n.Name == "atama");
            var lone = new Dictionary<string, int>();
            foreach (var p in sc.Parts.Where(p => p.Model == 0x2710))
                for (int v = 0; v < p.Verts; v++)
                {
                    if (p.V[3 * v + 1] < -7.6) continue;              // (only the hair below the neck top)
                    bool kami = false; string top = ""; float tw = 0;
                    for (int k = 0; k < 4; k++) { int b = p.Inf[4 * v + k]; if (b < 0) continue; if (hn[b].Name.StartsWith("kami")) kami = true; if (p.Wt[4 * v + k] > tw) { tw = p.Wt[4 * v + k]; top = hn[b].Name; } }
                    if (!kami) lone[top] = lone.GetValueOrDefault(top) + 1;
                }
            Console.WriteLine("hair below the neck on no chain: " + string.Join(", ", lone.Select(kv => $"{kv.Key} {kv.Value}")));
        }
        var t1 = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 1; f <= 150; f++)
        {
            sim.Step(1.0);
            if (f % 30 == 0)
            {
                PortIT.Engine.Port.Viewer3D.Pose(sc, sim);
                foreach (var (vn, yaw) in new[] { ("back", Math.PI), ("side", -1.57) })
                {
                    var bgra = PortIT.Engine.Port.Viewer3D.Draw(sc, 800, 800, new PortIT.Engine.Port.Viewer3D.Camera { Yaw = yaw, Zoom = 1.6, PanY = 250 });
                    var rgb = new Img(800, 800, 3);
                    for (int i = 0; i < 800 * 800; i++) { rgb.Px[3 * i] = bgra[4 * i + 2]; rgb.Px[3 * i + 1] = bgra[4 * i + 1]; rgb.Px[3 * i + 2] = bgra[4 * i]; }
                    ImageIO.SavePng(rgb, $"{args[2]}_{f:000}_{vn}.png");
                }
                if (f == 60)
                {
                    var keep = sc.Parts.ToList();
                    foreach (var only in new[] { 0, 0x2710, 0x4e20 })
                    {
                        sc.Parts = keep.Where(p => p.Model == only).ToList();
                        var bg = PortIT.Engine.Port.Viewer3D.Draw(sc, 800, 800, new PortIT.Engine.Port.Viewer3D.Camera { Yaw = Math.PI, Zoom = 1.6, PanY = 250 });
                        var im = new Img(800, 800, 3);
                        for (int i = 0; i < 800 * 800; i++) { im.Px[3 * i] = bg[4 * i + 2]; im.Px[3 * i + 1] = bg[4 * i + 1]; im.Px[3 * i + 2] = bg[4 * i]; }
                        ImageIO.SavePng(im, $"{args[2]}_only{only:x}.png");
                    }
                    sc.Parts = keep;
                }
                foreach (var (P, Tip) in sim.ChainPoints()) Console.Write($"[{Tip[0]:F2} {Tip[1]:F2} {Tip[2]:F2}] ");
                Console.WriteLine($" frame {f}");
            }
        }
        Console.WriteLine($"150 frames: {t1.ElapsedMilliseconds} ms");
        ok++;
        break;
    }
    case "view3d":
    {
        // (args: pac out_prefix [color.dds]) - the 3D viewer's frames at a few angles, with their times
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        var sc = PortIT.Engine.Port.Viewer3D.Load(args[1], 0, args.Length > 3 ? args[3] : null);
        Console.WriteLine($"loaded {sc.Verts} verts {sc.Tris} tris, texture {sc.TW}x{sc.TH}: {sw0.ElapsedMilliseconds} ms");
        var views = new List<(string, double, double, double, bool, bool, double, double)>();
        if (args.Length > 4)
            foreach (var v in args[4].Split(';'))
            {
                var f = v.Split(',');
                views.Add((f[0], double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture),
                           double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture), true, f.Length > 6 && f[6] == "w",
                           double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture), double.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture)));
            }
        foreach (var (vn, vy, vp, vz, vs, vw, vpx, vpy) in views)
        {
            var cam = new PortIT.Engine.Port.Viewer3D.Camera { Yaw = vy, Pitch = vp, Zoom = vz, Wire = vw, PanX = vpx, PanY = vpy };
            var bgra = PortIT.Engine.Port.Viewer3D.Draw(sc, 1200, 900, cam, vs);
            var rgb = new Img(1200, 900, 3);
            for (int i = 0; i < 1200 * 900; i++) { rgb.Px[3 * i] = bgra[4 * i + 2]; rgb.Px[3 * i + 1] = bgra[4 * i + 1]; rgb.Px[3 * i + 2] = bgra[4 * i]; }
            ImageIO.SavePng(rgb, $"{args[2]}_{vn}.png");
        }
        if (views.Count > 0) { ok++; break; }
        foreach (var (name, yaw, pitch, zoom, smooth, wire) in new[] { ("front", 0.0, 0.0, 1.0, true, false), ("turn", 0.7, 0.25, 1.0, true, false),
                                                                      ("back", Math.PI, 0.0, 1.0, false, false), ("face", 0.3, 0.1, 4.5, true, false), ("wire", 0.5, 0.0, 3.0, true, true) })
        {
            var cam = new PortIT.Engine.Port.Viewer3D.Camera { Yaw = yaw, Pitch = pitch, Zoom = zoom, Wire = wire, PanY = zoom > 2 ? 900 * 0.92 * zoom * 0.42 : 0 };
            var t0x = System.Diagnostics.Stopwatch.StartNew();
            var bgra = PortIT.Engine.Port.Viewer3D.Draw(sc, 1200, 900, cam, smooth);
            long ms = t0x.ElapsedMilliseconds;
            var rgb = new Img(1200, 900, 3);
            for (int i = 0; i < 1200 * 900; i++) { rgb.Px[3 * i] = bgra[4 * i + 2]; rgb.Px[3 * i + 1] = bgra[4 * i + 1]; rgb.Px[3 * i + 2] = bgra[4 * i]; }
            ImageIO.SavePng(rgb, $"{args[2]}_{name}.png");
            Console.WriteLine($"{name}: {ms} ms");
        }
        ok++;
        break;
    }
    case "zoom":
    {
        // (args: pac out.png W H topFrac [color.dds]) - a big close-up render for checking by eye
        PortIT.Engine.Port.Preview.RenderZoom(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture), args.Length > 6 ? args[6] : null);
        Console.WriteLine(args[2]); ok++;
        break;
    }
    case "cawmenu":
    {
        // (args[1]: the game's pac folder; args[2] optional category) - edit.pac MENU: per category its record count, per
        //  record (60 bytes) the model / texture ids (u16 at +12 / +14) and colour slots (+20), as caw_part.py reads them
        var top = Epac.Read(File.ReadAllBytes(Path.Combine(args[1], "edit", "edit.pac"))).Groups[0].Entries.ToDictionary(e => e.NameText, e => e.Data);
        var menu0 = Pach.Read(Bpe.Unpack(top["MENU"])).First(e => e.Id == 0).Data;
        foreach (var c in Pach.Read(Bpe.Unpack(menu0)))
        {
            if (args.Length > 2 && c.Id != uint.Parse(args[2])) continue;
            var k = Bpe.Unpack(c.Data);
            int total = BitConverter.ToUInt16(k, 0);
            int withModel = 0;
            var lines = new List<string>();
            for (int r = 0; r < total && 12 + (r + 1) * 60 <= k.Length; r++)
            {
                var rec = k.AsSpan(12 + r * 60, 60);
                ushort mid = BitConverter.ToUInt16(rec[12..]), tid = BitConverter.ToUInt16(rec[14..]);
                if (mid != 0xFFFF) withModel++;
                if (args.Length > 2) lines.Add($"  rec {r,3}: model M{mid:X3} tex T{tid:X3} colours {rec[20]}  bytes {Convert.ToHexString(rec[..24].ToArray())}");
            }
            Console.WriteLine($"category {c.Id,3}: {total} records, {withModel} with a model");
            foreach (var l in lines.Take(400)) Console.WriteLine(l);
        }
        ok++;
        break;
    }
    case "grid":
    {
        // (args: out.png cols cell img...) - images in a grid, each cell x cell (aspect kept), a light frame between
        int cols = int.Parse(args[2]), cell = int.Parse(args[3]), k = args.Length - 4, rows = (k + cols - 1) / cols;
        var o = new Img(cols * cell, rows * cell, 3);
        for (int j = 0; j < k; j++)
        {
            var src = ImageIO.Load(args[4 + j]).ToRgba();
            double s = Math.Min((double)cell / src.W, (double)cell / src.H);
            int w = Math.Max(1, (int)(src.W * s)), h = Math.Max(1, (int)(src.H * s));
            var im = PortIT.Engine.Imaging.Resample.Resize(src, w, h, PortIT.Engine.Imaging.Filter.Bilinear);
            int ox = (j % cols) * cell, oy = (j / cols) * cell;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 3; c++) o.Px[3 * ((oy + y) * o.W + ox + x) + c] = im.Px[4 * (y * w + x) + c];
            for (int x = 0; x < cell; x++) for (int c = 0; c < 3; c++) o.Px[3 * (oy * o.W + ox + x) + c] = 200;
            for (int y = 0; y < cell; y++) for (int c = 0; c < 3; c++) o.Px[3 * ((oy + y) * o.W + ox) + c] = 200;
        }
        ImageIO.SavePng(o, args[1]);
        ok++;
        break;
    }
    case "preview":
    {
        // (args[1]: out folder, args[2..]: pacs) - each pac's front / side render (attire 0) as <name>.png
        Directory.CreateDirectory(args[1]);
        foreach (var p in args.Skip(2))
            try { PortIT.Engine.Port.Preview.Render(p, Path.Combine(args[1], Path.GetFileNameWithoutExtension(p) + ".png")); Console.WriteLine("ok " + p); }
            catch (Exception ex) { Console.WriteLine($"FAIL {p}: {ex.Message}"); }
        ok++;
        break;
    }
    case "pactree":
    {
        // (args[1]: a pac (EPK8 / EPAC); args[2] optional "nodes") - groups, entries, PACH ids, per JBOY: meshes, nodes, textures
        var data = File.ReadAllBytes(args[1]);
        bool showNodes = args.Length > 2 && args[2] == "nodes";
        string? saveDir = args.Length > 3 && args[2] == "save" ? args[3] : null;
        if (saveDir != null) Directory.CreateDirectory(saveDir);
        void Walk(string path, byte[] blob, int depth, string full = "")
        {
            var raw = Bpe.Unpack(blob);
            full = full.Length > 0 ? full + "_" + path : path;
            if (saveDir != null && !Pach.IsPach(raw)) File.WriteAllBytes(Path.Combine(saveDir, full + ".bin"), raw);
            string pad = new string(' ', 2 * depth);
            if (Pach.IsPach(raw))
            {
                var es = Pach.Read(raw);
                Console.WriteLine($"{pad}{path}: PACH {es.Count} [{string.Join(" ", es.Select(e => e.Id.ToString("x")))}]");
                foreach (var e in es) Walk($"{e.Id:x4}", e.Data, depth + 1, full);
            }
            else if (Jboy.IsJboy(raw))
            {
                var m = Jboy.Read(raw);
                Console.WriteLine($"{pad}{path}: JBOY '{m.Name}' {raw.Length / 1024} KB, {m.Meshes.Count} meshes, {m.Meshes.Sum(s => s.Verts.Count)} verts, {m.Nodes.Count} nodes, tex [{string.Join(",", m.Textures)}]");
                foreach (var s in m.Meshes)
                {
                    var b = s.Verts.Count > 0 ? (s.Verts.Min(v => v.PosNrm[1]), s.Verts.Max(v => v.PosNrm[1])) : (0f, 0f);
                    Console.WriteLine($"{pad}   mesh {s.Shader,-12} v {s.Verts.Count,5} pal [{string.Join(",", s.Palette.Select(p => p < m.Nodes.Count ? m.Nodes[p].Name : p.ToString()))}] y {b.Item1:F2}..{b.Item2:F2}");
                }
                if (showNodes) Console.WriteLine($"{pad}   nodes: {string.Join(" ", m.Nodes.Select(n => n.Name))}");
            }
            else Console.WriteLine($"{pad}{path}: {System.Text.Encoding.Latin1.GetString(raw, 0, Math.Min(4, raw.Length)).Replace('\0', '.')} {raw.Length} bytes");
        }
        if (Epk8.IsEpk8(data))
            foreach (var g in Epk8.Read(data).Groups)
            {
                Console.WriteLine($"GROUP {g.TypeText} {g.Entries.Count}");
                foreach (var e in g.Entries) Walk(e.NameText, e.Data, 1);
            }
        else if (data.AsSpan(0, 4).SequenceEqual("EPAC"u8))
            foreach (var g in Epac.Read(data).Groups)
            {
                Console.WriteLine($"GROUP {g.TypeText} {g.Entries.Count}");
                foreach (var e in g.Entries) Walk(e.NameText, e.Data, 1);
            }
        else Console.WriteLine("not EPK8: " + System.Text.Encoding.Latin1.GetString(data, 0, 4));
        ok++;
        break;
    }
    case "fbxmeshes":
    {
        // (args[1]: .fbx [bone regex]) - each mesh: size, box, top bones by weight, uv box; bones matching the regex
        var sc = PortIT.Engine.Readers.SourceFbx.Load(args[1]);
        Console.WriteLine($"{sc.Bones.Count} bones, {sc.Meshes.Count} meshes, unit {sc.UnitCm}");
        foreach (var m in sc.Meshes)
        {
            int n = m.Count;
            double Mn(int k) => Enumerable.Range(0, n).Min(i => m.V[3 * i + k]);
            double Mx(int k) => Enumerable.Range(0, n).Max(i => m.V[3 * i + k]);
            var top = m.W.Select(kv => (sc.Bones[kv.Key].Name, S: kv.Value.Sum())).OrderByDescending(x => x.S).Take(5);
            double tot = m.W.Values.Sum(w => w.Sum());
            Console.WriteLine($"{m.Name,-12} v {n,6} t {m.Tris.Length / 3,6}  x {Mn(0),6:F1}..{Mx(0),6:F1} y {Mn(1),6:F1}..{Mx(1),6:F1} z {Mn(2),6:F1}..{Mx(2),6:F1}" +
                              $"  uv {m.Uv.Where((_, i) => i % 2 == 0).Min():F2}..{m.Uv.Where((_, i) => i % 2 == 0).Max():F2},{m.Uv.Where((_, i) => i % 2 == 1).Min():F2}..{m.Uv.Where((_, i) => i % 2 == 1).Max():F2}" +
                              $"  [{string.Join(" ", m.Textures)}]");
            Console.WriteLine("     " + string.Join("  ", top.Select(t => $"{t.Name} {100 * t.S / tot:F0}%")));
        }
        if (args.Length > 2)
            for (int i = 0; i < sc.Bones.Count; i++)
                if (System.Text.RegularExpressions.Regex.IsMatch(sc.Bones[i].Name, args[2]))
                {
                    var w = sc.Bones[i].World;
                    Console.WriteLine($"  {sc.Bones[i].Name,-34} {w[12],8:F2} {w[13],8:F2} {w[14],8:F2}  parent {(sc.Bones[i].Parent >= 0 ? sc.Bones[sc.Bones[i].Parent].Name : "-")}{(sc.Bones[i].Skinned ? "" : "  (no skin)")}");
                }
        ok++;
        break;
    }
    case "bonechunk":
    {
        // (args: .mdl) - the BONE chunk's header words and the first bytes, then each parsed bone (name, parent, T)
        var m = PortIT.Engine.Readers.Mcd.Load(args[1]);
        int p = m.Chunks.GetValueOrDefault("BONE", -1);
        var R = m.Raw;
        uint W(int o) => BitConverter.ToUInt32(R, o);
        Console.WriteLine($"{m.Game} BONE at {p}: size {W(p + 4)}  words {W(p + 8)} {W(p + 12)} {W(p + 16)} {W(p + 20)}  bones parsed {m.Bones.Count}");
        foreach (var (b, i) in m.Bones.Select((b, i) => (b, i)).Where(x => x.i < 12 || x.b.Name.StartsWith("J_Foot") || x.b.Name.StartsWith("J_Knee") || x.b.Name.StartsWith("J_Head")))
            Console.WriteLine($"  {i,3} {b.Name,-22} parent {b.Parent,4}  T {b.T[0],7:F2} {b.T[1],7:F2} {b.T[2],7:F2}");
        ok++;
        break;
    }
    case "moveset":
    {
        // (args: game base from) - the moves.txt a mod on `base` gets to play with `from`'s move-set
        var pros = PortIT.Engine.Readers.Profiles.Read(args[1]);
        Console.WriteLine($"{pros.Count} profiles, sizes {string.Join(",", pros.Values.Select(v => v.Length).Distinct())}");
        var lines = PortIT.Engine.Readers.Profiles.MovesTxt(pros, int.Parse(args[2]), int.Parse(args[3]));
        Console.WriteLine($"{lines.Count} slots differ: {string.Join(" ", lines.Take(12))} ...");
        ok++;
        break;
    }
    case "mdlinfo":
    {
        // (args: .mdl/.mcd...) - version, bones, each mesh: name, object, vertices, influences
        foreach (var p in args.Skip(1))
        {
            var m = PortIT.Engine.Readers.Mcd.Load(p);
            Console.WriteLine($"== {Path.GetFileName(p)}  {m.Game}  bones {m.Bones.Count}  lods {m.Lods.Count}");
            for (int k = 0; k < m.Meshes.Count; k++)
                Console.WriteLine($"  {m.Meshes[k].Material,-24} obj {m.Meshes[k].Object,-20} {m.Meshes[k].Vertices,6} verts  inf {m.Meshes[k].Influences}");
        }
        ok++;
        break;
    }
    case "crop":
    {
        // (args: out.png img x y w h) - a part of an image (DDS too), as is
        if (!args[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("crop: the first argument is the OUTPUT .png"); return 2; }
        var im = ImageIO.Decode(File.ReadAllBytes(args[2]), Path.GetExtension(args[2])).ToRgba();
        int cx = int.Parse(args[3]), cy = int.Parse(args[4]), cw = int.Parse(args[5]), chh = int.Parse(args[6]);
        var o = new Img(cw, chh, 3);
        for (int y = 0; y < chh; y++) for (int x = 0; x < cw; x++) for (int c = 0; c < 3; c++) o.Px[3 * (y * cw + x) + c] = im.Px[4 * ((cy + y) * im.W + cx + x) + c];
        ImageIO.SavePng(o, args[1]);
        Console.WriteLine($"{im.W}x{im.H}");
        ok++;
        break;
    }
    case "imgdiff":
    {
        // (args: out.png a b) - b where it differs from a (by more than 8), dimmed a elsewhere
        if (!args[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("imgdiff: the first argument is the OUTPUT .png"); return 2; }
        var a = ImageIO.Load(args[2]).ToRgba(); var b = ImageIO.Load(args[3]).ToRgba();
        var o = new Img(a.W, a.H, 3); int nd = 0;
        for (int i = 0; i < a.W * a.H; i++)
        {
            bool d = Enumerable.Range(0, 3).Any(c => Math.Abs(a.Px[4 * i + c] - b.Px[4 * i + c]) > 8);
            if (d) nd++;
            for (int c = 0; c < 3; c++) o.Px[3 * i + c] = d ? b.Px[4 * i + c] : (byte)(a.Px[4 * i + c] / 4);
        }
        ImageIO.SavePng(o, args[1]);
        Console.WriteLine($"{nd} pixels differ");
        ok++;
        break;
    }
    case "uvrange":
    {
        // (args: wrestler_folder attire out_dir) - each source mesh's uv box and how many triangles reach past 0-1
        var rf = new PortIT.Engine.Port.RockFbx { QuickRead = true };
        var (ms, bns) = rf.Build(args[1], args[2], args[3]);
        foreach (var b in bns.Where(b => b.Src is "J_Eye_L" or "J_Eye_R" or "J_Head"))
            Console.WriteLine($"bone {b.Src,-8} {b.World[3],7:F2} {b.World[7],7:F2} {b.World[11],7:F2}");
        foreach (var m in ms.Where(m => m.Name == "M_Head"))
        {
            // (triangles whose uv sits in a top corner square of the head map)
            var corner = new List<int>();
            for (int t = 0; t < m.Tris.Length; t += 3)
            {
                double u = 0, v = 0;
                for (int k = 0; k < 3; k++) { u += m.Uv[2 * m.Tris[t + k]] / 3; v += m.Uv[2 * m.Tris[t + k] + 1] / 3; }
                if (v > 0.93 && (u < 0.07 || u > 0.93)) corner.Add(t);
            }
            if (corner.Count > 0)
            {
                var pts = corner.SelectMany(t => new[] { m.Tris[t], m.Tris[t + 1], m.Tris[t + 2] }).Distinct().ToList();
                Console.WriteLine($"head corner-square triangles: {corner.Count}, y {pts.Min(i => m.Verts[3 * i + 1]):F2}..{pts.Max(i => m.Verts[3 * i + 1]):F2} z {pts.Min(i => m.Verts[3 * i + 2]):F2}..{pts.Max(i => m.Verts[3 * i + 2]):F2} x {pts.Min(i => m.Verts[3 * i]):F2}..{pts.Max(i => m.Verts[3 * i]):F2}");
            }
            else Console.WriteLine("head corner-square triangles: 0");
        }
        foreach (var m in ms.Where(m => m.Name == "M_EYE"))
            foreach (var (side, sel) in new[] { ("L(+x)", 1), ("R(-x)", -1) })
            {
                var idx = Enumerable.Range(0, m.N).Where(i => Math.Sign(m.Verts[3 * i]) == sel).ToList();
                if (idx.Count == 0) { Console.WriteLine($"eye {side}: no points"); continue; }
                double[] lo = [9e9, 9e9, 9e9], hi = [-9e9, -9e9, -9e9];
                foreach (int i in idx) for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], m.Verts[3 * i + c]); hi[c] = Math.Max(hi[c], m.Verts[3 * i + c]); }
                Console.WriteLine($"eye {side}: {idx.Count} points  centre {(lo[0] + hi[0]) / 2:F2} {(lo[1] + hi[1]) / 2:F2} {(lo[2] + hi[2]) / 2:F2}  size {hi[0] - lo[0]:F2} {hi[1] - lo[1]:F2} {hi[2] - lo[2]:F2}");
            }
        foreach (var m in ms)
        {
            int n = m.Uv.Length / 2;
            if (n == 0) continue;
            double u0 = 9, u1 = -9, v0 = 9, v1 = -9;
            for (int i = 0; i < n; i++) { u0 = Math.Min(u0, m.Uv[2 * i]); u1 = Math.Max(u1, m.Uv[2 * i]); v0 = Math.Min(v0, m.Uv[2 * i + 1]); v1 = Math.Max(v1, m.Uv[2 * i + 1]); }
            int outside = 0;
            for (int t = 0; t < m.Tris.Length; t += 3)
                if (Enumerable.Range(0, 3).Any(k => m.Uv[2 * m.Tris[t + k]] < -0.001 || m.Uv[2 * m.Tris[t + k]] > 1.001 || m.Uv[2 * m.Tris[t + k] + 1] < -0.001 || m.Uv[2 * m.Tris[t + k] + 1] > 1.001)) outside++;
            Console.WriteLine($"{m.Name,-20} u {u0,6:F2}..{u1,6:F2}  v {v0,6:F2}..{v1,6:F2}  {outside} of {m.Tris.Length / 3} triangles past 0-1");
            double[] blo = [9e9, 9e9, 9e9], bhi = [-9e9, -9e9, -9e9];
            for (int i = 0; i < m.N; i++) for (int c = 0; c < 3; c++) { blo[c] = Math.Min(blo[c], m.Verts[3 * i + c]); bhi[c] = Math.Max(bhi[c], m.Verts[3 * i + c]); }
            int unw = Enumerable.Range(0, m.N).Count(i => m.Weights.Sum(w => w.W[i]) <= 0);
            Console.WriteLine($"{"",20} box x {blo[0],7:F1}..{bhi[0],7:F1} y {blo[1],7:F1}..{bhi[1],7:F1} z {blo[2],7:F1}..{bhi[2],7:F1}  unweighted {unw}");
            if (args.Length > 4 && m.Name == args[4])
                foreach (var (bi, wv) in m.Weights.OrderByDescending(x => x.W.Sum()).Take(14))
                {
                    double sw = wv.Sum(); if (sw <= 0) continue;
                    double[] c3 = [0, 0, 0]; for (int i = 0; i < m.N; i++) for (int c = 0; c < 3; c++) c3[c] += wv[i] * m.Verts[3 * i + c] / sw;
                    var bw = bns[bi].World;
                    Console.WriteLine($"{"",24}{bns[bi].Src,-22} weight {sw,7:F0}  points at {c3[0],6:F1} {c3[1],6:F1} {c3[2],6:F1}  bone at {bw[3],6:F1} {bw[7],6:F1} {bw[11],6:F1}");
                }
        }
        ok++;
        break;
    }
    case "sheet":
    {
        if (!args[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("sheet: the first argument is the OUTPUT .png"); return 2; }
        // (args: out.png img...) - each image 256 x 256 over magenta (its alpha shown), side by side
        int k = args.Length - 2;
        var o = new Img(256 * k, 256, 3);
        for (int j = 0; j < k; j++)
        {
            var im = PortIT.Engine.Imaging.Resample.Resize(ImageIO.Load(args[2 + j]).ToRgba(), 256, 256, PortIT.Engine.Imaging.Filter.Bilinear);
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    int s = 4 * (y * 256 + x), d = 3 * (y * o.W + 256 * j + x);
                    double a = im.Px[s + 3] / 255.0;
                    byte[] bg = [255, 0, 255];
                    for (int c = 0; c < 3; c++) o.Px[d + c] = (byte)(im.Px[s + c] * a + bg[c] * (1 - a));
                }
        }
        ImageIO.SavePng(o, args[1]);
        ok++;
        break;
    }
    case "tmplpos":
    {
        // (args[1]: template pac or null) - where the SvR template's main bones sit (Face.World11)
        var conv = new PortIT.Engine.Build.SvrBuilder.Template(args[1] == "-" ? null : args[1]);
        var Wt = PortIT.Engine.Port.Face.World11(conv);
        for (int i = 0; i < conv.TNodes.Count; i++)
            if (System.Text.RegularExpressions.Regex.IsMatch(conv.TNodes[i].Name, "^(root|atama|l_momo|r_momo|l_te|l_eye|r_eye|d_kuchi|l_tsumasaki|mune)$"))
                Console.WriteLine($"{conv.TNodes[i].Name,-12} {Wt[i][3],8:F3} {Wt[i][7],8:F3} {Wt[i][11],8:F3}");
        ok++;
        break;
    }
    case "fbxdump":
    {
        // (args[1]: a binary .fbx -> its units, meshes (+ layers), materials and their texture files, skins, bones)
        var f = new PortIT.Engine.Readers.Fbx(args[1]);
        f.Gather();
        Console.WriteLine($"version {f.Version}  unit {f.UnitCm} cm  up axis {f.UpAxis} sign {f.UpSign}");
        foreach (var o in f.Objects.Values.Where(o => o.Kind == "Geometry" && o.Sub == "Mesh"))
        {
            var n = o.Node;
            var model = o.Out.Select(x => x.O).FirstOrDefault(x => x.Kind == "Model");
            var mats = model?.In.Select(x => x.O).Where(x => x.Kind == "Material").ToList() ?? [];
            var skins = o.In.Select(x => x.O).Where(x => x.Kind == "Deformer" && x.Sub == "Skin").ToList();
            int clusters = skins.Sum(s => s.In.Count(x => x.O.Kind == "Deformer"));
            string layers = string.Join(",", n.Kids.Where(k => k.Name.StartsWith("LayerElement")).Select(k => $"{k.Name[12..]}:{k["MappingInformationType"]?.S(0)}/{k["ReferenceInformationType"]?.S(0)}"));
            Console.WriteLine($"MESH {model?.Name} pts {n["Vertices"]?.D().Length / 3} pv {n["PolygonVertexIndex"]?.I().Length} clusters {clusters} [{layers}]");
            foreach (var m in mats)
            {
                var tex = m.In.Where(x => x.O.Kind == "Texture").Select(x => $"{x.Prop}={Path.GetFileName((x.O.Node["RelativeFilename"]?.S(0) ?? x.O.Node["FileName"]?.S(0) ?? "").Replace('\\', '/'))}");
                Console.WriteLine($"   mat {m.Name}: {string.Join("  ", tex)}");
            }
        }
        var limbs = f.Objects.Values.Count(o => o.Kind == "Model" && o.Sub == "LimbNode");
        Console.WriteLine($"{limbs} bones; poses: {string.Join(",", f.Objects.Values.Where(o => o.Kind == "Pose").Select(o => o.Sub + ":" + o.Node.All("PoseNode").Count()))}");
        foreach (var o in f.Objects.Values.Where(o => o.Kind == "Model" && o.Sub == "LimbNode" && (args.Length < 3 || System.Text.RegularExpressions.Regex.IsMatch(o.Name, args[2]))))
        {
            var w = f.World(o);
            Console.WriteLine($"  {o.Name,-40} {w[12],8:F3} {w[13],8:F3} {w[14],8:F3}   parent {f.ParentModel(o)?.Name}");
        }
        ok++;
        break;
    }
    case "run":
    {
        // (args[1]: a request json -> the C# worker's answer, printed; progress lines to stdout)
        var req = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject();
        var res = new PortIT.Engine.Worker(l => Console.WriteLine("  | " + l)).Run(req);
        Console.WriteLine(res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (res["ok"]?.GetValue<bool>() == true) ok++; else bad++;
        var oo = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Where(m => m.ModuleName.StartsWith("oo2core", StringComparison.OrdinalIgnoreCase)).Select(m => m.FileName).ToList();
        Console.WriteLine("oo2core loaded: " + (oo.Count == 0 ? "no" : string.Join(", ", oo)));
        break;
    }
    default:
        Console.WriteLine("unknown check: " + args[0]);
        return 2;
}
Console.WriteLine($"{ok} same as Python, {bad} different ({t0.Elapsed.TotalSeconds:F1} s)");
return bad == 0 ? 0 : 1;
