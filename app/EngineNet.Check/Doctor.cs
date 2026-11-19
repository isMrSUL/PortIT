// Tools for working out WHY a ported model looks wrong, so the next bug report does not need a pile of
// throwaway scripts. Written 2026-11-17 after the Cactus Jack "clipping around the groin" report, which took a
// whole session of hand-written Python to pin down; every one of these is a thing that was needed that day.
//
//   portit-check meshshot  <out.png> <model.json | port folder> [x0 x1 y0 y1] [width]
//   portit-check clipping  <model.json | port folder> [top N]
//   portit-check replay    <report.zip | model folder> <work folder> [budget] [base]
//   portit-check portdiff  <A> <B>                       (port folders or model.json files)
//   portit-check textures  <folder of chNNNNN.pac>       (2K19 - 2K22: which meshes lose their picture)
//   portit-check chpac     <SvR chNNN.pac>               (what is in it, and can it be a port template)
//   portit-check batch     <folder of port runs>         (many ports at once: what went wrong, counted)
//
using System.Text.Json;
using System.Text.Json.Nodes;
using PortIT.Engine.Imaging;

namespace PortIT.Check;

public static class Doctor
{
    // ---------------------------------------------------------------- the model
    public sealed class Mesh
    {
        public string Name = "";
        public double[][] P = [];        // positions
        public double[][] N = [];        // normals
        public int[][] T = [];           // triangles
        public bool Skin;                // the body / head: what must stay under the gear
        public bool Gear;                // what covers it
        public bool Cards;               // hair / beard cards
    }

    /// <summary>the pieces that are skin, never gear (FullPort.NotGearPrefix)</summary>
    static readonly string[] NotGear = ["M_Body", "M_Head", "M_EYE", "M_TransB", "M_transB", "M_MouthBag", "mouth_",
                                        "M_AEWMouth", "M_InnerMouth", "M_Teeth", "M_Tongue"];

