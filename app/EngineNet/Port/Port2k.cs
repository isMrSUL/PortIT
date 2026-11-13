using System.Text.RegularExpressions;
using PortIT.Engine.Imaging;
using PortIT.Engine.Readers;
using PortIT.Engine.Util;

namespace PortIT.Engine.Port;

/// <summary>
/// k26/port_2k.py (with full_port.install's wrapped hair colours): a 2K wrestler's rock_model tables filled from its
/// own meshes (setup), the vertex targets, hair / beard cards, strand thinning (brock_model.thin_cards), the face paint
/// (portit/face_paint.py) and the normal maps' green test (check_green).
/// </summary>
public sealed class Port2k
{
    public static readonly (int X, int Y, int W, int H)[] SheetFree = [(1024, 512, 1024, 256), (1024, 768, 512, 256), (1536, 768, 256, 64), (1536, 960, 256, 64)];
    public static readonly string[] CardMats = ["hair", "combinations", "beard", "facial"];
    public static readonly string[] HeadGear = ["mask", "hood", "hat", "helmet", "bandana", "headband", "balaclava"];
    public const int FullBudget = 1500;
    public const double SolidShare = 0.90, HairLift = 0.75, StrandCut = 0.45, GreenTurnAt = 1.05;
    public readonly HashSet<string> SolidAlpha = [];
    public HashSet<string> LeaveOut = [];
    /// <summary>moving hair: bone chains for the hair that hangs, swung by the game (Build.HairChains)</summary>
    public bool MovingHair;
    public string? HairNote;
    /// <summary>alternate looks to use (an entrance outfit's M_Hair_Entrance...): read as normal meshes</summary>
    public HashSet<string> UseVariants = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public sealed class Info
    {
        public List<(string Name, int Verts)> Meshes = [];
        public Dictionary<string, (int X, int Y, int W, int H)> Cells = [];
        public Dictionary<string, (int R, int G, int B)> Cards = [];
        public List<string> Cutout = [];
        public List<RockFbx.Mesh> RawMeshes = [];
        public Dictionary<string, List<RockFbx.Mesh>> TNames = [];
    }