    public static string FindModel(string path)
    {
        if (File.Exists(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return path;
        foreach (var q in new[] { Path.Combine(path, "model.json"), Path.Combine(path, "_work", "model.json") })
            if (File.Exists(q)) return q;
        if (Directory.Exists(path))
        {
            var hit = Directory.EnumerateFiles(path, "model.json", SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return hit;
        }
        throw new FileNotFoundException($"no model.json in or under {path}");
    }

    public static List<Mesh> Load(string modelJson, int attire = 0)
    {
        var d = JsonNode.Parse(File.ReadAllText(modelJson))!.AsObject();
        var ms = d["attires"]![attire]!["meshes"]!.AsArray();
        var o = new List<Mesh>();
        foreach (var mn in ms)
        {
            var m = mn!.AsObject();
            string name = (string?)m["name"] ?? "";
            var me = new Mesh
            {
                Name = name,
                P = Rows(m["positions"]),
                N = Rows(m["normals"]),
                T = Ints(m["triangles"]),
                Skin = name.StartsWith("M_Body") || name.StartsWith("M_Head"),
            };
            // (hair and beard cards are not clothing: skin "showing through the hair" is not a fault)
            me.Cards = name.Contains("Hair", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Beard", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Combinations", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Facial", StringComparison.OrdinalIgnoreCase);
            me.Gear = !NotGear.Any(p => name.StartsWith(p, StringComparison.Ordinal)) && !me.Cards;
            o.Add(me);
        }
        return o;
    }

    static double[][] Rows(JsonNode? n)
    {
        if (n is not JsonArray a) return [];
        var o = new double[a.Count][];
        for (int i = 0; i < a.Count; i++)
        {
            var r = a[i]!.AsArray();
            o[i] = [r[0]!.GetValue<double>(), r[1]!.GetValue<double>(), r[2]!.GetValue<double>()];
        }
        return o;
    }

    static int[][] Ints(JsonNode? n)
    {
        if (n is not JsonArray a) return [];
        var o = new int[a.Count][];
        for (int i = 0; i < a.Count; i++)
        {
            var r = a[i]!.AsArray();
            o[i] = [r[0]!.GetValue<int>(), r[1]!.GetValue<int>(), r[2]!.GetValue<int>()];
        }
        return o;
    }

    // ---------------------------------------------------------------- small vector maths
    static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static double Len(double[] a) => Math.Sqrt(Math.Max(Dot(a, a), 0));
    static double[] Unit(double[] a) { double l = Math.Max(Len(a), 1e-12); return [a[0] / l, a[1] / l, a[2] / l]; }

    // ================================================================ meshshot
    /// <summary>
    /// The model drawn with one colour per mesh, so you can SEE which piece is on top. This is the tool that
    /// settled the Cactus Jack groin report: the port preview showed a skin-coloured wedge, which could have been
    /// the gear's own texture - drawing the body red and the tights blue proved it was the body.
    /// Front view (looking along -Z... whichever way the model faces: the side with more of the body towards it).
    /// </summary>
    public static int MeshShot(string outPng, string path, double[]? box, int width)
    {
        var model = Load(FindModel(path));
        var drawn = model.Where(m => m.P.Length > 0 && m.T.Length > 0).ToList();
        if (drawn.Count == 0) { Console.WriteLine("no meshes with geometry"); return 2; }
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
        foreach (var m in drawn) foreach (var p in m.P) for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], p[c]); hi[c] = Math.Max(hi[c], p[c]); }
        double x0 = box?[0] ?? lo[0], x1 = box?[1] ?? hi[0], y0 = box?[2] ?? lo[1], y1 = box?[3] ?? hi[1];
        if (x1 - x0 < 1e-6 || y1 - y0 < 1e-6) { Console.WriteLine("empty box"); return 2; }
        int W = width, H = Math.Max(16, (int)(W * (y1 - y0) / (x1 - x0)));
        var im = new Img(W, H, 3);
        for (int i = 0; i < W * H; i++) { im.Px[3 * i] = 16; im.Px[3 * i + 1] = 18; im.Px[3 * i + 2] = 24; }
        var zb = new double[W * H];
        Array.Fill(zb, double.NegativeInfinity);
        var owner = new int[W * H];
        Array.Fill(owner, -1);

        // a readable, repeatable colour per mesh - the body and the usual gear get fixed ones
        byte[][] Palette(string name, int i)
        {
            if (name.StartsWith("M_Body")) return [[230, 60, 60]];
            if (name.StartsWith("M_Head")) return [[250, 140, 60]];
            double h = (i * 0.618033988749895) % 1.0;
            double r = Math.Abs(((h * 6 + 0) % 6) - 3) - 1, g = Math.Abs(((h * 6 + 4) % 6) - 3) - 1, b = Math.Abs(((h * 6 + 2) % 6) - 3) - 1;
            return [[(byte)(55 + 200 * Math.Clamp(r, 0, 1)), (byte)(55 + 200 * Math.Clamp(g, 0, 1)), (byte)(55 + 200 * Math.Clamp(b, 0, 1))]];
        }
        var cols = new byte[drawn.Count][];
        for (int i = 0; i < drawn.Count; i++) cols[i] = Palette(drawn[i].Name, i)[0];

        double SX(double x) => (x - x0) / (x1 - x0) * (W - 1);
        double SY(double y) => (y - y0) / (y1 - y0) * (H - 1);
        var px = new long[drawn.Count];
        for (int mi = 0; mi < drawn.Count; mi++)
        {
            var m = drawn[mi];
            foreach (var t in m.T)
            {
                if (t[0] >= m.P.Length || t[1] >= m.P.Length || t[2] >= m.P.Length) continue;
                double[] A = m.P[t[0]], B = m.P[t[1]], C = m.P[t[2]];
                double ax = SX(A[0]), ay = SY(A[1]), az = A[2];
                double bx = SX(B[0]), by = SY(B[1]), bz = B[2];
                double cx = SX(C[0]), cy = SY(C[1]), cz = C[2];
                int lx = (int)Math.Max(0, Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
                int hx = (int)Math.Min(W - 1, Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
                int ly = (int)Math.Max(0, Math.Floor(Math.Min(ay, Math.Min(by, cy))));
                int hy = (int)Math.Min(H - 1, Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
                if (lx > hx || ly > hy) continue;
                double det = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                if (Math.Abs(det) < 1e-12) continue;
                for (int y = ly; y <= hy; y++)
                    for (int x = lx; x <= hx; x++)
                    {
                        double w0 = ((bx - ax) * (y - ay) - (x - ax) * (by - ay)) / det;
                        double w1 = ((x - ax) * (cy - ay) - (cx - ax) * (y - ay)) / det;
                        if (w0 < 0 || w1 < 0 || w0 + w1 > 1) continue;
                        double z = az + w1 * (bz - az) + w0 * (cz - az);
                        int o = y * W + x;
                        if (z <= zb[o]) continue;
                        zb[o] = z; owner[o] = mi;
                        im.Px[3 * o] = cols[mi][0]; im.Px[3 * o + 1] = cols[mi][1]; im.Px[3 * o + 2] = cols[mi][2];
                    }
            }
        }
        foreach (int o in owner) if (o >= 0) px[o]++;
        ImageIO.SavePng(im, outPng);
        Console.WriteLine($"{outPng}  {W}x{H}   box x {x0:F2}..{x1:F2}  y {y0:F2}..{y1:F2}");
        foreach (var (m, i) in drawn.Select((m, i) => (m, i)).Where(t => px[t.i] > 0).OrderByDescending(t => px[t.i]))
            Console.WriteLine($"   {Col(cols[i])}  {m.Name,-20} {px[i],8} pixels on top");
        Console.WriteLine("(+Y is DOWN in this space: y small = head, y large = feet)");
        return 0;
    }

    // ================================================================ clipdiff
    /// <summary>
    /// The same model ported two ways, compared the way an EYE sees it: every pixel where the bad port shows SKIN
    /// but the good one shows CLOTHES. That is the whole question a "clipping" report asks, and it needs no
    /// guessing about normals or creases - port the model once at full detail (or a big window) as the good one.
    /// Written after the Cactus Jack groin report, where per-point tests kept flagging his open shirt, which is
    /// meant to show, and this told the two apart at once.
    /// </summary>
    public static int ClipDiff(string bad, string good, string? outPng, double[]? box, int width)
    {
        var mb = Load(FindModel(bad));
        var mg = Load(FindModel(good));
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
        foreach (var m in mb.Concat(mg)) foreach (var p in m.P) for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], p[c]); hi[c] = Math.Max(hi[c], p[c]); }
        double x0 = box?[0] ?? lo[0], x1 = box?[1] ?? hi[0], y0 = box?[2] ?? lo[1], y1 = box?[3] ?? hi[1];
        int W = width, H = Math.Max(16, (int)(W * (y1 - y0) / (x1 - x0)));
        int worstTotal = 0;
        var im = new Img(W, H, 3);
        foreach (var (view, sx, sz) in new[] { ("front", 1.0, 1.0), ("back", -1.0, -1.0) })
        {
            var (ob, nb) = Raster(mb, W, H, x0, x1, y0, y1, sx, sz);
            var (og, ng) = Raster(mg, W, H, x0, x1, y0, y1, sx, sz);
            var hits = new Dictionary<string, int>();
            int n = 0;
            for (int i = 0; i < W * H; i++)
            {
                bool skinBad = ob[i] >= 0 && mb[ob[i]].Skin;
                bool gearGood = og[i] >= 0 && mg[og[i]].Gear;
                byte r = 26, g2 = 28, b = 34;
                if (ob[i] >= 0) { r = g2 = b = (byte)(mb[ob[i]].Skin ? 86 : 54); }
                if (skinBad && gearGood)
                {
                    n++;
                    hits[mg[og[i]].Name] = hits.GetValueOrDefault(mg[og[i]].Name) + 1;
                    r = 240; g2 = 60; b = 60;
                }
                im.Px[3 * i] = r; im.Px[3 * i + 1] = g2; im.Px[3 * i + 2] = b;
            }
            worstTotal += n;
            Console.WriteLine($"{view,-6}: {n} pixels show skin in the first port and clothes in the second"
                            + (hits.Count > 0 ? "   " + string.Join(", ", hits.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")) : ""));
            if (outPng != null && n > 0)
            {
                string q = view == "front" ? outPng : Path.Combine(Path.GetDirectoryName(outPng) ?? ".",
                           Path.GetFileNameWithoutExtension(outPng) + "_back" + Path.GetExtension(outPng));
                ImageIO.SavePng(im, q);
                Console.WriteLine($"         {q}  (the red pixels are the fault; grey is the first port)");
            }
        }
        Console.WriteLine(worstTotal == 0
            ? "no skin shows through the clothes that does not show in the good port too."
            : "the red areas are where the first port lost cover. If they sit where the gear is thin, the GEAR needs its detail back.");
        return 0;
    }

    /// <summary>who owns each pixel, looking along an axis (sx: 1 front / -1 back; depth sz)</summary>
    static (int[] Owner, int Drawn) Raster(List<Mesh> model, int W, int H, double x0, double x1, double y0, double y1, double sx, double sz)
    {
        var zb = new double[W * H];
        Array.Fill(zb, double.NegativeInfinity);
        var owner = new int[W * H];
        Array.Fill(owner, -1);
        double SX(double x) => ((sx * x) - (sx > 0 ? x0 : -x1)) / (x1 - x0) * (W - 1);
        double SY(double y) => (y - y0) / (y1 - y0) * (H - 1);
        int drawn = 0;
        for (int mi = 0; mi < model.Count; mi++)
        {
            var m = model[mi];
            foreach (var t in m.T)
            {
                if (t[0] >= m.P.Length || t[1] >= m.P.Length || t[2] >= m.P.Length) continue;
                double[] A = m.P[t[0]], B = m.P[t[1]], C = m.P[t[2]];
                double ax = SX(A[0]), ay = SY(A[1]), az = sz * A[2];
                double bx = SX(B[0]), by = SY(B[1]), bz = sz * B[2];
                double cx = SX(C[0]), cy = SY(C[1]), cz = sz * C[2];
                int lx = (int)Math.Max(0, Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
                int hx = (int)Math.Min(W - 1, Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
                int ly = (int)Math.Max(0, Math.Floor(Math.Min(ay, Math.Min(by, cy))));
                int hy = (int)Math.Min(H - 1, Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
                if (lx > hx || ly > hy) continue;
                double det = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                if (Math.Abs(det) < 1e-12) continue;
                for (int y = ly; y <= hy; y++)
                    for (int x = lx; x <= hx; x++)
                    {
                        double w0 = ((bx - ax) * (y - ay) - (x - ax) * (by - ay)) / det;
                        double w1 = ((x - ax) * (cy - ay) - (cx - ax) * (y - ay)) / det;
                        if (w0 < 0 || w1 < 0 || w0 + w1 > 1) continue;
                        double z = az + w1 * (bz - az) + w0 * (cz - az);
                        int o = y * W + x;
                        if (z <= zb[o]) continue;
                        if (owner[o] < 0) drawn++;
                        zb[o] = z; owner[o] = mi;
                    }
            }
        }
        return (owner, drawn);
    }

    static string Col(byte[] c) =>
        c[0] > 180 && c[1] < 110 && c[2] < 110 ? "RED   " :
        c[0] < 120 && c[1] < 160 && c[2] > 180 ? "BLUE  " :
        c[0] < 140 && c[1] > 160 ? "GREEN " :
        c[0] > 200 && c[1] > 160 && c[2] < 120 ? "YELLOW" :
        c[0] > 170 && c[1] < 150 && c[2] > 170 ? "PURPLE" :
        c[0] > 200 && c[1] > 110 && c[2] < 110 ? "ORANGE" : $"{c[0]},{c[1]},{c[2]}";

    // ================================================================ clipping
    sealed class Tri
    {
        public double[] A = [], B = [], C = [], Nrm = [];
        public int Mesh;
        public int[] V = [];      // global corner ids, for the shared-edge lookup
    }

    /// <summary>
    /// Where skin shows through the gear, and WHY it is still there. The two kinds are completely different bugs
    /// and PortIT fixes them in different places, so the report says which:
    ///  - "sticks out"  : the skin is OUTSIDE the gear surface. FullPort.TuckSkin should push it back in.
    ///  - "seen through a gap" : the skin is INSIDE the gear surface but the gear does not cover it any more -
    ///    its own geometry was thinned away. No skin-moving can fix that; the gear needs its detail back.
    /// It also prints the "surrounded by gear" number the crease pass uses, so a false positive (skin lying
    /// beside a small piece of gear, which must NOT be buried) is obvious.
    /// </summary>
    public static int Clipping(string path, int top, string? reference = null)
    {
        var mine = Scan(path, true);
        if (reference != null)
        {
            var refPts = Scan(reference, false);
            int before = mine.Count;
            // a point that shows in the GOOD port too is meant to show (an open shirt, a cut-out) - not a fault
            mine = mine.Where(f => !refPts.Any(r => Math.Abs(r.P[0] - f.P[0]) < 0.35 && Math.Abs(r.P[1] - f.P[1]) < 0.35 && Math.Abs(r.P[2] - f.P[2]) < 0.35)).ToList();
            Console.WriteLine($"\nagainst {reference}: {before - mine.Count} of {before} also show there (so they are meant to), {mine.Count} are new");
        }
        Report(mine, top, reference != null);
        return 0;
    }

    static void Report(List<Found> real, int top, bool compared)
    {
        Console.WriteLine();
        Console.WriteLine($"skin points that can show{(compared ? " AND do not in the good port" : "")}: {real.Count}");
        foreach (var grp in real.GroupBy(f => (f.Kind, f.Gear)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"   {grp.Count(),5}  {grp.Key.Kind,-38} through {grp.Key.Gear}");
        if (real.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine("worst:");
        foreach (var f in real.OrderByDescending(f => Math.Abs(f.Out)).Take(top))
            Console.WriteLine($"   {f.Out * 10,6:F1} cm  {f.Surround * 100,3:F0}% surrounded  {f.Skin} at ({f.P[0]:F2}, {f.P[1]:F2}, {f.P[2]:F2})  [{f.Kind}, {f.Gear}]");
        Console.WriteLine();
        if (real.Any(f => f.Kind.StartsWith("inside")))
            Console.WriteLine("  \"seen through a gap\" means the GEAR lost its own geometry (thinning). Moving skin cannot fix it -\n" +
                              "  check at full detail or a bigger window, and fix it on the gear side.");
        if (real.Any(f => f.Kind.StartsWith("sticks")))
            Console.WriteLine("  \"sticks out\" is FullPort.TuckSkin's job - run the port with PORTIT_TUCKDEBUG=1 to see why each one was left.");
    }

    public readonly record struct Found(double Out, double Surround, string Kind, string Gear, double[] P, string Skin);

    static List<Found> Scan(string path, bool talk)
    {
        string mj = FindModel(path);
        var model = Load(mj);
        var skin = model.Where(m => m.Skin && m.P.Length > 0).ToList();
        var gear = model.Where(m => m.Gear && m.T.Length > 0).ToList();
        if (talk)
        {
            Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(mj))}/{Path.GetFileName(mj)}");
            Console.WriteLine($"skin: {string.Join(", ", skin.Select(m => $"{m.Name}({m.P.Length})"))}");
            Console.WriteLine($"gear: {string.Join(", ", gear.Select(m => $"{m.Name}({m.P.Length})"))}");
            var cards = model.Where(m => m.Cards && m.T.Length > 0).Select(m => m.Name).ToList();
            if (cards.Count > 0) Console.WriteLine($"not counted as gear (hair / beard cards): {string.Join(", ", cards)}");
        }
        if (skin.Count == 0 || gear.Count == 0) { if (talk) Console.WriteLine("nothing to compare"); return []; }

        // which way do the normals point? (as FullPort does it: off the head's own middle)
        var hd = model.Where(m => m.Name.StartsWith("M_Head") && m.P.Length > 0).ToList();
        if (hd.Count == 0) hd = skin;
        var hm = new double[3];
        int nh = hd.Sum(m => m.P.Length);
        foreach (var m in hd) foreach (var p in m.P) for (int c = 0; c < 3; c++) hm[c] += p[c] / nh;
        int outward = 0;
        foreach (var m in hd) for (int i = 0; i < m.P.Length; i++) if (i < m.N.Length && Dot(Sub(m.P[i], hm), m.N[i]) > 0) outward++;
        double outSign = (double)outward / Math.Max(nh, 1) > 0.5 ? 1 : -1;

        var tris = new List<Tri>();
        var share = new Dictionary<(int, int), List<int>>();
        var vid = new Dictionary<(long, long, long), int>();
        int Vid(double[] p)
        {
            var k = ((long)Math.Round(p[0] * 2048), (long)Math.Round(p[1] * 2048), (long)Math.Round(p[2] * 2048));
            if (!vid.TryGetValue(k, out int v)) vid[k] = v = vid.Count;
            return v;
        }
        for (int gi = 0; gi < gear.Count; gi++)
        {
            var g = gear[gi];
            foreach (var t in g.T)
            {
                if (t[0] >= g.P.Length || t[1] >= g.P.Length || t[2] >= g.P.Length) continue;
                double[] A = g.P[t[0]], B = g.P[t[1]], C = g.P[t[2]];
                double[] n = t.Select(i => i).Count() > 0 && g.N.Length > t[2]
                    ? Unit([g.N[t[0]][0] + g.N[t[1]][0] + g.N[t[2]][0], g.N[t[0]][1] + g.N[t[1]][1] + g.N[t[2]][1], g.N[t[0]][2] + g.N[t[1]][2] + g.N[t[2]][2]])
                    : Unit(Cross(Sub(B, A), Sub(C, A)));
                var tr = new Tri { A = A, B = B, C = C, Nrm = [outSign * n[0], outSign * n[1], outSign * n[2]], Mesh = gi, V = [Vid(A), Vid(B), Vid(C)] };
                int ti = tris.Count;
                tris.Add(tr);
                for (int e = 0; e < 3; e++)
                {
                    int a = tr.V[e], b = tr.V[(e + 1) % 3];
                    var k = a < b ? (a, b) : (b, a);
                    if (!share.TryGetValue(k, out var l)) share[k] = l = [];
                    l.Add(ti);
                }
            }
        }

        const double Cell = 0.5;
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < tris.Count; i++)
        {
            var t = tris[i];
            for (int c = 0; c < 3; c++) { }
            int[] l = new int[3], h = new int[3];
            for (int c = 0; c < 3; c++)
            {
                l[c] = (int)Math.Floor(Math.Min(t.A[c], Math.Min(t.B[c], t.C[c])) / Cell);
                h[c] = (int)Math.Floor(Math.Max(t.A[c], Math.Max(t.B[c], t.C[c])) / Cell);
            }
            for (int x = l[0]; x <= h[0]; x++) for (int y = l[1]; y <= h[1]; y++) for (int z = l[2]; z <= h[2]; z++)
            {
                var k = (x, y, z);
                if (!grid.TryGetValue(k, out var g2)) grid[k] = g2 = [];
                g2.Add(i);
            }
        }
        List<int> Near(double[] p, int r)
        {
            var o = new List<int>();
            int kx = (int)Math.Floor(p[0] / Cell), ky = (int)Math.Floor(p[1] / Cell), kz = (int)Math.Floor(p[2] / Cell);
            for (int x = kx - r; x <= kx + r; x++) for (int y = ky - r; y <= ky + r; y++) for (int z = kz - r; z <= kz + r; z++)
                if (grid.TryGetValue((x, y, z), out var l)) o.AddRange(l);
            return o.Distinct().ToList();
        }

        var dirs = new List<double[]>();
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            if (x != 0 || y != 0 || z != 0) dirs.Add(Unit([x, y, z]));

        var found = new List<Found>();
        foreach (var m in skin)
            for (int v = 0; v < m.P.Length; v++)
            {
                var p = m.P[v];
                var cand = Near(p, 1);
                if (cand.Count == 0) continue;
                int bi = -1; double bd = double.MaxValue; double[] bq = [];
                foreach (int i in cand)
                {
                    var (q, _) = Closest(p, tris[i]);
                    double dd = Len(Sub(q, p));
                    if (dd < bd) { bd = dd; bi = i; bq = q; }
                }
                if (bi < 0 || bd > 1.5) continue;
                var pn = PseudoNormal(tris, share, bi, bq);
                double side = Dot(Sub(p, bq), pn);
                // can it be seen from outside? (a ray that escapes without meeting gear)
                int hit = 0;
                var wide = Near(p, 3);
                foreach (var dv in dirs)
                {
                    bool any = false;
                    foreach (int i in wide)
                        if (RayHit(p, dv, tris[i], out double tt) && tt > 1e-4 && tt < 2.0) { any = true; break; }
                    if (any) hit++;
                }
                double sur = (double)hit / dirs.Count;
                if (side > 0.05 && sur >= 0.5)
                    found.Add(new Found(side, sur, "sticks out of the gear", gear[tris[bi].Mesh].Name, p, m.Name));
                else if (side <= 0.05 && sur is > 0.15 and < 0.75 && side > -0.6)
                    found.Add(new Found(side, sur, "inside the gear but seen through a gap", gear[tris[bi].Mesh].Name, p, m.Name));
            }
        return found;
    }

    /// <summary>closest point on a triangle, and whether it landed on an edge (-1: inside the face)</summary>
    static (double[] Q, int Edge) Closest(double[] p, Tri t)
    {
        double[] ab = Sub(t.B, t.A), ac = Sub(t.C, t.A), ap = Sub(p, t.A);
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return (t.A, 0);
        double[] bp = Sub(p, t.B);
        double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return (t.B, 1);
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { double v = d1 / Math.Max(d1 - d3, 1e-12); return ([t.A[0] + ab[0] * v, t.A[1] + ab[1] * v, t.A[2] + ab[2] * v], 0); }
        double[] cp = Sub(p, t.C);
        double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return (t.C, 2);
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { double w = d2 / Math.Max(d2 - d6, 1e-12); return ([t.A[0] + ac[0] * w, t.A[1] + ac[1] * w, t.A[2] + ac[2] * w], 2); }
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            double w = (d4 - d3) / Math.Max((d4 - d3) + (d5 - d6), 1e-12);
            return ([t.B[0] + (t.C[0] - t.B[0]) * w, t.B[1] + (t.C[1] - t.B[1]) * w, t.B[2] + (t.C[2] - t.B[2]) * w], 1);
        }
        double den = 1.0 / Math.Max(va + vb + vc, 1e-12);
        double vv = vb * den, ww = vc * den;
        return ([t.A[0] + ab[0] * vv + ac[0] * ww, t.A[1] + ab[1] * vv + ac[1] * ww, t.A[2] + ab[2] * vv + ac[2] * ww], -1);
    }

    /// <summary>the normal to measure "which side" by: at an edge, one triangle's own normal says nothing</summary>
    static double[] PseudoNormal(List<Tri> tris, Dictionary<(int, int), List<int>> share, int ti, double[] q)
    {
        var t = tris[ti];
        for (int e = 0; e < 3; e++)
        {
            double[] P0 = e == 0 ? t.A : e == 1 ? t.B : t.C, P1 = e == 0 ? t.B : e == 1 ? t.C : t.A;
            var ab = Sub(P1, P0);
            double l2 = Math.Max(Dot(ab, ab), 1e-20);
            double s = Math.Clamp(Dot(Sub(q, P0), ab) / l2, 0, 1);
            double[] c = [P0[0] + ab[0] * s, P0[1] + ab[1] * s, P0[2] + ab[2] * s];
            if (Len(Sub(c, q)) > 1e-5) continue;
            int a = t.V[e], b = t.V[(e + 1) % 3];
            if (!share.TryGetValue(a < b ? (a, b) : (b, a), out var sl) || sl.Count < 2) break;
            double[] acc = [0, 0, 0];
            foreach (int k in sl) for (int c2 = 0; c2 < 3; c2++) acc[c2] += tris[k].Nrm[c2];
            if (Len(acc) > 1e-9) return Unit(acc);
            break;
        }
        return t.Nrm;
    }

    static bool RayHit(double[] p, double[] d, Tri t, out double hit)
    {
        hit = 0;
        var e1 = Sub(t.B, t.A); var e2 = Sub(t.C, t.A);
        var h = Cross(d, e2);
        double a = Dot(e1, h);
        if (Math.Abs(a) < 1e-12) return false;
        double f = 1 / a;
        var s = Sub(p, t.A);
        double u = f * Dot(s, h);
        if (u < -1e-6 || u > 1 + 1e-6) return false;
        var q = Cross(s, e1);
        double v = f * Dot(d, q);
        if (v < -1e-6 || u + v > 1 + 1e-6) return false;
        hit = f * Dot(e2, q);
        return true;
    }

    // ================================================================ replay
    /// <summary>
    /// A bug report in, a finished port out. Takes the zip a user sent (or a folder), unpacks it safely, finds the
    /// wrestler and its attires, reads the report log beside it for the settings they used, writes request.json and
    /// runs the port - so reproducing a report is one command instead of half an hour of guessing paths.
    /// </summary>
    /// <summary>
    /// A trap that cost a wrong answer once: there is no solution file, so `dotnet build` in the app folder builds
    /// only the app and its engine - NOT this tool, which then keeps running the engine it was last built with.
    /// Build with `dotnet build EngineNet.Check`, which builds both.
    /// </summary>
    public static void WarnIfStale()
    {
        try
        {
            string dll = typeof(PortIT.Engine.Worker).Assembly.Location;
            if (dll.Length == 0) return;
            var built = File.GetLastWriteTimeUtc(dll);
            var dir = new DirectoryInfo(Path.GetDirectoryName(dll)!);
            for (int up = 0; up < 8 && dir != null; up++, dir = dir.Parent)
            {
                var eng = Path.Combine(dir.FullName, "EngineNet");
                if (!Directory.Exists(eng)) continue;
                var newest = Directory.EnumerateFiles(eng, "*.cs", SearchOption.AllDirectories)
                                      .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
                if (newest > built)
                    Console.WriteLine($"WARNING: the engine source is newer than the engine this tool loaded " +
                                      $"({newest.ToLocalTime():HH:mm:ss} vs {built.ToLocalTime():HH:mm:ss}).\n" +
                                      $"         Run: dotnet build EngineNet.Check   (that builds the engine too;" +
                                      $" plain `dotnet build` in the app folder builds only the app, so this tool keeps the old engine.)");
                return;
            }
            Console.WriteLine("(could not check whether the engine build is up to date)");
        }
        catch (Exception) { }
    }

    public static int Replay(string report, string work, int budget, int baseId, string game)
    {
        WarnIfStale();
        if (!File.Exists(report) && !Directory.Exists(report)) { Console.WriteLine($"no such report: {report}"); return 2; }
        if (!Directory.Exists(game)) { Console.WriteLine($"no such game folder: {game}"); return 2; }
        Directory.CreateDirectory(work);
        string src = report;
        // .rar / .7z: users send those as often as zips (Orange Cassidy, 2026-11-17). 7-Zip unpacks both.
        if (File.Exists(report) && (report.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || report.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
        {
            src = Path.Combine(work, "src");
            if (Directory.Exists(src) && Directory.EnumerateFileSystemEntries(src).Any()) Console.WriteLine($"already unpacked in {src}");
            else
            {
                string? sevenZip = new[] { @"C:\Program Files\7-Zip\7z.exe", @"C:\Program Files (x86)\7-Zip\7z.exe" }.FirstOrDefault(File.Exists);
                if (sevenZip == null) { Console.WriteLine("this is a .rar / .7z and 7-Zip is not installed - unpack it by hand and pass the folder"); return 2; }
                Directory.CreateDirectory(src);
                var pi = new System.Diagnostics.ProcessStartInfo(sevenZip) { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "x", "-y", $"-o{src}", report }) pi.ArgumentList.Add(a);
                using var pr = System.Diagnostics.Process.Start(pi)!;
                string so = pr.StandardOutput.ReadToEnd();
                pr.WaitForExit();
                if (pr.ExitCode != 0) { Console.WriteLine("7-Zip could not unpack it:\n" + so); return 2; }
                Console.WriteLine($"unpacked with 7-Zip into {src}");
            }
        }
        else if (File.Exists(report) && report.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            src = Path.Combine(work, "src");
            if (!Directory.Exists(src) || !Directory.EnumerateFileSystemEntries(src).Any())
            {
                Directory.CreateDirectory(src);
                using var z = System.IO.Compression.ZipFile.OpenRead(report);
                string root = Path.GetFullPath(src);
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith('/')) continue;
                    string dst = Path.GetFullPath(Path.Combine(src, e.FullName));
                    if (!dst.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    { Console.WriteLine($"refusing: {e.FullName} points outside the folder"); return 2; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    using var es = e.Open();
                    using var fs = File.Create(dst);
                    es.CopyTo(fs);
                }
                Console.WriteLine($"unpacked {z.Entries.Count} entries into {src}");
            }
            else Console.WriteLine($"already unpacked in {src}");
        }
        // a wrestler folder = one with a BaseModel beside the attire folders
        string? chr = new[] { src }.Concat(Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            .FirstOrDefault(d => Directory.EnumerateDirectories(d).Any(s => Path.GetFileName(s).Equals("BaseModel", StringComparison.OrdinalIgnoreCase)));
        if (chr == null)
        {
            Console.WriteLine("no wrestler folder here (none has a BaseModel) - that alone is why some models 'do not load'");
            foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories).Take(20)) Console.WriteLine("   " + d);
            return 2;
        }
        var attires = Directory.EnumerateDirectories(chr)
            .Where(d => !Path.GetFileName(d).Equals("BaseModel", StringComparison.OrdinalIgnoreCase))
            .Select(d => Directory.EnumerateFiles(d, "*.mcd").FirstOrDefault())
            .OfType<string>().ToList();
        Console.WriteLine($"wrestler: {chr}");
        foreach (var a in attires) Console.WriteLine($"   attire: {Path.GetRelativePath(chr, a)}");
        if (attires.Count == 0) { Console.WriteLine("no attire .mcd"); return 2; }

        // the report log the user sent: the attire they used (by the pieces it lists) and the body type
        // (the user's own report log, if it is beside what they sent - prefer one actually called report*.log, and
        //  never fall over because some other .log in that folder is open or unreadable)
        string? logDir = Path.GetDirectoryName(Path.GetFullPath(report)) ?? src;
        var logs = Directory.Exists(logDir) ? Directory.EnumerateFiles(logDir, "*.log").ToList() : [];
        string? log = logs.FirstOrDefault(f => Path.GetFileName(f).StartsWith("report", StringComparison.OrdinalIgnoreCase))
                   ?? logs.FirstOrDefault();
        string pick = attires[0];
        string text = "";
        if (log != null) { try { text = File.ReadAllText(log); } catch (IOException) { log = null; } }
        if (log != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"body type: stock superstar (\d+)");
            if (m.Success && baseId == 0) baseId = int.Parse(m.Groups[1].Value);
            Console.WriteLine($"report log: {Path.GetFileName(log)}" + (m.Success ? $"  (body type {baseId})" : ""));
            // Which attire did they port? The log lists the pieces, so read each attire's own piece names and take
            // the one that matches best - with two attires this is the difference between reproducing their bug and
            // staring at the wrong model (it cost a wasted port on the Cactus Jack report).
            if (attires.Count > 1)
            {
                int best = -1;
                foreach (var a in attires)
                {
                    int score = 0, total = 0;
                    try
                    {
                        foreach (var mb in PortIT.Engine.Readers.Mcd.Load(a).Meshes)
                        {
                            string nm = mb.Material;
                            if (nm.Length == 0 || !nm.StartsWith("M_")) continue;
                            total++;
                            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + System.Text.RegularExpressions.Regex.Escape(nm) + @"\b")) score++;
                            else score--;
                        }
                    }
                    catch (Exception e) { Console.WriteLine($"   ({Path.GetFileName(a)}: {e.GetType().Name})"); continue; }
                    Console.WriteLine($"   {Path.GetRelativePath(chr, a),-45} {score,3} of {total} pieces match the log");
                    if (score > best) { best = score; pick = a; }
                }
            }
        }
        if (baseId == 0) baseId = 139;
        Console.WriteLine($"porting {Path.GetRelativePath(chr, pick)}");

        var req = new JsonObject
        {
            ["command"] = "port",
            ["source"] = "2k26",
            ["path"] = Path.GetDirectoryName(Path.GetFullPath(chr)),
            ["id"] = Path.GetFullPath(chr),
            ["attire"] = Path.GetRelativePath(chr, pick),
            ["game"] = game,
            ["out"] = Path.Combine(work, "out"),
            ["mod_id"] = "replay",
            ["mod_name"] = "Replay",
            ["base"] = (double)baseId,
            ["budget"] = (double)budget,
        };
        string rq = Path.Combine(work, "request.json");
        File.WriteAllText(rq, req.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"request: {rq}  (attire {req["attire"]}, base {baseId}, budget {budget})");
        var res = new PortIT.Engine.Worker(l => Console.WriteLine("  | " + l)).Run(req);
        File.WriteAllText(Path.Combine(work, "result.json"), res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        bool good = res["ok"]?.GetValue<bool>() == true;
        Console.WriteLine(good
            ? $"ported: {res["pages"]} pages -> {Path.Combine(work, "out")}\n" +
              $"next:  portit-check clipping {Path.Combine(work, "out")}\n" +
              $"       portit-check meshshot groin.png {Path.Combine(work, "out")} -2.2 2.2 0 2.6"
            : "FAILED: " + res["error"]);
        return good ? 0 : 1;
    }

    // ================================================================ portdiff
    /// <summary>Two ports side by side: pages, which pieces changed size, and how far their points moved.</summary>
    public static int PortDiff(string a, string b)
    {
        var A = Load(FindModel(a));
        var B = Load(FindModel(b));
        foreach (var (lbl, p) in new[] { ("A", a), ("B", b) })
        {
            string? rj = new[] { Path.Combine(p, "result.json"), Path.Combine(Path.GetDirectoryName(Path.GetFullPath(p)) ?? ".", "result.json") }.FirstOrDefault(File.Exists);
            if (rj != null)
            {
                var r = JsonNode.Parse(File.ReadAllText(rj))!.AsObject();
                Console.WriteLine($"{lbl}: {r["pages"]} pages, checks {r["checks"]?.ToJsonString()}");
            }
        }
        var names = A.Select(m => m.Name).Union(B.Select(m => m.Name)).ToList();
        Console.WriteLine($"{"piece",-22} {"A pts",8} {"B pts",8} {"A tris",8} {"B tris",8}   moved");
        foreach (var n in names)
        {
            var ma = A.FirstOrDefault(m => m.Name == n);
            var mb = B.FirstOrDefault(m => m.Name == n);
            string moved = "-";
            if (ma != null && mb != null && ma.P.Length == mb.P.Length && ma.P.Length > 0)
            {
                double worst = 0, sum = 0;
                for (int i = 0; i < ma.P.Length; i++) { double d = Len(Sub(ma.P[i], mb.P[i])); worst = Math.Max(worst, d); sum += d; }
                moved = worst < 1e-9 ? "same" : $"worst {worst * 10:F1} cm, mean {sum / ma.P.Length * 10:F2} cm";
            }
            else if (ma == null) moved = "only in B";
            else if (mb == null) moved = "only in A";
            Console.WriteLine($"{n,-22} {ma?.P.Length ?? 0,8} {mb?.P.Length ?? 0,8} {ma?.T.Length ?? 0,8} {mb?.T.Length ?? 0,8}   {moved}");
        }
        return 0;
    }

    // ---------------------------------------------------------------- 2K19 - 2K22 textures
    //
    // "sometimes 2K19 textures disappear from the model when I try to port him" (user, 2026-11-17).
    // A Yuke's pack names each mesh's colour map; the pictures themselves sit in a texture table, in
    // the same pack or in a txNNNN.pac beside it. YukesPack.Extract writes out only the ones it finds
    // and says nothing about the rest, so a missing picture is silent. This counts them.

    public sealed class TexRow
    {
        public string Pac = "", Mesh = "", Texture = "", Why = "", Colour = "";
        public int Points;
    }

    /// <summary>every mesh of every pack, and whether its colour map is really in the pack</summary>
    public static List<TexRow> PackTextures(string pac)
    {
        var rows = new List<TexRow>();
        var blobs = PortIT.Engine.Readers.YukesPack.Members(pac);
        var extra = PortIT.Engine.Readers.YukesPack.TexturePacks(pac);
        var tex = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var kv in PortIT.Engine.Readers.YukesPack.TexturesByTable(blobs)) tex[kv.Key] = kv.Value;
        foreach (var tx in extra)
        {
            var txb = PortIT.Engine.Readers.YukesPack.Members(tx);
            blobs.AddRange(txb);
            foreach (var kv in PortIT.Engine.Readers.YukesPack.TexturesByTable(txb)) tex[kv.Key] = kv.Value;
        }
        foreach (var b in blobs)
            if (PortIT.Engine.Readers.YukesPack.TexturesOf(b) is { } t)
                foreach (var kv in t) tex[kv.Key] = kv.Value;
        var models = new List<PortIT.Engine.Readers.YukesPack.Model>();
        foreach (var b in blobs)
            if (b.Length >= 4 && (b.AsSpan(0, 4).SequenceEqual("JBOY"u8) || b.AsSpan(0, 4).SequenceEqual("YOBJ"u8)))
                try { models.Add(PortIT.Engine.Readers.YukesPack.ReadJboy(b)); } catch (Exception) { }
        if (models.Count == 0) return rows;
        var whole = PortIT.Engine.Readers.YukesPack.Combine(models);
        foreach (var me in whole.Meshes)
        {
            string stem = PortIT.Engine.Readers.YukesPack.StemOf(me);
            string why;
            if (me.Diffuse.Length == 0) why = "the mesh names no colour map";
            else if (stem.Length == 0) why = "no name to save it under";
            else if (tex.ContainsKey(me.Diffuse.ToLowerInvariant())) why = "";
            else why = extra.Count > 0 ? "not in this pack nor in its tx pack" : "not in this pack (its txNNNN.pac is missing)";
            // the per-vertex colour, which is where 2K23+ keeps the HAIR COLOUR
            string col = "";
            if (me.Rgba.Length >= 4)
            {
                var r = new List<byte>(); var g = new List<byte>(); var b = new List<byte>();
                for (int i = 0; i + 3 < me.Rgba.Length; i += 4) { r.Add(me.Rgba[i]); g.Add(me.Rgba[i + 1]); b.Add(me.Rgba[i + 2]); }
                r.Sort(); g.Sort(); b.Sort();
                col = $"{r[r.Count / 2]},{g[g.Count / 2]},{b[b.Count / 2]}";
            }
            rows.Add(new TexRow { Pac = pac, Mesh = me.Name, Texture = me.Diffuse, Points = me.N, Why = why, Colour = col });
        }
        return rows;
    }

    /// <summary>a whole folder of packs: who loses a picture, and why</summary>
    public static int Textures(string folder)
    {
        var pacs = File.Exists(folder) ? new List<string> { folder }
            : Directory.EnumerateFiles(folder, "ch*.pac", SearchOption.AllDirectories)
                       .Where(p => PortIT.Engine.Readers.YukesPack.IsCharacterPack(p)).Order(StringComparer.Ordinal).ToList();
        Console.WriteLine($"{pacs.Count} character pack(s)");
        if (pacs.Count == 1)        // one pack: also list the pictures it and its tx packs actually hold
        {
            var packs = new List<string> { pacs[0] };
            packs.AddRange(PortIT.Engine.Readers.YukesPack.TexturePacks(pacs[0]));
            foreach (var p in packs)
            {
                var names = new List<string>();
                List<byte[]> mem;
                try { mem = PortIT.Engine.Readers.YukesPack.Members(p); }
                catch (Exception e) { Console.WriteLine($"  {Name(p)}: CANNOT UNPACK - {e.GetType().Name}: {e.Message}"); continue; }
                Console.WriteLine($"  {Name(p)}: {mem.Count} member(s): "
                    + string.Join(" ", mem.Select(m => $"{(m.Length >= 4 ? System.Text.Encoding.ASCII.GetString(m, 0, 4).Replace(' ', '.') : "??")}/{m.Length}")));
                if (Environment.GetEnvironmentVariable("PORTIT_DUMPMEM") is { } dd)
                {
                    Directory.CreateDirectory(dd);
                    for (int i = 0; i < mem.Count; i++)
                        File.WriteAllBytes(Path.Combine(dd, $"{Path.GetFileNameWithoutExtension(p)}_{i:00}.bin"), mem[i]);
                }
                foreach (var b in mem)
                    if (PortIT.Engine.Readers.YukesPack.TexturesOf(b) is { } t) names.AddRange(t.Keys);
                names.Sort(StringComparer.Ordinal);
                Console.WriteLine($"  {Name(p)} holds {names.Count} picture(s): {string.Join(", ", names)}");
            }
        }
        int packs_bad = 0, meshes_bad = 0, meshes = 0;
        var why = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pac in pacs)
        {
            List<TexRow> rows;
            try { rows = PackTextures(pac); }
            catch (Exception e) { Console.WriteLine($"{Name(pac)}: COULD NOT READ - {e.GetType().Name}: {e.Message}"); packs_bad++; continue; }
            meshes += rows.Count;
            var lost = rows.Where(r => r.Why.Length > 0 && r.Texture.Length > 0).ToList();
            var none = rows.Where(r => r.Texture.Length == 0).ToList();
            foreach (var r in lost) why[r.Why] = why.GetValueOrDefault(r.Why) + 1;
            meshes_bad += lost.Count;
            if (lost.Count == 0 && none.Count == 0) continue;
            packs_bad += lost.Count > 0 ? 1 : 0;
            Console.WriteLine($"{Name(pac)}: {rows.Count} meshes, {lost.Count} with a colour map that is NOT in the pack"
                              + (none.Count > 0 ? $", {none.Count} naming none" : ""));
            foreach (var r in lost.OrderByDescending(r => r.Points).Take(8))
                Console.WriteLine($"     {r.Mesh,-20} {r.Texture,-34} {r.Points,6} points   ({r.Why})");
            foreach (var r in none.OrderByDescending(r => r.Points).Take(4))
                Console.WriteLine($"     {r.Mesh,-20} {"(no colour map named)",-34} {r.Points,6} points");
            foreach (var r in rows.Where(r => r.Mesh.Contains("Hair", StringComparison.OrdinalIgnoreCase)
                                           || r.Mesh.Contains("Combinations", StringComparison.OrdinalIgnoreCase)
                                           || r.Mesh.Contains("Beard", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"     {r.Mesh,-20} vertex colour {(r.Colour.Length > 0 ? r.Colour : "none")}");
        }
        Console.WriteLine();
        Console.WriteLine($"{meshes} meshes in all, {meshes_bad} lose their picture, in {packs_bad} pack(s)");
        foreach (var kv in why.OrderByDescending(k => k.Value)) Console.WriteLine($"   {kv.Value,5}  {kv.Key}");
        return meshes_bad == 0 ? 0 : 1;
    }

    static string Name(string pac) =>
        Path.GetFileName(Path.GetDirectoryName(pac)) + "/" + Path.GetFileName(pac);

    // ---------------------------------------------------------------- a SvR character pac
    //
    // Every port is posed to a stock superstar's model - the TEMPLATE - and reads three things out of
    // it: the body (child 0), the hair (0x2710) and the eyelashes (0x4E20). SvrBuilder used to take the
    // pac's FIRST entry and nothing else, so a pac whose first entry is a port (one of PortIT's own
    // "replace a superstar" writes) threw a bare KeyNotFoundException on '20000' and every port after
    // it failed with no explanation (found 2026-11-17 on a test copy whose ch125 had been replaced).

    public static int ChPac(string path)
    {
        var arc = PortIT.Engine.Formats.Epk8.Read(File.ReadAllBytes(path));
        Console.WriteLine($"{Path.GetFileName(path)}  {new FileInfo(path).Length:N0} bytes, {arc.Groups.Count} group(s)");
        int usable = 0;
        foreach (var g in arc.Groups)
            foreach (var e in g.Entries)
            {
                string nm = e.NameText.Trim('\0', ' ');
                if (nm.Length == 0 || e.Data.Length == 0) continue;
                List<uint> kids;
                try { kids = PortIT.Engine.Formats.Pach.Read(PortIT.Engine.Formats.Bpe.Unpack(e.Data)).Select(k => k.Id).ToList(); }
                catch (Exception) { Console.WriteLine($"   {g.TypeText}/{nm,-10} {e.Data.Length,10:N0} bytes   (not a PACH)"); continue; }
                bool body = kids.Contains(0u), hair = kids.Contains(0x2710u), lash = kids.Contains(0x4E20u);
                if (body && lash) usable++;
                Console.WriteLine($"   {g.TypeText}/{nm,-10} {e.Data.Length,10:N0} bytes   {kids.Count,3} parts   "
                                  + $"body {(body ? "yes" : "NO ")}  hair {(hair ? "yes" : "no ")}  lashes {(lash ? "yes" : "NO ")}"
                                  + (body && lash ? "   <- can be a port template" : ""));
            }
        Console.WriteLine(usable > 0
            ? $"{usable} entr(ies) can serve as a port template"
            : "NO entry here can be a port template: it has no model with both a body and eyelashes");
        return usable > 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- many ports at once
    //
    // A folder of port runs, each with the request.json it was asked for and the response.json it gave
    // (what PortIT.exe --run writes). Built for "here is a folder of 2K19 models, I don't want to see any
    // problem" - one command that says which ones failed, which lost a picture, and which the engine
    // itself complained about.

    static readonly string[] Trouble =
    [
        "left out", "no texture", "no colour", "untextured", "could not", "cannot", "missing",
        "too big", "over the", "check ", "crashed", "white",
    ];

    public static int Batch(string folder)
    {
        var runs = Directory.EnumerateDirectories(folder).Where(d => File.Exists(Path.Combine(d, "response.json")))
                            .Order(StringComparer.Ordinal).ToList();
        if (runs.Count == 0) { Console.WriteLine($"no port runs in {folder}"); return 2; }
        int failed = 0, clean = 0;
        var errors = new Dictionary<string, int>(StringComparer.Ordinal);
        var notes = new Dictionary<string, int>(StringComparer.Ordinal);
        var worst = new List<(string Run, int Over)>();
        foreach (var run in runs)
        {
            JsonNode? res;
            try { res = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "response.json"))); }
            catch (Exception) { res = null; }
            string name = Path.GetFileName(run);
            if (res is null || res["ok"]?.GetValue<bool>() != true)
            {
                failed++;
                string err = res?["error"]?.GetValue<string>() ?? "no response";
                errors[Shorten(err)] = errors.GetValueOrDefault(Shorten(err)) + 1;
                Console.WriteLine($"FAILED  {name}: {err}");
                continue;
            }
            var bad = new List<string>();
            foreach (var line in (res["report"] as JsonArray ?? []).Select(x => x!.GetValue<string>()))
                if (Trouble.Any(t => line.Contains(t, StringComparison.OrdinalIgnoreCase)))
                { bad.Add(line); notes[Shorten(line)] = notes.GetValueOrDefault(Shorten(line)) + 1; }
            int face = res["checks"]?["face_over"]?.GetValue<int>() ?? 0;
            int gear = res["checks"]?["gear_over"]?.GetValue<int>() ?? 0;
            foreach (var n in (res["checks"]?["notes"] as JsonArray ?? []).Select(x => x!.GetValue<string>()))
            { bad.Add("check: " + n); notes[Shorten(n)] = notes.GetValueOrDefault(Shorten(n)) + 1; }
            if (face + gear > 0) worst.Add((name, face + gear));
            if (bad.Count == 0 && face + gear == 0) { clean++; continue; }
            Console.WriteLine($"{name}: {bad.Count} note(s)" + (face + gear > 0 ? $", face {face} / gear {gear} points outside" : ""));
            foreach (var b in bad.Take(6)) Console.WriteLine($"     {b}");
        }
        Console.WriteLine();
        Console.WriteLine($"{runs.Count} ports: {clean} with nothing to report, {runs.Count - clean - failed} with notes, {failed} failed");
        foreach (var kv in errors.OrderByDescending(k => k.Value)) Console.WriteLine($"   FAILED x{kv.Value,-4} {kv.Key}");
        foreach (var kv in notes.OrderByDescending(k => k.Value).Take(14)) Console.WriteLine($"   x{kv.Value,-5} {kv.Key}");
        foreach (var w in worst.OrderByDescending(w => w.Over).Take(5))
            Console.WriteLine($"   most points outside: {w.Run} ({w.Over})");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>a line with its numbers and names taken out, so the same trouble counts as one</summary>
    static string Shorten(string line)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(line, @"\d+", "N");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^M_\w+", "<piece>");
        return t.Length > 120 ? t[..120] : t;
    }
}