    /// <summary>a wrestler from a WWE 2K19 - 2K22 pack (its base model is a Yuke's pointer file)</summary>
    public static bool YukesPack(string chr)
    {
        try
        {
            using var f = File.OpenRead(RockFbx.BaseMcd(chr));
            var b = new byte[4];
            return f.Read(b, 0, 4) == 4 && b.SequenceEqual("PJBY"u8.ToArray());
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool IsCard(RockFbx.Mesh m)
    {
        string mat = m.Material.ToLowerInvariant();
        if (CardMats.Any(c => mat.StartsWith(c))) return true;
        if (RaiMats.Contains(mat.Split(':')[0])) return true;
        return mat.EndsWith("_alpha") && !SolidAlpha.Contains(mat);
    }

    /// <summary>materials with a hair strand map of their own (&lt;mat&gt;_rai): hair cards whatever their name - Bray Wyatt's
    /// dreads in JOEMASHUPS' pack are "h_acce" with a black placeholder colour map; as gear they came out black, 2026-11-13</summary>
    public HashSet<string> RaiMats = new(StringComparer.OrdinalIgnoreCase);

    public void SolidAlphaGear(List<RockFbx.Mesh> meshes, string probe, Action<string> log)
    {
        SolidAlpha.Clear();
        foreach (var m in meshes)
        {
            string mat = m.Material.ToLowerInvariant();
            if (!mat.EndsWith("_alpha") || CardMats.Any(c => mat.StartsWith(c)) || string.IsNullOrEmpty(m.Texture)) continue;
            Img a;
            try { a = ImageIO.Load(Path.Combine(probe, m.Texture)).ToRgba(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
            int n = a.W * a.H, solid = 0;
            for (int i = 0; i < n; i++) if (a.Px[4 * i + 3] >= 128) solid++;
            double share = (double)solid / n;
            if (share >= SolidShare)
            {
                SolidAlpha.Add(mat);
                log($"{m.Name}: its map is {(100 * share).ToString("F0", Inv)}% solid - drawn as solid gear (the skin under it hidden), not as a see-through card");
            }
        }
    }

    public static string AttireDir(string chr, string attire)
    {
        string p = Path.Combine(chr, attire);
        return File.Exists(p) ? Path.GetRelativePath(chr, Path.GetDirectoryName(p)!) : attire;
    }

    /// <summary>2K hair has no colour map: the median of its per-vertex colour, made sRGB x HAIR_LIFT (full_port.install)</summary>
    /// <summary>an AEW wrestler (aew_bridge): it stands in for rock_fbx.build - set by Build</summary>
    public AewBridge? Bridge;

    (int R, int G, int B) HairRgbOf(string chr, string attire, string meshName) => HairRgb(chr, attire, meshName, Bridge);

    public static (int R, int G, int B) HairRgb(string chr, string attire, string meshName, AewBridge? bridge = null)
    {
        var lin = (90, 70, 50);
        if (bridge != null) lin = bridge.HairRgb();
        else
        {
        var m = Mcd.Load(RockFbx.AttireMcd(chr, attire));
        for (int k = 0; k < m.Meshes.Count; k++)
        {
            if (m.Meshes[k].Material != meshName) continue;
            var c = m.StreamOf(k, "COLOR");
            if (c != null && c.N > 0)
            {
                var v = new int[3];
                var hi = new int[3];
                for (int ch = 0; ch < 3; ch++)
                {
                    var col = Enumerable.Range(0, c.N).Select(i => c.V[c.C * i + ch]).Order().ToArray();
                    int n = col.Length;
                    double med = n % 2 == 1 ? col[n / 2] : (col[n / 2 - 1] + col[n / 2]) / 2;
                    v[ch] = (int)med;
                    hi[ch] = (int)col[n * 3 / 4];
                }
                // (blond dreads with dark roots / insides: most points near black, the strands that show light - Bray Wyatt's
                //  Promo dreads, JOEMASHUPS: median (11, 9, 4), 75th percentile (98, 70, 31) - they came out black, 2026-11-13.
                //  Dark hair keeps its median: a beard's 75th percentile stays dark)
                int lum(int[] x) => (2 * x[0] + 5 * x[1] + x[2]) / 8;
                if (lum(v) < 24 && lum(hi) > 3 * Math.Max(lum(v), 8)) v = hi;
                lin = (v[0], v[1], v[2]);
                break;
            }
        }
        }
        int S(int x)
        {
            double c = x / 255.0;
            double s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            return (int)Math.Min(255, Math.Round(255 * s * HairLift, MidpointRounding.ToEven));
        }
        return (S(lin.Item1), S(lin.Item2), S(lin.Item3));
    }

    /// <summary>full_port.install's strand_colour: flat colour shaded by the rai map's red / blue, green over STRAND_CUT opaque</summary>
    public static void StrandColour(string src, string dst, (int R, int G, int B) rgb)
    {
        var a = ImageIO.Load(src).ToRgba();
        var o = new Img(a.W, a.H, 4);
        int[] c = [rgb.R, rgb.G, rgb.B];
        for (int i = 0; i < a.W * a.H; i++)
        {
            double r = a.Px[4 * i] / 255.0, g = a.Px[4 * i + 1] / 255.0, b = a.Px[4 * i + 2] / 255.0;
            double shade = 0.55 + 0.75 * r + 0.25 * b;
            for (int k = 0; k < 3; k++) o.Px[4 * i + k] = (byte)Math.Clamp(c[k] * shade, 0, 255);
            o.Px[4 * i + 3] = (byte)(g > StrandCut ? 255 : 0);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        ImageIO.SavePng(o, dst);
    }

    public static int StrandPieces(RockFbx.Mesh m)
    {
        var parent = Enumerable.Range(0, m.N).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        for (int t = 0; t < m.Tris.Length; t += 3)
        {
            int ra = Find(m.Tris[t]);
            parent[Find(m.Tris[t + 1])] = ra;
            parent[Find(m.Tris[t + 2])] = ra;
        }
        return m.Tris.Distinct().Select(Find).Distinct().Count();
    }

    /// <summary>brock_model.thin_cards: whole cards (connected triangle groups) kept, evenly (default_rng(7) order),
    /// until about target vertices -&gt; (cards, cards kept)</summary>
    public static (int Cards, int Kept) ThinCards(RockFbx.Mesh m, int target)
    {
        var tris = m.Tris; int n = m.N, nt = tris.Length / 3;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        for (int t = 0; t < nt; t++)
        {
            int ra = Find(tris[3 * t]), rb = Find(tris[3 * t + 1]), rc = Find(tris[3 * t + 2]);
            parent[rb] = ra;
            parent[Find(rc)] = ra;
        }
        var root = Enumerable.Range(0, n).Select(Find).ToArray();
        var cards = new Dictionary<int, List<int>>();
        var ids = new List<int>();
        for (int t = 0; t < nt; t++)
        {
            int r = root[tris[3 * t]];
            if (!cards.TryGetValue(r, out var l)) { cards[r] = l = []; ids.Add(r); }
            l.Add(t);
        }
        NpRandom.Seed7().Shuffle(ids);
        // (the biggest cards first: they cover the head; a random pick of 1 in 5 left the scalp bare - Marina Shafir,
        //  2K25, cut to the classic window, GitHub issue 1, 2026-11-12)
        var area = new Dictionary<int, double>();
        foreach (var (r, l) in cards)
        {
            double s = 0;
            foreach (int t in l)
            {
                int a = tris[3 * t], b = tris[3 * t + 1], c = tris[3 * t + 2];
                double[] u = [m.Verts[3 * b] - m.Verts[3 * a], m.Verts[3 * b + 1] - m.Verts[3 * a + 1], m.Verts[3 * b + 2] - m.Verts[3 * a + 2]];
                double[] w = [m.Verts[3 * c] - m.Verts[3 * a], m.Verts[3 * c + 1] - m.Verts[3 * a + 1], m.Verts[3 * c + 2] - m.Verts[3 * a + 2]];
                double x = u[1] * w[2] - u[2] * w[1], y = u[2] * w[0] - u[0] * w[2], z = u[0] * w[1] - u[1] * w[0];
                s += Math.Sqrt(x * x + y * y + z * z) / 2;
            }
            area[r] = s;
        }
        // spread over the head: each next card the one with the most (area x distance to the cards already kept) - the
        // biggest-first order kept the long braids and left bald patches on top (2026-11-12)
        var cen = new Dictionary<int, double[]>();
        foreach (var (r, l) in cards)
        {
            var c3 = new double[3]; int cnt = 0;
            foreach (int t in l) for (int q3 = 0; q3 < 3; q3++) { int v = tris[3 * t + q3]; c3[0] += m.Verts[3 * v]; c3[1] += m.Verts[3 * v + 1]; c3[2] += m.Verts[3 * v + 2]; cnt++; }
            for (int c = 0; c < 3; c++) c3[c] /= Math.Max(cnt, 1);
            cen[r] = c3;
        }
        {
            var order = new List<int>(ids.Count);
            var left = ids.OrderByDescending(c => area[c]).ToList();   // (stable: equal areas keep the shuffled order)
            var near = left.ToDictionary(c => c, _ => double.PositiveInfinity);
            while (left.Count > 0)
            {
                int bi = 0; double best = double.NegativeInfinity;
                for (int i = 0; i < left.Count; i++)
                {
                    double d = near[left[i]];
                    double sc = double.IsPositiveInfinity(d) ? area[left[i]] : Math.Sqrt(area[left[i]]) * d;
                    if (sc > best) { best = sc; bi = i; }
                }
                int pick = left[bi]; left.RemoveAt(bi); order.Add(pick);
                var p = cen[pick];
                foreach (int c in left)
                {
                    var q = cen[c];
                    double d = Math.Sqrt((p[0] - q[0]) * (p[0] - q[0]) + (p[1] - q[1]) * (p[1] - q[1]) + (p[2] - q[2]) * (p[2] - q[2]));
                    if (d < near[c]) near[c] = d;
                }
            }
            ids = order;
        }
        var keepT = new List<int>(); int nv = 0;
        foreach (int c in ids)
        {
            int vs = cards[c].SelectMany(t => new[] { tris[3 * t], tris[3 * t + 1], tris[3 * t + 2] }).Distinct().Count();
            if (nv + vs > target && keepT.Count > 0) continue;
            keepT.AddRange(cards[c]);
            nv += vs;
        }
        keepT.Sort();
        var used = new bool[n];
        foreach (int t in keepT) { used[tris[3 * t]] = used[tris[3 * t + 1]] = used[tris[3 * t + 2]] = true; }
        var remap = new int[n]; int k = 0;
        for (int i = 0; i < n; i++) remap[i] = used[i] ? k++ : -1;
        var idx = Enumerable.Range(0, n).Where(i => used[i]).ToArray();
        m.Tris = keepT.SelectMany(t => new[] { remap[tris[3 * t]], remap[tris[3 * t + 1]], remap[tris[3 * t + 2]] }).ToArray();
        m.Verts = Take(m.Verts, 3, idx); m.Normals = Take(m.Normals, 3, idx); m.Uv = Take(m.Uv, 2, idx);
        m.Weights = m.Weights.Select(e => (e.Bone, idx.Select(i => e.W[i]).ToArray())).ToList();
        return (cards.Count, keepT.Select(t => root[tris[3 * t]]).Distinct().Count());
    }

    static double[] Take(double[] a, int k, int[] idx)
    {
        var o = new double[idx.Length * k];
        for (int i = 0; i < idx.Length; i++) for (int c = 0; c < k; c++) o[k * i + c] = a[k * idx[i] + c];
        return o;
    }

    // ---------------------------------------------------------------- setup
    /// <summary>fill the RockModel's tables for this wrestler (the probe read: names / sizes / textures only)</summary>
    public Info Setup(RockModel R, string chr, string attire, string work, bool full, Action<string> print)
    {
        string probe = Path.Combine(work, "_probe");
        var rf = new RockFbx { DropMeshes = new HashSet<string>(["M_Teeth", "M_Tongue"]).Union(LeaveOut).ToHashSet(), QuickRead = true };
        rf.UseVariants.UnionWith(UseVariants);
        var (meshes, _) = Bridge != null ? Bridge.Build(rf) : rf.Build(chr, attire, probe);
        foreach (var l in rf.Report) print(l);
        SolidAlphaGear(meshes, probe, print);
        R.Fbx.NotGear = new HashSet<string>(["M_Body", "M_Head", "M_EYE", "M_Head_Caruncle", "M_TransB_eyelash", "M_transB_eyelash", "M_Teeth", "M_Tongue", "M_MouthBag"])
            .Union(meshes.Where(IsCard).Select(m => m.Name)).ToHashSet();
        R.DropMeshes = new HashSet<string>(["M_Teeth", "M_Tongue"]).Union(LeaveOut)
            .Union(meshes.Where(m => m.Name.ToLowerInvariant().StartsWith("lambert") || m.Name.ToLowerInvariant().StartsWith("polysurface")
                                     || m.Name.ToLowerInvariant().Contains("eyeshadow") || m.Name == "S_Hair").Select(m => m.Name)).ToHashSet();
        // (eyelashes with no map of their own - 2K draws its default lash texture, SvR has none: Enzo Amore's lashes came out
        //  as white rims round his eyes in game, 2026-11-13; the head map has its lashes painted)
        foreach (var m in meshes.Where(m => m.Name.StartsWith("M_transB_eyelash", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(m.Texture)))
        {
            R.DropMeshes.Add(m.Name);
            print($"{m.Name}: no eyelash map in the mod (it drew white) - left out");
        }
        var bag = meshes.FirstOrDefault(m => m.Name == "M_MouthBag");
        if (bag != null)
        {
            var c0 = Mean0(bag.Verts);
            foreach (var m in meshes)
            {
                if (ReferenceEquals(m, bag) || !string.IsNullOrEmpty(m.Texture) || R.DropMeshes.Contains(m.Name) || m.N > 8000 || IsCard(m)
                    || m.Name is "M_Head" or "M_Body" or "M_EYE") continue;   // (the head itself, untextured by name, was dropped - Charlotte 2K23)
                var c = Mean0(m.Verts);
                if (Blas.Norm(new[] { c[0] - c0[0], c[1] - c0[1], c[2] - c0[2] }) < 4.0)
                {
                    R.DropMeshes.Add(m.Name);
                    R.Fbx.NotGear.Add(m.Name);
                    print($"{m.Name}: an untextured mesh in the mouth (2K tongue / teeth) - left out");
                }
            }
        }
        RaiMats.Clear();
        foreach (var m in meshes)
        {
            string b = m.Material.Split(':')[0];
            if (m.Name.StartsWith("M_Head", StringComparison.OrdinalIgnoreCase) || m.Name is "M_Body" or "M_EYE") continue;
            if (!CardMats.Any(c => b.ToLowerInvariant().StartsWith(c)) && R.FindTex(chr, AttireDir(chr, attire), b, "_rai") != null && RaiMats.Add(b))
                print($"{m.Name}: material {b} has a hair strand map ({b}_rai) - ported as hair");
        }
        R.Kind = [];
        foreach (var m in meshes)
        {
            string n = m.Name.ToLowerInvariant();
            if (n == "m_eye") R.Kind[m.Name] = "eye";
            else if (n.Contains("eyelash")) R.Kind[m.Name] = "lash";
        }
        var tnames = new Dictionary<string, List<RockFbx.Mesh>>();
        var torder = new List<string>();
        foreach (var m in meshes)
        {
            if (R.DropMeshes.Contains(m.Name)) continue;
            string bas = m.Material.Split(':')[0];
            string t = bas is "eye_l" or "eye_r" ? "eye" : bas;
            if (!tnames.TryGetValue(t, out var l)) { tnames[t] = l = []; torder.Add(t); }
            l.Add(m);
        }
        var mouthbag = R.Cells["mouthbag"];
        var baseCells = new Dictionary<string, (int X, int Y, int W, int H)>
        {
            ["head"] = (0, 0, 1024, 1024), ["body"] = (1024, 0, 1024, 512), ["eye"] = (1792, 768, 256, 128), ["eyelash"] = (1792, 896, 256, 128), ["mouthbag"] = mouthbag,
        };
        int Verts(string t) => tnames[t].Sum(m => m.N);
        var rest = torder.Where(t => !baseCells.ContainsKey(t)).OrderBy(t => -Verts(t)).ToList();
        Dictionary<string, (int, int, int, int)>? cells = null;
        string? shortOf = null;
        for (int shrink = 0; shrink < 4; shrink++)
        {
            cells = new Dictionary<string, (int, int, int, int)>(baseCells.Select(kv => KeyValuePair.Create(kv.Key, ((int, int, int, int))kv.Value)));
            var free = SheetFree.ToList();
            shortOf = null;
            foreach (var t in rest)
            {
                int size = (Verts(t) > 1500 || IsCard(tnames[t][0]) ? 256 : 128) >> shrink;
                size = Math.Max(32, size);
                int? fit;
                while (true)
                {
                    fit = null;
                    for (int k = 0; k < free.Count; k++) if (free[k].W >= size && free[k].H >= Math.Min(size, free[k].H)) { fit = k; break; }
                    if (fit != null || size <= 32) break;
                    size /= 2;
                }
                if (fit == null)
                {
                    if (full) { cells[t] = (0, 0, 32, 32); continue; }
                    shortOf = t;
                    break;
                }
                var (x, y, w, h) = free[fit.Value];
                free.RemoveAt(fit.Value);
                int ch = Math.Min(size, h);
                cells[t] = (x, y, size, ch);
                if (w - size > 0) free.Insert(fit.Value, (x + size, y, w - size, h));
                if (h - ch > 0) free.Add((x, y + ch, size, h - ch));
            }
            if (shortOf == null)
            {
                if (shrink > 0) print($"texture sheet: {rest.Count} gear parts - laid out at 1/{1 << shrink} size to fit");
                break;
            }
        }
        if (shortOf != null) throw new InvalidOperationException("no room in the texture sheet for " + shortOf);
        R.Cells = cells!.ToDictionary(kv => kv.Key, kv => ((int, int, int, int))kv.Value);
        R.Cutout = tnames.Where(kv => IsCard(kv.Value[0])).Select(kv => kv.Key).ToHashSet();
        R.Shine = new() { ["head"] = (10, 1 / 16.0), ["body"] = (10, 1 / 16.0), ["eye"] = (52, 0), ["eyelash"] = (0, 0), ["eye_caruncle"] = (40, 0) };
        foreach (var t in torder)
            if (!R.Shine.ContainsKey(t))
                R.Shine[t] = R.Cutout.Contains(t) ? (20, 0) : t.Contains("shoe") || t.Contains("boot") ? (80, 1 / 3.0) : (40, 1 / 3.0);
        R.Solid = new(R.Solid) { ["eye_caruncle"] = (176, 104, 98) };
        var cardsRgb = new Dictionary<string, (int, int, int)>();
        // (the strand colours are written to the work folder and looked up first: they were written next to the rai map -
        //  into the user's own mod folder - and a mod's own placeholder <t>_color.dds was found before them: Bray Wyatt's
        //  dreads (JOEMASHUPS, a black h_acce_color.dds) stayed black, 2026-11-13)
        string strands = Path.Combine(work, "_strands");
        if (Directory.Exists(strands)) foreach (var old in Directory.EnumerateFiles(strands)) File.Delete(old);
        Directory.CreateDirectory(strands);
        if (!R.TexFirst.Contains(strands)) R.TexFirst.Insert(0, strands);
        foreach (var t in R.Cutout.OrderBy(t => torder.IndexOf(t)))
        {
            var ms = tnames[t];
            string ad = AttireDir(chr, attire);
            var rai = R.FindTex(chr, ad, Regex.Replace(t, "(?i)_alpha$", ""), "_rai") ?? R.FindTex(chr, ad, t, "_rai");
            var rgb = HairRgbOf(chr, attire, ms[0].Name);
            if (rai != null) StrandColour(rai, Path.Combine(strands, t + "_color.png"), rgb);
            else if (string.IsNullOrEmpty(ms[0].Texture)) R.Solid = new(R.Solid) { [t] = rgb };
            cardsRgb[t] = rgb;
        }
        return new Info
        {
            Meshes = DictOrder(meshes.Select(m => (m.Name, m.N))), Cells = R.Cells, Cards = cardsRgb,
            Cutout = R.Cutout.Order(StringComparer.Ordinal).ToList(), RawMeshes = meshes, TNames = tnames,
        };
    }

    /// <summary>a Python dict built from pairs: a repeated name keeps its first place and its last value</summary>
    static List<(string Name, int Verts)> DictOrder(IEnumerable<(string, int)> pairs)
    {
        var o = new List<(string Name, int Verts)>();
        foreach (var (n, v) in pairs) { int at = o.FindIndex(x => x.Name == n); if (at < 0) o.Add((n, v)); else o[at] = (n, v); }
        return o;
    }

    static double[] Mean0(double[] P)
    {
        int n = P.Length / 3; var m = new double[3];
        for (int i = 0; i < n; i++) for (int c = 0; c < 3; c++) m[c] += P[3 * i + c];
        for (int c = 0; c < 3; c++) m[c] /= n;
        return m;
    }

    /// <summary>hair-card vertices in the classic window (1300 left Marina Shafir's braids at 1 in 8 cards)</summary>
    public const int HairRoom = 2200;
    /// <summary>the fewest points a gear piece keeps in the classic window (or all it has, when fewer)</summary>
    public const int GearFloor = 700;

    /// <summary>vertices kept per mesh (Brock's tested numbers for the 192-page window, x scale)</summary>
    public static Dictionary<string, int> Targets(Info info, double scale)
    {
        var T = new Dictionary<string, int>();
        long gearTotal = 0;
        foreach (var (n, v) in info.Meshes)
        {
            string low = n.ToLowerInvariant();
            if (low == "m_body") T[n] = 3000;
            else if (low == "m_head") T[n] = 2700;
            else if (low == "m_eye" || low.Contains("eyelash") || low is "m_teeth" or "m_tongue") continue;
            else if (low == "m_mouthbag") T[n] = 220;
            else if (low.Contains("caruncle")) T[n] = 160;
            else gearTotal += v;
        }
        const int gearRoom = 3000;
        foreach (var (n, v) in info.Meshes)
            if (!T.ContainsKey(n) && n.ToLowerInvariant() != "m_eye" && !n.ToLowerInvariant().Contains("eyelash"))
            {
                T[n] = Math.Max(Math.Min(v, GearFloor), (int)(gearRoom * (double)v / Math.Max(gearTotal, 1)));   // (a small top kept its shape: 2203 -> 325 let the skin through, GitHub issue 1)
                if (HeadGear.Any(k => n.ToLowerInvariant().Contains(k))) T[n] = Math.Max(T[n], Math.Min(v, 1100));
            }
        return T.ToDictionary(kv => kv.Key, kv => Math.Max(150, (int)(kv.Value * scale)));
    }

    /// <summary>hair cards that lie flat on the skin (left out of the model) painted into the head / body colour map
    /// under them: each card point takes the skin UV of its nearest skin triangle, the card's own colour + alpha drawn
    /// there. Marina Shafir (2K25): M_Hair2 is the dark braid base - left out unpainted, the light scalp of the head map
    /// showed (GitHub issue 1, 2026-11-12). Read from the wrestler's own maps (colorFile: RockModel.FindTex), written to <work>/_painted each build: the shared work
    /// folder keeps old wrestlers' textures (Big E got an earlier port's face, 2026-11-12).</summary>
    static void BakeFlatCards(List<RockFbx.Mesh> meshes, List<(RockFbx.Mesh M, double MaxCm)> paint, string outDir, Action<string> log, Dictionary<string, string> overrides, Func<string, string?> colorFile, bool headOnly = false)
    {
        static string Base(RockFbx.Mesh m) => m.Material.Split(':')[0];
        var skins = meshes.Where(m => (m.Name == "M_Head" || m.Name == "M_Body" && !headOnly) && m.Uv.Length / 2 == m.N && colorFile(Base(m)) != null).ToList();
        if (skins.Count == 0) return;
        // (all skin points in one list -> mesh / point; per point the triangles that use it)
        var P = skins.SelectMany(m => m.Verts).ToArray();
        var owner = new List<(int M, int V)>();
        for (int si = 0; si < skins.Count; si++) for (int v = 0; v < skins[si].N; v++) owner.Add((si, v));
        var around = skins.Select(m =>
        {
            var l = new List<int>[m.N];
            for (int v = 0; v < m.N; v++) l[v] = [];
            for (int t = 0; t < m.Tris.Length / 3; t++) for (int k = 0; k < 3; k++) l[m.Tris[3 * t + k]].Add(t);
            return l;
        }).ToList();
        var maps = new Dictionary<int, Img>();
        Img MapOf(int si)
        {
            if (maps.TryGetValue(si, out var im)) return im;
            return maps[si] = ImageIO.Load(colorFile(Base(skins[si]))!).ToRgba();
        }
        foreach (var (hm, maxCm) in paint)
        {
            if (hm.Uv.Length / 2 != hm.N) { log($"{hm.Name}: no texture coordinates - not painted"); continue; }
            Img card;
            if (colorFile(Base(hm)) is { } cf) card = ImageIO.Load(cf).ToRgba();
            else
            {
                // (no colour map, no alpha: a one-colour paint made blobs - Adam Cole '20's layer holds his beard too)
                log($"{hm.Name}: no card colour map - not painted");
                continue;
            }
            var (J, D2) = FullPort.Nearest(hm.Verts, P);
            // each card point -> (skin mesh, its UV there)
            var sm = new int[hm.N]; var su = new double[2 * hm.N];
            for (int i = 0; i < hm.N; i++)
            {
                var (si, v0) = owner[J[i]];
                var m = skins[si];
                double[] p = [hm.Verts[3 * i], hm.Verts[3 * i + 1], hm.Verts[3 * i + 2]];
                double best = double.MaxValue, bu = m.Uv[2 * v0], bv = m.Uv[2 * v0 + 1];
                foreach (int t in around[si][v0])
                {
                    int a = m.Tris[3 * t], b = m.Tris[3 * t + 1], c = m.Tris[3 * t + 2];
                    var (w0, w1, w2, d2) = ClosestOnTri(p, m.Verts, a, b, c);
                    if (d2 < best)
                    {
                        best = d2;
                        bu = w0 * m.Uv[2 * a] + w1 * m.Uv[2 * b] + w2 * m.Uv[2 * c];
                        bv = w0 * m.Uv[2 * a + 1] + w1 * m.Uv[2 * b + 1] + w2 * m.Uv[2 * c + 1];
                    }
                }
                sm[i] = si; su[2 * i] = bu; su[2 * i + 1] = bv;
            }
            int painted = 0;
            for (int t = 0; t < hm.Tris.Length / 3; t++)
            {
                int a = hm.Tris[3 * t], b = hm.Tris[3 * t + 1], c = hm.Tris[3 * t + 2];
                if (sm[a] != sm[b] || sm[a] != sm[c]) continue;
                if (Math.Sqrt(Math.Max(D2[a], Math.Max(D2[b], D2[c]))) > maxCm) continue;   // (only cards lying on the skin)
                var dst = MapOf(sm[a]);
                double[] xs = new double[3], ys = new double[3];
                int[] vi = [a, b, c];
                for (int k = 0; k < 3; k++) { xs[k] = Frac(su[2 * vi[k]]) * dst.W; ys[k] = (1 - Frac(su[2 * vi[k] + 1])) * dst.H; }   // (the 2K UVs: v up, image rows down)
                // (a triangle across a UV seam of the skin: its corners land far apart - skipped)
                if (xs.Max() - xs.Min() > dst.W * 0.08 || ys.Max() - ys.Min() > dst.H * 0.08) continue;
                double d = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0]);
                if (Math.Abs(d) < 1e-9) continue;
                int x0 = Math.Max(0, (int)Math.Floor(xs.Min())), x1 = Math.Min(dst.W - 1, (int)Math.Ceiling(xs.Max()));
                int y0 = Math.Max(0, (int)Math.Floor(ys.Min())), y1 = Math.Min(dst.H - 1, (int)Math.Ceiling(ys.Max()));
                for (int py = y0; py <= y1; py++)
                    for (int px = x0; px <= x1; px++)
                    {
                        double gx = px + 0.5, gy = py + 0.5;
                        double b1 = ((gx - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (gy - ys[0])) / d;
                        double b2 = ((xs[1] - xs[0]) * (gy - ys[0]) - (gx - xs[0]) * (ys[1] - ys[0])) / d;
                        double b0 = 1 - b1 - b2;
                        if (b0 < -1e-6 || b1 < -1e-6 || b2 < -1e-6) continue;
                        double u = b0 * hm.Uv[2 * a] + b1 * hm.Uv[2 * b] + b2 * hm.Uv[2 * c];
                        double v = b0 * hm.Uv[2 * a + 1] + b1 * hm.Uv[2 * b + 1] + b2 * hm.Uv[2 * c + 1];
                        int cx = Math.Clamp((int)(Frac(u) * card.W), 0, card.W - 1), cy = Math.Clamp((int)((1 - Frac(v)) * card.H), 0, card.H - 1);
                        int ci = 4 * (cy * card.W + cx), di = 4 * (py * dst.W + px);
                        double al = card.Px[ci + 3] / 255.0;
                        if (al <= 0) continue;
                        for (int k = 0; k < 3; k++) dst.Px[di + k] = (byte)Math.Round(dst.Px[di + k] * (1 - al) + card.Px[ci + k] * al);
                        painted++;
                    }
            }
            log($"{hm.Name}: {(double.IsPositiveInfinity(maxCm) ? "flat hair cards" : "scalp cards (all, before thinning)")} painted into the skin colour map ({painted} pixels)");
        }
        foreach (var (si, im) in maps)
        {
            string path = Path.Combine(outDir, "_painted", Base(skins[si]) + "_color.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ImageIO.SavePng(im, path);
            overrides[Base(skins[si]) + "_color"] = path;
        }
    }

    static double Frac(double x) => x - Math.Floor(x);

    /// <summary>hair-card triangles this close to the skin (all 3 corners) are painted under a thinned hair mesh</summary>
    public const double ScalpPaintCm = 1.5;

    /// <summary>full detail: one gear piece over this many points is cut to it (a mod's 270,000-point pants)</summary>
    public const int FullGearMax = 20000;
    /// <summary>... only for a piece over this many (a normal heavy top is 25,000: left as it is)</summary>
    public const int FullGearLimit = 60000;

    /// <summary>the cards of a kept hair / beard mesh this close to the skin are painted under it as a base</summary>
    public const double CardBaseCm = 0.8;

    static RockFbx.Mesh Clone(RockFbx.Mesh m) => new() { Name = m.Name, Material = m.Material, Texture = m.Texture, Verts = m.Verts, Normals = m.Normals, Uv = m.Uv, Tris = m.Tris };

    /// <summary>the closest point of triangle a b c (of the flat point list V) to p: its barycentric weights and squared distance</summary>
    static (double, double, double, double) ClosestOnTri(double[] p, double[] V, int a, int b, int c)
    {
        double[] A = [V[3 * a], V[3 * a + 1], V[3 * a + 2]], B = [V[3 * b], V[3 * b + 1], V[3 * b + 2]], C = [V[3 * c], V[3 * c + 1], V[3 * c + 2]];
        double[] ab = [B[0] - A[0], B[1] - A[1], B[2] - A[2]], ac = [C[0] - A[0], C[1] - A[1], C[2] - A[2]], ap = [p[0] - A[0], p[1] - A[1], p[2] - A[2]];
        static double Dot(double[] x, double[] y) => x[0] * y[0] + x[1] * y[1] + x[2] * y[2];
        double d00 = Dot(ab, ab), d01 = Dot(ab, ac), d11 = Dot(ac, ac), d20 = Dot(ap, ab), d21 = Dot(ap, ac);
        double den = d00 * d11 - d01 * d01;
        double w1 = 0, w2 = 0;
        if (Math.Abs(den) > 1e-12) { w1 = (d11 * d20 - d01 * d21) / den; w2 = (d00 * d21 - d01 * d20) / den; }
        w1 = Math.Clamp(w1, 0, 1); w2 = Math.Clamp(w2, 0, 1);
        if (w1 + w2 > 1) { double s = w1 + w2; w1 /= s; w2 /= s; }
        double w0 = 1 - w1 - w2;
        double[] q = [w0 * A[0] + w1 * B[0] + w2 * C[0], w0 * A[1] + w1 * B[1] + w2 * C[1], w0 * A[2] + w1 * B[2] + w2 * C[2]];
        double dx = q[0] - p[0], dy = q[1] - p[1], dz = q[2] - p[2];
        return (w0, w1, w2, dx * dx + dy * dy + dz * dz);
    }

    public static List<string> CardMeshes(Info info)
    {
        var cut = info.Cutout;
        return info.Meshes.Select(m => m.Name).Where(n =>
        {
            string k = n.ToLowerInvariant().Replace("m_", "");
            return cut.Any(t => t.ToLowerInvariant().Contains(k) || t.ToLowerInvariant().StartsWith(k));
        }).ToList();
    }

    // ---------------------------------------------------------------- face paint (portit/face_paint.py)
    public static string? VariantDir(string chr, string attireDir)
    {
        string bas = Path.Combine(chr, "BaseModel");
        if (!Directory.Exists(bas)) return null;
        string name = Path.GetFileName(attireDir.TrimEnd('\\', '/')).ToLowerInvariant();
        string look = Regex.Replace(name, @"^\d+_", "");
        var variants = Directory.EnumerateFileSystemEntries(bas).Select(p => Path.GetFileName(p)!)
            .Where(d => d.ToLowerInvariant().StartsWith("textures_") && Directory.Exists(Path.Combine(bas, d))).ToList();
        string Tail(string d) => d["textures_".Length..].ToLowerInvariant();
        var pick = variants.FirstOrDefault(d => Tail(d) == name || Tail(d) == look)
                   ?? variants.FirstOrDefault(d => look.Length > 0 && (look.Contains(Tail(d)) || Tail(d).Contains(look)));
        return pick != null ? Path.Combine(bas, pick) : null;
    }

    public static string? SiblingTextures(string chr, string attireDir)
    {
        if (Directory.Exists(Path.Combine(chr, attireDir, "Textures"))) return null;
        var sib = Directory.EnumerateFileSystemEntries(chr).Select(p => Path.GetFileName(p)!)
            .Where(d => d != "BaseModel" && Directory.Exists(Path.Combine(chr, d, "Textures")))
            .OrderBy(d => !d.ToLowerInvariant().EndsWith("default_attire")).ThenBy(d => d, StringComparer.Ordinal).ToList();
        return sib.Count > 0 ? Path.Combine(chr, sib[0], "Textures") : null;
    }

    static string? First(IEnumerable<string?> dirs, string name)
    {
        foreach (var d in dirs)
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFileSystemEntries(d))
                if (Path.GetFileName(f).ToLowerInvariant().Split('.')[0] == name) return f;
        }
        return null;
    }

    /// <summary>-&gt; the folders to look in before the attire's / base model's: [painted head, Textures_&lt;look&gt;, a sibling
    /// attire's Textures]; the painted head: out\_facepaint\Textures\head_color.png (+ head_nrm.png)</summary>
    public static List<string> FacePaint(string chr, string attireDir, string outDir, Action<string> log, bool normals = true)
    {
        var first = new List<string>();
        var vr = VariantDir(chr, attireDir);
        if (vr != null) { first.Add(vr); log($"textures: this attire uses the base model's {Path.GetFileName(vr)}"); }
        var sib = SiblingTextures(chr, attireDir);
        if (sib != null) { first.Add(sib); log($"textures: {attireDir} has none of its own - it wears {Path.GetFileName(Path.GetDirectoryName(sib))}'s"); }
        string?[] dirs = [vr, Path.Combine(chr, "BaseModel", "Textures")];
        string? paint = First(dirs, "facepaint_color"), head = First(dirs, "head_color");
        if (paint == null || head == null) return first;
        string dst = Path.Combine(outDir, "_facepaint", "Textures");
        Directory.CreateDirectory(dst);
        var pi = ImageIO.Load(paint).ToRgba();
        var hi = ImageIO.Load(head).ToRgba();
        if (pi.W != hi.W || pi.H != hi.H) pi = Resample.ResizeRgbaPremultiplied(pi, hi.W, hi.H, Filter.Lanczos);
        int n = hi.W * hi.H;
        var alpha = new double[n];
        for (int i = 0; i < n; i++) alpha[i] = pi.Px[4 * i + 3] / 255.0;
        var c = hi.Copy();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < 3; k++)
                c.Px[4 * i + k] = (byte)Math.Clamp(hi.Px[4 * i + k] * (1 - alpha[i]) + pi.Px[4 * i + k] * alpha[i], 0, 255);
        ImageIO.SavePng(c, Path.Combine(dst, "head_color.png"));
        string? pn = First(dirs, "facepaint_nrm"), hn = First(dirs, "head_nrm");
        if (normals && pn != null && hn != null)
        {
            var ha = ImageIO.Load(hn).ToRgb();
            var pa = Resample.Resize(ImageIO.Load(pn).ToRgb(), ha.W, ha.H, Filter.Lanczos);
            double[] al;
            if (ha.W == hi.W && ha.H == hi.H) al = alpha;
            else al = Resample.Resize(pi.Channel(3), ha.W, ha.H, Filter.Lanczos).Px.Select(x => x / 255.0).ToArray();
            var o = new Img(ha.W, ha.H, 3);
            for (int i = 0; i < ha.W * ha.H; i++)
                for (int k = 0; k < 3; k++) o.Px[3 * i + k] = (byte)Math.Clamp(ha.Px[3 * i + k] * (1 - al[i]) + pa.Px[3 * i + k] * al[i], 0, 255);
            ImageIO.SavePng(o, Path.Combine(dst, "head_nrm.png"));
        }
        int painted = alpha.Count(a => a > 0.04);
        log($"face paint: {Path.GetRelativePath(chr, paint)} laid on the head ({(100.0 * painted / n).ToString("F0", Inv)}% of the face texture painted)");
        return [dst, .. first];
    }

    // ---------------------------------------------------------------- normal maps' green
    static double[] Gradient(double[] a, int h, int w, int axis)
    {
        var o = new double[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (axis == 1)
                    o[i] = x == 0 ? (a[i + 1] - a[i]) / 1.0 : x == w - 1 ? (a[i] - a[i - 1]) / 1.0 : (a[i + 1] - a[i - 1]) / 2.0;
                else
                    o[i] = y == 0 ? (a[i + w] - a[i]) / 1.0 : y == h - 1 ? (a[i] - a[i - w]) / 1.0 : (a[i + w] - a[i - w]) / 2.0;
            }
        return o;
    }

    /// <summary>rock_model.green_curl_ratio: a normal map's curl with green as it is / turned (x, y 0 - 255)</summary>
    public static double GreenCurlRatio(double[] r, double[] g, int w, int h, int side = 512)
    {
        (double[] V, int W, int H) Small(double[] c)
        {
            var im = new Img(w, h, 1, c.Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray());
            var s = Resample.Resize(im, Math.Min(side, w), Math.Min(side, h), Filter.Box);
            return (s.Px.Select(v => v / 255.0 - 0.5).ToArray(), s.W, s.H);
        }
        var (x, sw, sh) = Small(r);
        var (y, _, _) = Small(g);
        if (sh < 8 || sw < 8) return 1.0;
        var gy1 = Gradient(y, sh, sw, 1);
        var gx0 = Gradient(x, sh, sw, 0);
        var gny1 = Gradient(y.Select(v => -v).ToArray(), sh, sw, 1);
        var da = new double[sw * sh]; var db = new double[sw * sh];
        for (int i = 0; i < da.Length; i++) { da[i] = Math.Abs(gy1[i] - gx0[i]); db[i] = Math.Abs(gny1[i] - gx0[i]); }
        double a = Np.Sum(da) / da.Length, b = Np.Sum(db) / db.Length;
        return b > 1e-9 ? a / b : 1.0;
    }

    public static void CheckGreen(RockModel R, string chr, string attire, Action<string> log)
    {
        var p = R.FindTex(chr, AttireDir(chr, attire), "head", "_nrm");
        if (p == null) return;
        Img a;
        try { a = Resample.Resize(ImageIO.Load(p).ToRgb(), 1024, 1024, Filter.Lanczos); }
        catch (Exception) { return; }
        int n = a.W * a.H;
        var r = new double[n]; var g = new double[n];
        for (int i = 0; i < n; i++) { r[i] = a.Px[3 * i]; g[i] = R.NormalFlipG ? 255.0 - a.Px[3 * i + 1] : a.Px[3 * i + 1]; }
        double ratio = GreenCurlRatio(r, g, a.W, a.H);
        if (ratio > GreenTurnAt)
        {
            R.NormalFlipG = !R.NormalFlipG;
            log($"normal maps: the face map measures {ratio.ToString("F2", Inv)} (over 1: green against SvR's) - green {(R.NormalFlipG ? "flipped" : "kept as stored")} for this wrestler");
        }
        else log($"normal maps: the face map measures {ratio.ToString("F2", Inv)} - green as set");
    }

    // ---------------------------------------------------------------- the port (port_2k.build / _build)
    public sealed class Result
    {
        public string Pac = "";
        public System.Text.Json.Nodes.JsonObject Rep = new();
        public List<string> Report = [];
        public Info Info = new();
        public (int FaceOver, int GearOver, List<string> Notes) Checks;
        public List<string> Pack = [];
        public RockModel R = new();
        public FullPort FP = new();
    }

    /// <summary>a 2K wrestler -&gt; out\ch.pac (+ the texture pack on the full route). budget: the classic window's pages, or
    /// FULL_BUDGET and over for the full-detail route. log: the port's log; print: what the Python printed to stdout.</summary>
    public Result Build(string chr, string attire, string outDir, string game, int budget = 190, string? template = null, Action<string>? log = null,
                        Action<string>? print = null, string packPrefix = "portit_hd", IEnumerable<string>? leaveOut = null, AewBridge? bridge = null, int packSize = 8192, IEnumerable<string>? use = null)
    {
        log ??= _ => { };
        UseVariants = new HashSet<string>(use ?? [], StringComparer.OrdinalIgnoreCase);
        print ??= _ => { };
        Bridge = bridge;
        var R = new RockModel { TemplateMouth = bridge == null, CornerSmooth = 14 };
        var FP = new FullPort { GearHead = bridge != null, PackSize = packSize };
        R.Fbx.UseVariants.UnionWith(UseVariants);
        R.NormalFlipG = bridge == null && !YukesPack(chr);
        if (bridge != null) { R.Source = bridge.Build; chr = bridge.Char; attire = bridge.Attire; }
        R.TexFirst = [];
        if (bridge == null)
        {
            R.TexFirst = FacePaint(chr, AttireDir(chr, attire), outDir, log);
            CheckGreen(R, chr, attire, log);
        }
        Directory.CreateDirectory(outDir);
        bool full = budget >= FullBudget;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LeaveOut = (leaveOut ?? []).Where(n => !string.IsNullOrEmpty(n)).ToHashSet();
        if (LeaveOut.Count > 0) log("left out (unticked): " + string.Join(", ", LeaveOut.Order(StringComparer.Ordinal)));
        var info = Setup(R, chr, attire, outDir, full, print);
        log($"read the 2K model: {sw.Elapsed.TotalSeconds:F0} s");
        template ??= Path.Combine(game, "pac", "ch", "ch125.pac");
        var cards = CardMeshes(info);
        // (an alternate look used - M_Hair_ENT: card_meshes goes by name; one on a hair-card texture is cards too)
        foreach (var (t, ms) in info.TNames)
            if (info.Cutout.Contains(t))
                foreach (var m in ms) if (UseVariants.Contains(m.Name) && !cards.Contains(m.Name)) cards.Add(m.Name);
        string pac = Path.Combine(outDir, "ch.pac");
        var flat = FP.FlatCards(info.RawMeshes, cards.ToHashSet(), log);
        string model = Path.Combine(outDir, "model.json");
        if (full)
        {
            var skip = info.TNames.Where(kv => kv.Value.All(m => flat.Contains(m.Name))).Select(kv => kv.Key).ToHashSet();
            var cells = FP.Layout(R, chr, AttireDir(chr, attire), skip, log);
            log("texture sheet 8192 x 4096: " + string.Join(", ", cells.Select(kv => $"{kv.Key} {kv.Value.W}x{kv.Value.H}")));
            var cj = new System.Text.Json.Nodes.JsonObject();
            foreach (var (t, c) in R.Cells) cj[t] = new System.Text.Json.Nodes.JsonArray(c.X, c.Y, c.W, c.H);
            File.WriteAllText(Path.Combine(outDir, "cells.json"), new System.Text.Json.Nodes.JsonObject
            {
                ["cells"] = cj, ["mouth"] = new System.Text.Json.Nodes.JsonArray(R.TmplMouthCell.X, R.TmplMouthCell.Y),
            }.ToJsonString());
            R.Target = [];
            foreach (var n in cards) R.Kind[n] = "hair";
        }
        int cap = full ? FullPort.WindowPages(game, log) : 0;
        // (no big-model DLL - the game's 768 KB: a full-detail port is shared out like the classic window, the face and
        //  body first, not every mesh cut by the same factor - the face went 4310 -> 593 points, GitHub issue 1, 2026-11-12)
        bool small = full && cap < 300;
        double scale = small ? Math.Min(1.0, cap / 190.0) : 1.0;
        var rawCount = info.Meshes.ToDictionary(x => x.Name, x => x.Verts);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var cardT = new Dictionary<string, int>();
            if (!full || small)
            {
                var T = Targets(info, scale);
                foreach (var n in T.Keys.ToList())
                    if (cards.Contains(n)) { cardT[n] = Math.Max(300, (int)(Math.Min(rawCount[n], HairRoom) * scale)); T.Remove(n); }
                R.Target = T;
            }
            else if (scale >= 1.0)
            {
                // (full detail keeps every point - but a mod's gear can be absurd: Drew Gulak 2K23's pants 269,738 points and
                //  jacket 153,197 made one part 3,874 pages, twice what the game draws; such a piece is cut to FullGearMax)
                R.Target = info.Meshes.Where(x => !cards.Contains(x.Name) && x.Verts > FullGearLimit && x.Name is not ("M_Body" or "M_Head"))
                                      .ToDictionary(x => x.Name, x => FullGearMax);
                foreach (var (n, _) in R.Target) log($"{n}: {rawCount[n]} points - more than the game can draw in one piece, cut to {FullGearMax}");
            }
            else if (scale < 1.0)
            {
                R.Target = info.Meshes.Where(x => !cards.Contains(x.Name)).ToDictionary(x => x.Name, x => Math.Max(150, (int)(x.Verts * scale)));
                foreach (var n in cards) cardT[n] = Math.Max(300, (int)(rawCount[n] * Math.Sqrt(scale)));   // (hair cut less than the rest: it vanished at x0.2, GitHub issue 1)
            }
            var ct = cardT;
            R.Prepare = (meshes, bones) =>
            {
                int head = bones.FindIndex(b => b.Src == "J_Head");
                // (painted into the skin map before thinning: the flat cards, and the scalp cards a cut-down port thins -
                //  the gaps then show hair, not the light scalp of the head map; GitHub issue 1, 2026-11-12)
                var paint = new List<(RockFbx.Mesh, double)>();
                foreach (var m in meshes)
                    if (flat.Contains(m.Name)) paint.Add((Clone(m), double.PositiveInfinity));
                    else if (ct.ContainsKey(m.Name)) paint.Add((Clone(m), ScalpPaintCm));
                    // (every other card mesh - a beard, the hair at the hairline: its cards lying on the skin painted under
                    //  them too, so the gaps between the strands show hair, not bare skin - Seth Rollins' beard looked
                    //  see-through in game, 2026-11-12)
                    else if (cards.Contains(m.Name)) paint.Add((Clone(m), CardBaseCm));
                R.TexOverride.Clear();
                if (paint.Count > 0) BakeFlatCards(meshes, paint, outDir, log, R.TexOverride, t => R.FindTex(chr, attire, t, "_color"), headOnly: MovingHair);   // (moving hair: flat braid ends painted on the back would stay there while the braids swing)
                foreach (var m in meshes)
                {
                    if (m.Weights.Count == 0) { var one = new double[m.N]; Array.Fill(one, 1.0); m.Weights.Add((head, one)); }
                    if (ct.TryGetValue(m.Name, out int tc))
                    {
                        var (nc, kc) = ThinCards(m, tc);
                        log($"{m.Name}: cards {nc} -> {kc}");
                    }
                    else if (!full && R.Target.TryGetValue(m.Name, out int tg) && m.N > 3 * tg && StrandPieces(m) > 300)
                    {
                        var (nc, kc) = ThinCards(m, 2 * tg);
                        log($"{m.Name}: strands {nc} -> {kc}");
                    }
                }
                return meshes.Where(m => !flat.Contains(m.Name)).ToList();
            };
            sw.Restart();
            R.Log.Clear();
            var (_, report) = R.Build(chr, attire, outDir, template);
            foreach (var l in R.Fbx.Report.Concat(R.Log)) print(l);
            FP.SkinOverGear = R.Fbx.UsedHpl ? 0.10 : double.PositiveInfinity;
            log($"model built: {sw.Elapsed.TotalSeconds:F0} s");
            sw.Restart();
            FP.FixModel(model, cards.Where(n => !flat.Contains(n)).ToList(), template, log);
            {
                // (the builder's own colour sheet: the hair cards' cells, cut hard in every level as the HD pack -
                //  Seth Rollins' beard came out big and blocky in game, 2026-11-12)
                var mj = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(model))!;
                var cc = info.Cutout.Where(t => R.Cells.ContainsKey(t)).Select(t => R.Cells[t]).ToList();
                foreach (var at in mj["attires"]!.AsArray())
                {
                    if (MovingHair) at!["hair_physics"] = true;
                    if (cc.Count > 0)
                    {
                        at!["cut_cells"] = new System.Text.Json.Nodes.JsonArray(cc.Select(c => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonArray(c.Item1, c.Item2, c.Item3, c.Item4)).ToArray());
                        at["cut_sheet"] = new System.Text.Json.Nodes.JsonArray(R.Sheet.W, R.Sheet.H);
                    }
                }
                File.WriteAllText(model, mj.ToJsonString());
            }
            log($"weights fixed: {sw.Elapsed.TotalSeconds:F0} s");
            sw.Restart();
            var rep = PortIT.Engine.Build.SvrBuilder.Run(model, pac, full);
            log($"ch.pac built: {sw.Elapsed.TotalSeconds:F0} s");
            if (rep["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(rep["error"]?.GetValue<string>() ?? "build failed");
            HairNote = null;
            foreach (var w in rep["warnings"] as System.Text.Json.Nodes.JsonArray ?? [])
                if (w?.GetValue<string>() is { } ws && ws.StartsWith("moving hair")) { log(ws); HairNote = ws; }
            int pages = rep["attires"]![0]!["pages"]!.GetValue<int>();
            if (full && pages > cap && attempt < 5)
            {
                scale *= (double)cap / pages * 0.97;
                log($"build {attempt + 1}: {pages} pages - over the game vertex window ({cap} pages a wrestler), simplifying (x{scale.ToString("F2", Inv)})");
                continue;
            }
            if (!full && pages > budget - 2 && attempt < 5)
            {
                scale *= (double)(budget - 2) / pages * 0.97;
                log($"build {attempt + 1}: {pages} pages - over {budget}, simplifying more (x{scale.ToString("F2", Inv)})");
                continue;
            }
            log($"build {attempt + 1}: {pages} pages ({(full ? "full detail" : $"window {budget}")})");
            var (heavy, most) = FP.HeavyMeshes(pac);
            if (heavy.Count > 0 && attempt < 5)
            {
                int biggest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(model))!["attires"]![0]!["meshes"]!.AsArray().Max(m => m!["triangles"]!.AsArray().Count);
                FP.MaxMeshTris = Math.Max(2000, (int)(Math.Min(FP.MaxMeshTris, biggest) * 0.6));
                log($"build {attempt + 1}: {heavy.Count} mesh(es) over {FP.SafeIndices} indices - meshes over {FP.MaxMeshTris} triangles split, again");
                continue;
            }
            if (heavy.Count > 0)
            {
                report.Add($"warning: {heavy.Count} mesh(es) still over {FP.SafeIndices} indices (the most that has run in game; the game takes 65535)");
                log(report[^1]);
            }
            if (most > FP.StockMeshes)
            {
                report.Add($"warning: a part has {most} meshes (stock up to {FP.StockMeshes})");
                log(report[^1]);
            }
            var probs = FP.PacLimits(pac, full ? cap : budget);
            if (probs.Any(p => p.Contains("indices")) && FP.MaxMeshTris > 3000 && attempt < 5)
            {
                FP.MaxMeshTris /= 2;
                log($"build {attempt + 1}: a mesh over 65535 indices - split at {FP.MaxMeshTris} triangles, again");
                continue;
            }
            if (probs.Count > 0) throw new InvalidOperationException("the built model breaks the game limits (it would crash): " + string.Join("; ", probs));
            report.Add($"game limits: every mesh under 65535 vertices / indices, every part under {FP.PartPages} pages, {pages} pages in all (the window takes {(full ? cap : budget)})");
            var checks = FP.Checks(model, template);
            report.Add($"checks: {checks.FaceOver} face vertices swing past the stock head, {checks.GearOver} gear vertices drift from the skin{(checks.Notes.Count == 0 ? "" : " - " + string.Join("; ", checks.Notes.Take(6)))}");
            log(report[^1]);
            if (LeaveOut.Count > 0) report.Add("left out by you: " + string.Join(", ", LeaveOut.Order(StringComparer.Ordinal)));
            if (HairNote != null) report.Add(HairNote);
            if (full && cap <= 768 / 4 - 24)
                report.Add($"cut down to {cap} pages: the game has not used PortIT's big-model DLL yet (no bigmodel.log in the game folder, " +
                           "or it shows the normal 768 KB) - hair is thinner and clothes rougher. Install the DLL, start the game once, then port again for the full model");
            if (flat.Count > 0) report.Add("left out (painted on the head texture): " + string.Join(", ", flat.Order(StringComparer.Ordinal)));
            var pack = new List<string>();
            if (full)
            {
                var cut = info.Cutout.Where(t => R.Cells.ContainsKey(t)).Select(t => R.Cells[t]).ToList();
                pack = FP.MakePack(outDir, pac, Path.Combine(outDir, "texture_pack"), packPrefix, cut, log);
            }
            return new Result { Pac = pac, Rep = rep, Report = report, Info = info, Checks = checks, Pack = pack, R = R, FP = FP };
        }
        throw new InvalidOperationException("could not fit the model");
    }
}
