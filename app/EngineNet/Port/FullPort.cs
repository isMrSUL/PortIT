using System.Text.Json.Nodes;
using PortIT.Engine.Build;
using PortIT.Engine.Util;

namespace PortIT.Engine.Port;

/// <summary>
/// k26/full_port.py, the model.json fixes after rock_model.build (fix_model): hair / face cards on the skin's weights
/// (card_weights, mouth_cards), gear moving with the skin under it (gear_weights), the head under a mask removed
/// (hide_head_under), body skin tucked in under the gear (tuck_skin), AEW inner-lip bridges, big meshes split.
/// The NumPy arithmetic orders are the measured ones (Util/Np.cs): cross products plain, einsum 'ij,ij-&gt;i' of three
/// (p0 + p2) + p1, row sums in order, (k, 3) @ (3,) fused 1, 0, 2.
/// </summary>
public sealed class FullPort
{
    public double FlatCardCm = 2.5, KeepEdgeCm = 3.0;
    public (double Lo, double Hi) GearSkin = (0.15, 0.35);
    public bool GearHead = false;
    public double MouthGap = 0.03;
    public static readonly string[] Lower = ["d_kuchi", "d_kuchi_00", "d_kuchi_01", "l_kuchi", "r_kuchi"];
    public int MaxMeshTris = 30000;
    public Dictionary<string, int> Split = [];
    public const double Shallow = 0.30;
    public (double Margin, double Deep) Tuck = (0.04, 0.70);
    /// <summary>how far skin outside the gear (along the gear's own facing) is still tucked in below it. Unlimited for
    /// models without a 2K hidden-polygon list; 0.10 (1 cm, against flicker) when the body was cut by the attire's own
    /// list: 2K shows the rest of the skin, also where it lies over the gear (the back of the calves over an open-backed
    /// boot's inner panel - tucking it 3 - 7 cm in left a hole, Rock 1999, 2026-11-12)</summary>
    public double SkinOverGear = double.PositiveInfinity;
    /// <summary>no skin is moved under gear hit this close (model units, 0.1 = 1 cm) to the gear's open edge</summary>
    public double HemCm = 1.0;   // (3 cm left the outer hip steps on Andre; 10 cm: none, no skin through Big E / Rock gear)
    static readonly string[] NotGearPrefix = ["M_Body", "M_Head", "M_EYE", "M_TransB", "M_transB", "M_MouthBag", "mouth_", "M_AEWMouth", "M_InnerMouth", "M_Teeth", "M_Tongue"];

    // ---------------------------------------------------------------- model.json access
    static JsonArray Meshes(JsonObject spec) => spec["attires"]![0]!["meshes"]!.AsArray();
    static string Name(JsonNode m) => m["name"]!.GetValue<string>();
    static string? KindOf(JsonNode m) => m["kind"]?.GetValue<string>();
    static double D(JsonNode? x) => x!.GetValue<double>();

    static double[] Flat(JsonNode m, string key, int k)
    {
        var a = m[key]!.AsArray();
        var o = new double[a.Count * k];
        for (int i = 0; i < a.Count; i++) { var r = a[i]!.AsArray(); for (int c = 0; c < k; c++) o[k * i + c] = D(r[c]); }
        return o;
    }

    static int[] Tris(JsonNode m)
    {
        var a = m["triangles"]!.AsArray();
        var o = new int[a.Count * 3];
        for (int i = 0; i < a.Count; i++) { var r = a[i]!.AsArray(); for (int c = 0; c < 3; c++) o[3 * i + c] = r[c]!.GetValue<int>(); }
        return o;
    }

    static List<string> Bones(JsonNode m) => m["bones"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();

    /// <summary>per vertex [(bone name, w)] in the file's order</summary>
    static List<List<(string B, double W)>> Weights(JsonNode m)
    {
        var bn = Bones(m);
        return m["weights"]!.AsArray().Select(e => e!.AsArray().Select(x => (bn[x![0]!.GetValue<int>()], D(x[1]))).ToList()).ToList();
    }

    static JsonArray Rows(double[] a, int k, int? round = null)
    {
        var o = new JsonArray();
        for (int i = 0; i < a.Length / k; i++)
        {
            var r = new JsonArray();
            for (int c = 0; c < k; c++) r.Add(round is int nd ? Np.Round(a[k * i + c], nd) : a[k * i + c]);
            o.Add(r);
        }
        return o;
    }

    static JsonArray TriRows(IEnumerable<int> flat)
    {
        var o = new JsonArray(); var f = flat.ToArray();
        for (int t = 0; t < f.Length; t += 3) o.Add(new JsonArray(f[t], f[t + 1], f[t + 2]));
        return o;
    }

    static void Keep(JsonObject m, IReadOnlyList<int> used)
    {
        foreach (var key in new[] { "positions", "normals", "uvs", "weights" })
        {
            var a = m[key]!.AsArray();
            m[key] = new JsonArray(used.Select(i => a[i]!.DeepClone()).ToArray());
        }
    }

    static void SetWeights(JsonObject m, List<string> bones, List<List<(string B, double W)>> wl, bool sortDesc)
    {
        var bi = new Dictionary<string, int>();
        for (int k = 0; k < bones.Count; k++) bi[bones[k]] = k;
        m["bones"] = new JsonArray(bones.Select(b => (JsonNode)b).ToArray());
        m["weights"] = new JsonArray(wl.Select(e => (JsonNode)new JsonArray((sortDesc ? e.OrderBy(x => -x.W).ToList() : e)
            .Where(x => x.W > 1e-5).Select(x => (JsonNode)new JsonArray(bi[x.B], Np.PyRound(x.W, 5))).ToArray())).ToArray());
    }

    // ---------------------------------------------------------------- NumPy-ordered small maths
    static double[] Cross(double[] a, int ia, double[] b, int ib) =>
        [a[ia + 1] * b[ib + 2] - a[ia + 2] * b[ib + 1], a[ia + 2] * b[ib] - a[ia] * b[ib + 2], a[ia] * b[ib + 1] - a[ia + 1] * b[ib]];
    static double Ein(double[] a, int ia, double[] b, int ib) => (a[ia] * b[ib] + a[ia + 2] * b[ib + 2]) + a[ia + 1] * b[ib + 1];
    static double RowDot(double[] a, int ia, double[] x) =>
        Math.FusedMultiplyAdd(a[ia + 2], x[2], Math.FusedMultiplyAdd(a[ia], x[0], Math.FusedMultiplyAdd(a[ia + 1], x[1], 0.0)));
    static double RowSum3(double a, double b, double c) => (a + b) + c;
    static double Norm3Seq(double x, double y, double z) => Math.Sqrt(0.0 + x * x + y * y + z * z);

    static double[] Mean0(double[] P)
    {
        int n = P.Length / 3; var m = new double[3];
        for (int i = 0; i < n; i++) for (int c = 0; c < 3; c++) m[c] += P[3 * i + c];
        for (int c = 0; c < 3; c++) m[c] /= n;
        return m;
    }

    /// <summary>_nearest: for each P point, the nearest Q point and its squared distance</summary>
    public static (int[] J, double[] D2) Nearest(double[] P, double[] Q)
    {
        int n = P.Length / 3, m = Q.Length / 3;
        var j = new int[n]; var d = new double[n];
        Parallel.For(0, n, i =>
        {
            int best = 0; double bd = double.PositiveInfinity;
            for (int q = 0; q < m; q++)
            {
                double dx = P[3 * i] - Q[3 * q], dy = P[3 * i + 1] - Q[3 * q + 1], dz = P[3 * i + 2] - Q[3 * q + 2];
                double dd = dx * dx + dy * dy + dz * dz;
                if (dd < bd) { bd = dd; best = q; }
            }
            j[i] = best; d[i] = bd;
        });
        return (j, d);
    }

    /// <summary>_cards: connected pieces (union-find as the Python does it) -&gt; a root label per point</summary>
    public static int[] Cards(int n, int[] T)
    {
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        for (int t = 0; t < T.Length; t += 3)
            foreach (var (x, y) in new[] { (T[t], T[t + 1]), (T[t + 1], T[t + 2]) })
            {
                int rx = Find(x), ry = Find(y);
                if (rx != ry) parent[rx] = ry;
            }
        return Enumerable.Range(0, n).Select(Find).ToArray();
    }

    static double Median(double[] a)
    {
        var s = (double[])a.Clone(); Array.Sort(s);
        int n = s.Length;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }

    // ---------------------------------------------------------------- flat cards (on the 2K meshes)
    public HashSet<string> FlatCards(List<RockFbx.Mesh> meshes, HashSet<string> cardNames, Action<string> log)
    {
        var skin = meshes.Where(m => m.Name is "M_Body" or "M_Head").ToList();
        var o = new HashSet<string>();
        if (skin.Count == 0) return o;
        var S = skin.SelectMany(m => m.Verts).ToArray();
        foreach (var m in meshes)
        {
            if (!cardNames.Contains(m.Name)) continue;
            var (_, d2) = Nearest(m.Verts, S);
            double far = 0.0;
            foreach (var x in d2) far = Math.Max(far, Math.Sqrt(x));
            if (far < FlatCardCm)
            {
                o.Add(m.Name);
                log($"{m.Name}: hair cards lie flat on the skin (all within {far.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} cm) - painted on the head texture, left out");
            }
        }
        return o;
    }

    // ---------------------------------------------------------------- cards
    static (Dictionary<string, double> Lim, double Scale) LeverLimits(SvrBuilder.Template c, JsonObject joints)
    {
        var tf = Face.TemplateFaceOf(c);
        var lim = new Dictionary<string, double>();
        for (int i = 0; i < tf.W.Count; i++)
            foreach (var (b, w) in tf.W[i])
                if (tf.FaceBones.Contains(b) && tf.Pos.TryGetValue(b, out var pb) && w > 0.01)
                {
                    double[] dv = [tf.V[3 * i] - pb[0], tf.V[3 * i + 1] - pb[1], tf.V[3 * i + 2] - pb[2]];
                    lim[b] = Math.Max(lim.GetValueOrDefault(b, 0.0), w * Blas.Norm(dv));
                }
        double[] le = joints["l_eye"]!.AsArray().Select(D).ToArray(), re = joints["r_eye"]!.AsArray().Select(D).ToArray();
        double iod = Blas.Norm(new[] { le[0] - re[0], le[1] - re[1], le[2] - re[2] });
        return (lim, iod / Blas.Norm(new[] { tf.EyeL[0] - tf.EyeR[0], tf.EyeL[1] - tf.EyeR[1], tf.EyeL[2] - tf.EyeR[2] }));
    }

    public HashSet<string> CardWeights(JsonObject spec, IReadOnlyCollection<string> cardNames, SvrBuilder.Template template, Action<string> log)
    {
        var ms = Meshes(spec);
        var J = spec["joints"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(D).ToArray());
        double eyeY = (J["l_eye"][1] + J["r_eye"][1]) / 2;
        var skin = ms.Where(m => Name(m!).StartsWith("M_Head") || Name(m!).StartsWith("M_Body")).Select(m => m!).ToList();
        var SP = skin.SelectMany(m => Flat(m, "positions", 3)).ToArray();
        var SW = skin.SelectMany(Weights).ToList();
        var isHead = skin.SelectMany(m => Enumerable.Repeat(Name(m).StartsWith("M_Head"), m["positions"]!.AsArray().Count)).ToArray();
        var lower = SW.Select(e => { double s = 0; foreach (var (b, w) in e) if (Lower.Contains(b)) s += w; return s >= 0.3; }).ToArray();
        var (lim, scale) = LeverLimits(template, spec["joints"]!.AsObject());
        var faceCards = new HashSet<string>();
        foreach (var mn in ms)
        {
            var m = mn!.AsObject();
            string nm = Name(m);
            if (!cardNames.Any(n => nm == n || nm.StartsWith(n + "_"))) continue;
            var P = Flat(m, "positions", 3);
            int np_ = P.Length / 3;
            var comp = Cards(np_, Tris(m));
            bool face = Median(Enumerable.Range(0, np_).Select(i => P[3 * i + 1]).ToArray()) > eyeY;
            var (nn, dist) = Nearest(P, SP);
            var side = new Dictionary<int, bool>();
            if (face)
            {
                faceCards.Add(nm);
                var root = new Dictionary<int, int>();
                for (int i = 0; i < np_; i++)
                    if (!root.TryGetValue(comp[i], out int r) || dist[i] < dist[r]) root[comp[i]] = i;
                foreach (var (r, i) in root) side[r] = lower[nn[i]];
                foreach (bool s in new[] { true, false })
                {
                    var idx = Enumerable.Range(0, lower.Length).Where(i => lower[i] == s).ToArray();
                    var vi = Enumerable.Range(0, np_).Where(i => side[comp[i]] == s).ToArray();
                    if (vi.Length > 0 && idx.Length > 0)
                    {
                        var (j, _) = Nearest(Take3(P, vi), Take3(SP, idx));
                        for (int q = 0; q < vi.Length; q++) nn[vi[q]] = idx[j[q]];
                    }
                }
            }
            else
            {
                var idx = Enumerable.Range(0, isHead.Length).Where(i => isHead[i]).ToArray();
                var (j, _) = Nearest(P, Take3(SP, idx));
                for (int q = 0; q < np_; q++) nn[q] = idx[j[q]];
            }
            var wlAll = new List<List<(string B, double W)>>();
            int capped = 0;
            for (int v = 0; v < np_; v++)
            {
                var wl = new List<(string B, double W)>(SW[nn[v]]);
                double spare = 0.0;
                for (int q = 0; q < wl.Count; q++)
                {
                    string b = wl[q].B;
                    if (!lim.ContainsKey(b) || b == "d_kuchi" || !J.ContainsKey(b)) continue;
                    var jb = J[b];
                    double wmax = lim[b] * scale / Math.Max(Blas.Norm(new[] { P[3 * v] - jb[0], P[3 * v + 1] - jb[1], P[3 * v + 2] - jb[2] }), 1e-6);
                    if (wl[q].W > wmax) { spare += wl[q].W - wmax; wl[q] = (b, wmax); capped++; }
                }
                if (spare != 0)
                {
                    string to = side.GetValueOrDefault(comp[v]) ? "d_kuchi" : "atama";
                    int at = wl.FindIndex(x => x.B == to);
                    if (at < 0) wl.Add((to, 0.0 + spare)); else wl[at] = (to, wl[at].W + spare);
                }
                var top = wl.OrderBy(x => -x.W).Take(4).ToList();
                double tot = 0; foreach (var (_, w) in top) tot += w;
                wlAll.Add(top.Select(x => (x.B, x.W / tot)).ToList());
            }
            var bones = wlAll.SelectMany(e => e.Select(x => x.B)).Distinct().Order(StringComparer.Ordinal).ToList();
            SetWeights(m, bones, wlAll, sortDesc: false);
            int pieces = comp.Distinct().Count();
            log($"{nm}: {(face ? "face" : "scalp")} cards ({pieces}), on the skin under them{(face ? $", {side.Values.Count(x => x)} on the jaw side" : "")}; {capped} face-bone weights capped to the stock swing");
        }
        return faceCards;
    }

    static double[] Take3(double[] a, int[] idx)
    {
        var o = new double[idx.Length * 3];
        for (int i = 0; i < idx.Length; i++) { o[3 * i] = a[3 * idx[i]]; o[3 * i + 1] = a[3 * idx[i] + 1]; o[3 * i + 2] = a[3 * idx[i] + 2]; }
        return o;
    }

    public void MouthCards(JsonObject spec, HashSet<string> faceCards, Action<string> log)
    {
        var ms = Meshes(spec);
        var heads = ms.Where(m => Name(m!).StartsWith("M_Head")).Select(m => m!).ToList();
        var V = new List<double>(); var T = new List<int>(); int off = 0;
        foreach (var m in heads)
        {
            V.AddRange(Flat(m, "positions", 3));
            T.AddRange(Tris(m).Select(x => x + off));
            off += m["positions"]!.AsArray().Count;
        }
        var J = spec["joints"]!.AsObject();
        var L = Face.Landmarks(V.ToArray(), T.ToArray(), J["l_eye"]!.AsArray().Select(D).ToArray(), J["r_eye"]!.AsArray().Select(D).ToArray()).L;
        double my = L["mouth"][1];
        double x0 = Math.Min(L["mouth_l"][0], L["mouth_r"][0]), x1 = Math.Max(L["mouth_l"][0], L["mouth_r"][0]);
        double half = (x1 - x0) / 2, xm = (x0 + x1) / 2, zf = L["mouth"][2];
        foreach (var mn in ms)
        {
            var m = mn!.AsObject();
            if (!faceCards.Contains(Name(m))) continue;
            var P = Flat(m, "positions", 3);
            var Tm = Tris(m);
            int np_ = P.Length / 3;
            var bn = Bones(m);
            var jaw = m["weights"]!.AsArray().Select(e => { double s = 0; foreach (var x in e!.AsArray()) if (Lower.Contains(bn[x![0]!.GetValue<int>()])) s += D(x[1]); return s; }).ToArray();
            var comp = Cards(np_, Tm);
            var inside = Enumerable.Range(0, np_).Select(i => Math.Abs(P[3 * i] - xm) < half * 1.1 && P[3 * i + 2] < zf + 0.35).ToArray();
            var drop = new HashSet<int>();
            foreach (int r in comp.Distinct())
            {
                var vi = Enumerable.Range(0, np_).Where(i => comp[i] == r).ToArray();
                double mean = Np.Sum(vi.Select(i => jaw[i]).ToArray()) / vi.Length;
                bool hit = mean > 0.2 ? vi.Any(i => inside[i] && P[3 * i + 1] < my - MouthGap) : vi.Any(i => inside[i] && P[3 * i + 1] > my + MouthGap);
                if (hit) drop.Add(r);
            }
            if (drop.Count == 0) continue;
            var keptT = new List<int>();
            for (int t = 0; t < Tm.Length; t += 3) if (!drop.Contains(comp[Tm[t]])) keptT.AddRange([Tm[t], Tm[t + 1], Tm[t + 2]]);
            var used = keptT.Distinct().Order().ToArray();
            var remap = new int[np_]; Array.Fill(remap, -1);
            for (int i = 0; i < used.Length; i++) remap[used[i]] = i;
            m["triangles"] = TriRows(keptT.Select(x => remap[x]));
            Keep(m, used);
            log($"{Name(m)}: {drop.Count} of {comp.Distinct().Count()} cards crossed the lip line in the mouth - left out");
        }
    }

    // ---------------------------------------------------------------- gear on the skin's weights
    public void GearWeights(JsonObject spec, HashSet<string> skip, Action<string> log)
    {
        var ms = Meshes(spec);
        var body = ms.Select(m => m!).Where(m => Name(m).StartsWith("M_Body") || (GearHead && Name(m).StartsWith("M_Head"))).ToList();
        var BP = body.SelectMany(m => Flat(m, "positions", 3)).ToArray();
        var BW = body.SelectMany(Weights).Select(e => { var d = new List<(string B, double W)>(); foreach (var x in e) { int at = d.FindIndex(y => y.B == x.B); if (at < 0) d.Add(x); else d[at] = x; } return d; }).ToList();
        var (lo, hi) = GearSkin;
        string[] skipPre = ["M_Body", "M_Head", "M_EYE", "M_TransB", "M_MouthBag", "mouth_", "M_AEWMouth"];
        foreach (var mn in ms)
        {
            var m = mn!.AsObject();
            string nm = Name(m);
            if (skipPre.Any(p => nm.StartsWith(p)) || skip.Contains(nm) || KindOf(m) is "eye" or "lash" or "hair") continue;
            var P = Flat(m, "positions", 3);
            int np_ = P.Length / 3;
            var (j, d2) = Nearest(P, BP);
            var bn = Bones(m);
            var mw = m["weights"]!.AsArray();
            if (GearHead && nm.StartsWith("M_InnerMouth"))
            {
                var ownLo = mw.Select(e => { double s = 0; foreach (var x in e!.AsArray()) if (bn[x![0]!.GetValue<int>()] == "d_kuchi") s += D(x[1]); return s > 0.2; }).ToArray();
                var skLo = BW.Select(e => e.FirstOrDefault(x => x.B == "d_kuchi").W > 0.2).ToArray();
                foreach (bool side in new[] { false, true })
                {
                    var sel = Enumerable.Range(0, np_).Where(i => ownLo[i] == side).ToArray();
                    var cand = Enumerable.Range(0, skLo.Length).Where(i => skLo[i] == side).ToArray();
                    if (sel.Length > 0 && cand.Length > 0)
                    {
                        var (jj, dd2) = Nearest(Take3(P, sel), Take3(BP, cand));
                        for (int q = 0; q < sel.Length; q++) { j[sel[q]] = cand[jj[q]]; d2[sel[q]] = dd2[q]; }
                    }
                }
            }
            else if (GearHead)
            {
                var skJ = BW.Select(e => { double s = 0; foreach (var b in Lower) s += e.FirstOrDefault(x => x.B == b).W; return s > 0.2; }).ToArray();
                var hit = Enumerable.Range(0, np_).Where(v => skJ[j[v]]).ToArray();
                if (hit.Length > 0)
                {
                    var LJ = Enumerable.Range(0, skJ.Length).Where(i => skJ[i]).ToArray();
                    var up = new List<int>();
                    foreach (int v in hit)
                    {
                        var near = LJ.Where(i => Math.Abs(BP[3 * i] - P[3 * v]) < 0.05).ToArray();
                        if (near.Length > 0 && P[3 * v + 1] < near.Min(i => BP[3 * i + 1])) up.Add(v);
                    }
                    var cand = Enumerable.Range(0, skJ.Length).Where(i => !skJ[i]).ToArray();
                    if (up.Count > 0 && cand.Length > 0)
                    {
                        var ua = up.ToArray();
                        var (jj, dd2) = Nearest(Take3(P, ua), Take3(BP, cand));
                        for (int q = 0; q < ua.Length; q++) { j[ua[q]] = cand[jj[q]]; d2[ua[q]] = dd2[q]; }
                        log($"{nm}: {ua.Length} points above the lower lip kept off the jaw");
                    }
                }
            }
            var outW = new List<List<(string B, double W)>>();
            int moved = 0;
            for (int v = 0; v < np_; v++)
            {
                var own = new List<(string B, double W)>();
                foreach (var x in mw[v]!.AsArray())
                {
                    string b = bn[x![0]!.GetValue<int>()]; double w = D(x[1]);
                    int at = own.FindIndex(y => y.B == b);
                    if (at < 0) own.Add((b, w)); else own[at] = (b, w);
                }
                double t = Math.Clamp((hi - Math.Sqrt(d2[v])) / (hi - lo), 0, 1);
                if (t <= 0) { outW.Add(own); continue; }
                moved++;
                var mix = new List<(string B, double W)>();
                void Add(string b, double w) { int at = mix.FindIndex(y => y.B == b); if (at < 0) mix.Add((b, 0.0 + w)); else mix[at] = (b, mix[at].W + w); }
                foreach (var (b, w) in own) Add(b, (1 - t) * w);
                foreach (var (b, w) in BW[j[v]]) Add(b, t * w);
                var top = mix.OrderBy(x => -x.W).Take(4).ToList();
                double tot = 0; foreach (var (_, w) in top) tot += w;
                outW.Add(top.Select(x => (x.B, x.W / tot)).ToList());
            }
            var bones = outW.SelectMany(e => e.Select(x => x.B)).Distinct().Order(StringComparer.Ordinal).ToList();
            SetWeights(m, bones, outW, sortDesc: true);
            if (moved > 0) log($"{nm}: {moved} of {np_} vertices move with the skin under them");
        }
    }

    // ---------------------------------------------------------------- split / bridges
    public void SplitBig(JsonObject spec, Action<string> log)
    {
        var o = new JsonArray();
        foreach (var mn in Meshes(spec).ToList())
        {
            var m = mn!.AsObject();
            var T = Tris(m);
            int nt = T.Length / 3;
            int most = Math.Min(MaxMeshTris, Split.GetValueOrDefault(Name(m), MaxMeshTris));
            if (nt <= most) { o.Add(m.DeepClone()); continue; }
            var P = Flat(m, "positions", 3);
            var cen = new double[nt * 3];
            for (int t = 0; t < nt; t++)
                for (int c = 0; c < 3; c++) cen[3 * t + c] = ((P[3 * T[3 * t] + c] + P[3 * T[3 * t + 1] + c]) + P[3 * T[3 * t + 2] + c]) / 3;
            int axis = 0; double bestR = double.NegativeInfinity;
            for (int c = 0; c < 3; c++)
            {
                double mx = double.NegativeInfinity, mn2 = double.PositiveInfinity;
                for (int t = 0; t < nt; t++) { mx = Math.Max(mx, cen[3 * t + c]); mn2 = Math.Min(mn2, cen[3 * t + c]); }
                if (mx - mn2 > bestR) { bestR = mx - mn2; axis = c; }
            }
            var order = Enumerable.Range(0, nt).OrderBy(t => cen[3 * t + axis]).ToArray();
            int n = (nt + most - 1) / most;
            int at = 0;
            for (int k = 0; k < n; k++)
            {
                int size = nt / n + (k < nt % n ? 1 : 0);
                var part = order[at..(at + size)].Order().ToArray();
                at += size;
                var tri = part.SelectMany(t => new[] { T[3 * t], T[3 * t + 1], T[3 * t + 2] }).ToArray();
                var used = tri.Distinct().Order().ToArray();
                var remap = new int[P.Length / 3]; Array.Fill(remap, -1);
                for (int i = 0; i < used.Length; i++) remap[used[i]] = i;
                var piece = new JsonObject();
                foreach (var (key, val) in m)
                    if (key is not ("positions" or "normals" or "uvs" or "weights" or "triangles")) piece[key] = val!.DeepClone();
                piece["name"] = $"{Name(m)}_s{k}";
                foreach (var key in new[] { "positions", "normals", "uvs", "weights" })
                {
                    var a = m[key]!.AsArray();
                    piece[key] = new JsonArray(used.Select(i => a[i]!.DeepClone()).ToArray());
                }
                piece["triangles"] = TriRows(tri.Select(x => remap[x]));
                o.Add(piece);
            }
            log($"{Name(m)}: {nt} triangles - split into {n} meshes (the game: 16-bit index counts)");
        }
        spec["attires"]![0]!["meshes"] = o;
    }

    public void InnerBridges(JsonObject spec, Action<string> log, double spread = 0.3)
    {
        foreach (var mn in Meshes(spec))
        {
            var m = mn!.AsObject();
            if (!Name(m).StartsWith("M_InnerMouth")) continue;
            var bn = Bones(m);
            var w = m["weights"]!.AsArray().Select(e => { double s = 0; foreach (var x in e!.AsArray()) if (Lower.Contains(bn[x![0]!.GetValue<int>()])) s += D(x[1]); return s; }).ToArray();
            var T = Tris(m);
            var kept = new List<int>(); int cut = 0;
            for (int t = 0; t < T.Length; t += 3)
            {
                double a = w[T[t]], b = w[T[t + 1]], c = w[T[t + 2]];
                if (Math.Max(a, Math.Max(b, c)) - Math.Min(a, Math.Min(b, c)) <= spread) kept.AddRange([T[t], T[t + 1], T[t + 2]]); else cut++;
            }
            if (cut > 0) { m["triangles"] = TriRows(kept); log($"{Name(m)}: {cut} triangles across the lips left out"); }
        }
    }

    // ---------------------------------------------------------------- skin under the gear
    List<JsonObject> GearOf(JsonArray ms, HashSet<string> skip) =>
        ms.Select(m => m!.AsObject()).Where(m => !NotGearPrefix.Any(p => Name(m).StartsWith(p)) && !skip.Contains(Name(m))
            && KindOf(m) is not ("eye" or "lash" or "hair") && m["triangles"]!.AsArray().Count > 0).ToList();

    /// <summary>_closest_on for one point against triangles (A, B, C rows at idx) -&gt; points, edge (-1 inside)</summary>
    static (double[] Q, int[] Edge) ClosestOn(double[] p, double[] A, double[] B, double[] C, int[] idx)
    {
        int k = idx.Length;
        var q = new double[3 * k]; var edge = new int[k];
        for (int r = 0; r < k; r++)
        {
            int i = 3 * idx[r];
            double[] ab = [B[i] - A[i], B[i + 1] - A[i + 1], B[i + 2] - A[i + 2]], ac = [C[i] - A[i], C[i + 1] - A[i + 1], C[i + 2] - A[i + 2]];
            var n = Cross(ab, 0, ac, 0);
            double nn = Math.Max(RowSum3(n[0] * n[0], n[1] * n[1], n[2] * n[2]), 1e-20);
            double t = RowSum3((p[0] - A[i]) * n[0], (p[1] - A[i + 1]) * n[1], (p[2] - A[i + 2]) * n[2]) / nn;
            double[] qq = [p[0] - n[0] * t, p[1] - n[1] * t, p[2] - n[2] * t];
            double[] v2 = [qq[0] - A[i], qq[1] - A[i + 1], qq[2] - A[i + 2]];
            double d00 = RowSum3(ab[0] * ab[0], ab[1] * ab[1], ab[2] * ab[2]), d01 = RowSum3(ab[0] * ac[0], ab[1] * ac[1], ab[2] * ac[2]);
            double d11 = RowSum3(ac[0] * ac[0], ac[1] * ac[1], ac[2] * ac[2]);
            double d20 = RowSum3(v2[0] * ab[0], v2[1] * ab[1], v2[2] * ab[2]), d21 = RowSum3(v2[0] * ac[0], v2[1] * ac[1], v2[2] * ac[2]);
            double den = Math.Max(d00 * d11 - d01 * d01, 1e-20);
            double bv = (d11 * d20 - d01 * d21) / den, bw = (d00 * d21 - d01 * d20) / den;
            bool inside = bv >= 0 && bw >= 0 && bv + bw <= 1;
            edge[r] = -1;
            for (int c = 0; c < 3; c++) q[3 * r + c] = qq[c];
            if (inside) continue;
            double best = double.PositiveInfinity;
            for (int s = 0; s < 3; s++)
            {
                double[] P0 = s == 0 ? [A[i], A[i + 1], A[i + 2]] : s == 1 ? [B[i], B[i + 1], B[i + 2]] : [C[i], C[i + 1], C[i + 2]];
                double[] P1 = s == 0 ? [B[i], B[i + 1], B[i + 2]] : s == 1 ? [C[i], C[i + 1], C[i + 2]] : [A[i], A[i + 1], A[i + 2]];
                double[] dd = [P1[0] - P0[0], P1[1] - P0[1], P1[2] - P0[2]];
                double tt = Math.Clamp(RowSum3((p[0] - P0[0]) * dd[0], (p[1] - P0[1]) * dd[1], (p[2] - P0[2]) * dd[2]) / Math.Max(RowSum3(dd[0] * dd[0], dd[1] * dd[1], dd[2] * dd[2]), 1e-20), 0, 1);
                double[] cp = [P0[0] + dd[0] * tt, P0[1] + dd[1] * tt, P0[2] + dd[2] * tt];
                double dist = RowSum3((cp[0] - p[0]) * (cp[0] - p[0]), (cp[1] - p[1]) * (cp[1] - p[1]), (cp[2] - p[2]) * (cp[2] - p[2]));
                if (dist < best) { best = dist; edge[r] = s; for (int c = 0; c < 3; c++) q[3 * r + c] = cp[c]; }
            }
        }
        return (q, edge);
    }

    public int TuckSkin(JsonObject spec, HashSet<string> skip, Action<string> log)
    {
        var ms = Meshes(spec);
        var skin = ms.Select(m => m!.AsObject()).Where(m => Name(m).StartsWith("M_Body")).ToList();
        var gear = GearOf(ms, skip);
        if (skin.Count == 0 || gear.Count == 0) return 0;
        var (margin, deep) = Tuck;
        var hd = ms.Select(m => m!.AsObject()).Where(m => Name(m).StartsWith("M_Head") && m["positions"]!.AsArray().Count > 0).ToList();
        if (hd.Count == 0) hd = skin;
        var HP = hd.SelectMany(m => Flat(m, "positions", 3)).ToArray();
        var HN = hd.SelectMany(m => Flat(m, "normals", 3)).ToArray();
        var hm = Mean0(HP);
        int nh = HP.Length / 3, outward = 0;
        for (int i = 0; i < nh; i++)
            if (RowSum3((HP[3 * i] - hm[0]) * HN[3 * i], (HP[3 * i + 1] - hm[1]) * HN[3 * i + 1], (HP[3 * i + 2] - hm[2]) * HN[3 * i + 2]) > 0) outward++;
        double outSign = (double)outward / nh > 0.5 ? 1.0 : -1.0;
        var headMid = hm;
        var Al = new List<double>(); var Bl = new List<double>(); var Cl = new List<double>(); var GNl = new List<double>(); var TIl = new List<int>();
        int bse = 0;
        foreach (var g in gear)
        {
            var P = Flat(g, "positions", 3); var T = Tris(g); var N = Flat(g, "normals", 3);
            for (int t = 0; t < T.Length; t += 3)
            {
                for (int c = 0; c < 3; c++) { Al.Add(P[3 * T[t] + c]); Bl.Add(P[3 * T[t + 1] + c]); Cl.Add(P[3 * T[t + 2] + c]); }
                TIl.AddRange([T[t] + bse, T[t + 1] + bse, T[t + 2] + bse]);
                double[] n = new double[3];
                for (int c = 0; c < 3; c++) n[c] = (N[3 * T[t] + c] + N[3 * T[t + 1] + c]) + N[3 * T[t + 2] + c];
                double len = Math.Max(Norm3Seq(n[0], n[1], n[2]), 1e-12);
                for (int c = 0; c < 3; c++) GNl.Add(outSign * n[c] / len);
            }
            bse += P.Length / 3;
        }
        double[] A = [.. Al], B = [.. Bl], C = [.. Cl], GN = [.. GNl];
        int[] TI = [.. TIl];
        int ntr = TI.Length / 3;
        var e1 = new double[3 * ntr]; var e2 = new double[3 * ntr];
        for (int i = 0; i < 3 * ntr; i++) { e1[i] = B[i] - A[i]; e2[i] = C[i] - A[i]; }
        var ecount = new Dictionary<(int, int), int>();
        (int, int) Ek(int a, int b) => a < b ? (a, b) : (b, a);
        for (int t = 0; t < ntr; t++)
            foreach (var (a, b) in new[] { (0, 1), (1, 2), (2, 0) })
            { var k = Ek(TI[3 * t + a], TI[3 * t + b]); ecount[k] = ecount.GetValueOrDefault(k) + 1; }
        var triOpen = new bool[ntr, 3];
        for (int t = 0; t < ntr; t++)
        {
            int q = 0;
            foreach (var (a, b) in new[] { (0, 1), (1, 2), (2, 0) }) triOpen[t, q++] = ecount[Ek(TI[3 * t + a], TI[3 * t + b])] == 1;
        }
        const double cell = 0.25;
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < ntr; i++)
        {
            var lo = new int[3]; var hi = new int[3];
            for (int c = 0; c < 3; c++)
            {
                lo[c] = (int)Math.Floor((Math.Min(Math.Min(A[3 * i + c], B[3 * i + c]), C[3 * i + c]) - deep) / cell);
                hi[c] = (int)Math.Floor((Math.Max(Math.Max(A[3 * i + c], B[3 * i + c]), C[3 * i + c]) + deep) / cell);
            }
            for (int x = lo[0]; x <= hi[0]; x++)
                for (int y = lo[1]; y <= hi[1]; y++)
                    for (int z = lo[2]; z <= hi[2]; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var l)) grid[(x, y, z)] = l = [];
                        l.Add(i);
                    }
        }
        (int, int, int) Key(double[] P, int v) => ((int)Math.Floor(P[3 * v] / cell), (int)Math.Floor(P[3 * v + 1] / cell), (int)Math.Floor(P[3 * v + 2] / cell));
        // (distance from a point to the nearest open edge of the gear triangles in its cell list)
        double HemDist(double x, double y, double z, List<int> cl)
        {
            double best = double.PositiveInfinity;
            foreach (int t in cl)
                for (int e = 0; e < 3; e++)
                {
                    if (!triOpen[t, e]) continue;
                    double[] P0 = e == 0 ? A : e == 1 ? B : C, P1 = e == 0 ? B : e == 1 ? C : A;
                    int i = 3 * t;
                    double dx = P1[i] - P0[i], dy = P1[i + 1] - P0[i + 1], dz = P1[i + 2] - P0[i + 2];
                    double ll = dx * dx + dy * dy + dz * dz;
                    double tt = ll > 1e-20 ? Math.Clamp(((x - P0[i]) * dx + (y - P0[i + 1]) * dy + (z - P0[i + 2]) * dz) / ll, 0, 1) : 0;
                    double ex = P0[i] + dx * tt - x, ey = P0[i + 1] + dy * tt - y, ez = P0[i + 2] + dz * tt - z;
                    best = Math.Min(best, ex * ex + ey * ey + ez * ez);
                }
            return Math.Sqrt(best);
        }
        int total = 0;
        foreach (var m in skin)
        {
            var P = Flat(m, "positions", 3);
            var N = Flat(m, "normals", 3);
            int np_ = P.Length / 3;
            for (int v = 0; v < np_; v++)
            {
                double len = Math.Max(Norm3Seq(N[3 * v], N[3 * v + 1], N[3 * v + 2]), 1e-12);
                for (int c = 0; c < 3; c++) N[3 * v + c] = outSign * N[3 * v + c] / len;
            }
            int moved = 0;
            var keys = Enumerable.Range(0, np_).Select(v => Key(P, v)).ToArray();
            for (int v = 0; v < np_; v++)
            {
                if (!grid.TryGetValue(keys[v], out var cl)) continue;
                double[] d = [N[3 * v], N[3 * v + 1], N[3 * v + 2]];
                int k = cl.Count;
                var same = new bool[k];
                bool anySame = false;
                for (int r = 0; r < k; r++) { same[r] = RowDot(GN, 3 * cl[r], d) > 0.3; anySame |= same[r]; }
                if (!anySame) continue;
                var on = new bool[k]; var tv = new double[k];
                for (int r = 0; r < k; r++)
                {
                    int i = 3 * cl[r];
                    var h = Cross(d, 0, e2, i);
                    double a = Ein(e1, i, h, 0);
                    bool ok = Math.Abs(a) > 1e-12;
                    double f = ok ? 1.0 / a : 0;
                    double[] s = [P[3 * v] - A[i], P[3 * v + 1] - A[i + 1], P[3 * v + 2] - A[i + 2]];
                    double u = f * Ein(s, 0, h, 0);
                    var q = Cross(s, 0, e1, i);
                    double w = f * RowDot(q, 0, d);
                    tv[r] = f * Ein(e2, i, q, 0);
                    on[r] = ok && u >= -1e-6 && w >= -1e-6 && u + w <= 1 + 1e-6;
                }
                double tmin = double.PositiveInfinity; bool near = false;
                // (a hit within HemCm of the gear's open edge - a hem, a leg hole, a strap's side - moves no skin: just past the
                //  hem the skin is outside the gear, and pushing it in left a step / slit there - Andre the Giant 2017, 2026-11-12)
                bool AtHem(int r) => HemDist(P[3 * v] + d[0] * tv[r], P[3 * v + 1] + d[1] * tv[r], P[3 * v + 2] + d[2] * tv[r], cl) < HemCm;
                for (int r = 0; r < k; r++) if (on[r] && same[r] && tv[r] > -Math.Min(Shallow, SkinOverGear) && tv[r] < margin && !AtHem(r)) { near = true; tmin = Math.Min(tmin, tv[r]); }
                if (near)
                {
                    for (int c = 0; c < 3; c++) P[3 * v + c] -= d[c] * (margin - tmin);
                    moved++;
                    continue;
                }
                // (np.argmax over t where inner, -inf elsewhere: the first largest)
                int kb = -1; double tb = double.NegativeInfinity;
                for (int r = 0; r < k; r++)
                    if (on[r] && tv[r] < 0 && tv[r] > -Math.Min(deep, SkinOverGear) && (kb < 0 || tv[r] > tb)) { tb = tv[r]; kb = r; }
                if (kb < 0) continue;
                if (same[kb] && !AtHem(kb))
                {
                    for (int c = 0; c < 3; c++) P[3 * v + c] -= d[c] * (margin - tv[kb]);
                    moved++;
                }
            }
            bool isHead = Name(m).StartsWith("M_Head");
            keys = Enumerable.Range(0, np_).Select(v => Key(P, v)).ToArray();
            for (int v = 0; v < np_; v++)
            {
                if (!grid.TryGetValue(keys[v], out var cl)) continue;
                double[] pv = [P[3 * v], P[3 * v + 1], P[3 * v + 2]];
                int[] c2;
                if (isHead)
                {
                    double[] od = [pv[0] - headMid[0], pv[1] - headMid[1], pv[2] - headMid[2]];
                    double ol = Math.Max(Blas.Norm(od), 1e-9);
                    double[] odn = [od[0] / ol, od[1] / ol, od[2] / ol];
                    c2 = cl.Where(i => RowDot(GN, 3 * i, odn) > 0.3).ToArray();
                }
                else
                {
                    double[] nv = [N[3 * v], N[3 * v + 1], N[3 * v + 2]];
                    c2 = cl.Where(i => RowDot(GN, 3 * i, nv) > 0.3).ToArray();
                }
                if (c2.Length == 0) continue;
                var (q, edge) = ClosestOn(pv, A, B, C, c2);
                int kb = 0; double db = double.PositiveInfinity;
                for (int r = 0; r < c2.Length; r++)
                {
                    double dd = Norm3Seq(q[3 * r] - pv[0], q[3 * r + 1] - pv[1], q[3 * r + 2] - pv[2]);
                    if (dd < db) { db = dd; kb = r; }
                }
                if (db > 0.30 || (edge[kb] >= 0 && triOpen[c2[kb], edge[kb]])) continue;
                if (HemDist(q[3 * kb], q[3 * kb + 1], q[3 * kb + 2], cl) < HemCm) continue;   // (by a hem: as above)
                double sd = Blas.Dot(new[] { pv[0] - q[3 * kb], pv[1] - q[3 * kb + 1], pv[2] - q[3 * kb + 2] }, new[] { GN[3 * c2[kb]], GN[3 * c2[kb] + 1], GN[3 * c2[kb] + 2] });
                if (-margin < sd && sd < (isHead ? 0.35 : 0.15))
                {
                    for (int c = 0; c < 3; c++) P[3 * v + c] -= GN[3 * c2[kb] + c] * (sd + margin);
                    moved++;
                }
            }
            if (moved > 0)
            {
                m["positions"] = Rows(P, 3, 6);
                total += moved;
                log($"{Name(m)}: {moved} vertices under the gear moved in below it");
            }
        }
        return total;
    }

    public int HideHeadUnder(JsonObject spec, HashSet<string> skip, Action<string> log)
    {
        var ms = Meshes(spec);
        var heads = ms.Select(m => m!.AsObject()).Where(m => Name(m).StartsWith("M_Head") && m["triangles"]!.AsArray().Count > 0).ToList();
        var gear = GearOf(ms, skip);
        if (heads.Count == 0 || gear.Count == 0) return 0;
        var HP = heads.SelectMany(m => Flat(m, "positions", 3)).ToArray();
        var mid = Mean0(HP);
        int nh = HP.Length / 3;
        var lo = new double[3]; var hi = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
            for (int i = 0; i < nh; i++) { mn = Math.Min(mn, HP[3 * i + c]); mx = Math.Max(mx, HP[3 * i + c]); }
            lo[c] = mn - 0.3; hi[c] = mx + 0.3;
        }
        var dists = Enumerable.Range(0, nh).Select(i => Norm3Seq(HP[3 * i] - mid[0], HP[3 * i + 1] - mid[1], HP[3 * i + 2] - mid[2])).ToArray();
        double reach = Face.Percentile(dists, 60) * 1.35;
        gear = gear.Where(g =>
        {
            var P = Flat(g, "positions", 3); int n = P.Length / 3, inn = 0;
            for (int i = 0; i < n; i++) if (Norm3Seq(P[3 * i] - mid[0], P[3 * i + 1] - mid[1], P[3 * i + 2] - mid[2]) < reach) inn++;
            return (double)inn / n > 0.5;
        }).ToList();
        if (gear.Count == 0) return 0;
        var Al = new List<double>(); var Bl = new List<double>(); var Cl = new List<double>();
        foreach (var g in gear)
        {
            var P = Flat(g, "positions", 3); var T = Tris(g);
            for (int t = 0; t < T.Length; t += 3)
            {
                bool near = true;
                for (int c = 0; c < 3 && near; c++)
                {
                    double a = P[3 * T[t] + c], b = P[3 * T[t + 1] + c], cc = P[3 * T[t + 2] + c];
                    near = Math.Max(Math.Max(a, b), cc) >= lo[c] && Math.Min(Math.Min(a, b), cc) <= hi[c];
                }
                if (!near) continue;
                for (int c = 0; c < 3; c++) { Al.Add(P[3 * T[t] + c]); Bl.Add(P[3 * T[t + 1] + c]); Cl.Add(P[3 * T[t + 2] + c]); }
            }
        }
        if (Al.Count == 0) return 0;
        double[] A = [.. Al], B = [.. Bl], C = [.. Cl];
        int ntr = A.Length / 3;
        var e1 = new double[A.Length]; var e2 = new double[A.Length]; var sA = new double[A.Length];
        for (int i = 0; i < A.Length; i++) { e1[i] = B[i] - A[i]; e2[i] = C[i] - A[i]; sA[i] = mid[i % 3] - A[i]; }
        var qs = new double[A.Length];
        for (int r = 0; r < ntr; r++) { var q = Cross(sA, 3 * r, e1, 3 * r); qs[3 * r] = q[0]; qs[3 * r + 1] = q[1]; qs[3 * r + 2] = q[2]; }
        var tq = new double[ntr];
        for (int r = 0; r < ntr; r++) tq[r] = Ein(e2, 3 * r, qs, 3 * r);
        int total = 0;
        foreach (var m in heads)
        {
            var P = Flat(m, "positions", 3);
            int np_ = P.Length / 3;
            var cov = new bool[np_];
            Parallel.For(0, np_, v =>
            {
                double[] d = [P[3 * v] - mid[0], P[3 * v + 1] - mid[1], P[3 * v + 2] - mid[2]];
                double L = Blas.Norm(d);
                if (L < 1e-6) return;
                d = [d[0] / L, d[1] / L, d[2] / L];
                if (!(L < reach)) return;
                for (int r = 0; r < ntr; r++)
                {
                    int i = 3 * r;
                    var h = Cross(d, 0, e2, i);
                    double a = Ein(e1, i, h, 0);
                    bool ok = Math.Abs(a) > 1e-12;
                    if (!ok) continue;
                    double f = 1.0 / a;
                    double u = f * Ein(sA, i, h, 0);
                    double w = f * RowDot(qs, i, d);
                    double t = f * tq[r];
                    if (u >= 0 && w >= 0 && u + w <= 1 && t > 0.7 * L && t < L + 0.15) { cov[v] = true; return; }
                }
            });
            var T = Tris(m);
            int nt = T.Length / 3, keptN = 0;
            var keep = new bool[nt];
            for (int t = 0; t < nt; t++) { keep[t] = !(cov[T[3 * t]] && cov[T[3 * t + 1]] && cov[T[3 * t + 2]]); if (keep[t]) keptN++; }
            if (keptN == nt) continue;
            var usedB = new bool[np_];
            for (int t = 0; t < nt; t++) if (keep[t]) { usedB[T[3 * t]] = usedB[T[3 * t + 1]] = usedB[T[3 * t + 2]] = true; }
            var used = Enumerable.Range(0, np_).Where(i => usedB[i]).ToArray();
            var remap = new int[np_]; Array.Fill(remap, -1);
            for (int i = 0; i < used.Length; i++) remap[used[i]] = i;
            Keep(m, used);
            var kt = new List<int>();
            for (int t = 0; t < nt; t++) if (keep[t]) kt.AddRange([remap[T[3 * t]], remap[T[3 * t + 1]], remap[T[3 * t + 2]]]);
            m["triangles"] = TriRows(kt);
            total += nt - keptN;
            log($"{Name(m)} under the gear removed: {np_ - used.Length} of {np_} vertices, {nt - keptN} of {nt} triangles");
        }
        var keepM = ms.Select(m => m!.AsObject()).Where(m => !(Name(m).StartsWith("M_Head") && m["triangles"]!.AsArray().Count == 0)).Select(m => m.DeepClone()).ToArray();
        spec["attires"]![0]!["meshes"] = new JsonArray(keepM);
        return total;
    }

    /// <summary>fix_model: the weight fixes on a rock_model model.json (in place), then oversized meshes split</summary>
    public void FixModel(string path, IReadOnlyList<string> cardNames, string template, Action<string> log)
    {
        var spec = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var conv = new SvrBuilder.Template(template);
        var face = CardWeights(spec, cardNames, conv, log);
        if (face.Count > 0) MouthCards(spec, face, log);
        var skip = cardNames.ToHashSet();
        GearWeights(spec, skip, log);
        HideHeadUnder(spec, skip, log);
        TuckSkin(spec, skip, log);
        if (GearHead) InnerBridges(spec, log);
        SplitBig(spec, log);
        File.WriteAllText(path, spec.ToJsonString());
    }

    // ---------------------------------------------------------------- the built ch.pac against the game's limits
    public int SafeIndices = 24000, StockMeshes = 33, PartPages = 1900;
    public const double Bend = 0.52;
    public static readonly HashSet<string> BodyBones = ["atama", "kubi", "kubi_x", "mune", "root", "koshi", "l_eye", "r_eye"];

    static IEnumerable<(string Name, uint Part, Formats.Jboy Model)> Models(string pac)
    {
        foreach (var g in Formats.Epk8.Read(File.ReadAllBytes(pac)).Groups)
        {
            if (g.TypeText != "EMD ") continue;
            foreach (var e in g.Entries)
            {
                string nm = e.NameText.Trim('\0', ' ');
                if (!nm.EndsWith('2')) continue;
                foreach (var x in Formats.Pach.Read(Formats.Bpe.Unpack(e.Data)))
                {
                    var raw = Formats.Bpe.Unpack(x.Data);
                    if (!Formats.Jboy.IsJboy(raw)) continue;
                    yield return (nm, x.Id, Formats.Jboy.Read(raw));
                }
            }
        }
    }

    /// <summary>-&gt; problems (each a crash in game): a mesh over 65535 vertices / indices, a part over PART_PAGES, the model
    /// over its vertex window</summary>
    public List<string> PacLimits(string pac, int windowPages = 0)
    {
        var probs = new List<string>();
        var totals = new List<(string, int)>();
        foreach (var grp in Models(pac).GroupBy(x => x.Name))
        {
            int total = 0;
            foreach (var (nm, i, model) in grp)
            {
                int pages = 0;
                for (int k = 0; k < model.Meshes.Count; k++)
                {
                    var s = model.Meshes[k];
                    long ni = s.Strips.Sum(st => (long)st.Idx.Length);
                    if (s.Verts.Count > 65535 || ni > 65535) probs.Add($"{nm} part 0x{i:x} mesh {k}: {s.Verts.Count} vertices, {ni} indices (the game takes 65535)");
                    pages += Budget.MeshPages(s);
                }
                if (pages > PartPages) probs.Add($"{nm} part 0x{i:x}: {pages} pages (one part over 2048 crashes the draw)");
                total += pages;
            }
            if (windowPages > 0 && total > windowPages) probs.Add($"{grp.Key}: {total} pages, the vertex window takes {windowPages}");
        }
        return probs;
    }

    /// <summary>(vertices, indices) of the built meshes over SAFE_INDICES, and the largest mesh count of a part</summary>
    public (List<(int V, long I)> Heavy, int Most) HeavyMeshes(string pac)
    {
        var o = new List<(int, long)>(); int most = 0;
        foreach (var (_, _, model) in Models(pac))
        {
            most = Math.Max(most, model.Meshes.Count);
            foreach (var s in model.Meshes)
            {
                long ni = s.Strips.Sum(st => (long)st.Idx.Length);
                if (ni > SafeIndices) o.Add((s.Verts.Count, ni));
            }
        }
        return (o, most);
    }

    // ---------------------------------------------------------------- checks
    /// <summary>-&gt; (face_over, gear_over, notes): face vertices swinging past the stock head's levers; gear drifting from
    /// the skin more than the body's own neighbours (99 %). (Python sums a drift over a set of bone names - its order
    /// changes every run; here the names in order.)</summary>
    public (int FaceOver, int GearOver, List<string> Notes) Checks(string path, string template)
    {
        var spec = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var ms = Meshes(spec);
        var J = spec["joints"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(D).ToArray());
        var (lim, scale) = LeverLimits(new SvrBuilder.Template(template), spec["joints"]!.AsObject());
        var notes = new List<string>(); int faceOver = 0;
        static double Dist(double[] P, int v, double[] q) => Blas.Norm(new[] { P[3 * v] - q[0], P[3 * v + 1] - q[1], P[3 * v + 2] - q[2] });
        foreach (var mn in ms)
        {
            var m = mn!;
            var P = Flat(m, "positions", 3);
            var bn = Bones(m);
            int bad = 0; double worst = 0.0;
            var wsAll = m["weights"]!.AsArray();
            for (int v = 0; v < wsAll.Count; v++)
            {
                var ws = wsAll[v]!.AsArray();
                if (ws.Count > 4) notes.Add($"{Name(m)}: a vertex with {ws.Count} weights");
                foreach (var x in ws)
                {
                    string b = bn[x![0]!.GetValue<int>()]; double w = D(x[1]);
                    if (BodyBones.Contains(b) || w <= 0.01 || !lim.ContainsKey(b) || !J.ContainsKey(b)) continue;
                    double r = w * Dist(P, v, J[b]) / (lim[b] * scale);
                    if (r > 1.25) { bad++; worst = Math.Max(worst, r); }
                }
            }
            if (bad > 0) notes.Add($"{Name(m)}: {bad} face-bone weights swing past the stock head (worst {worst.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}x)");
            faceOver += bad;
        }
        var body = ms.Select(m => m!).Where(m => Name(m).StartsWith("M_Body") || (GearHead && Name(m).StartsWith("M_Head"))).ToList();
        var BP = body.SelectMany(m => Flat(m, "positions", 3)).ToArray();
        var BW = body.SelectMany(Weights).Select(e => e.GroupBy(x => x.B).ToDictionary(g => g.Key, g => g.Last().W)).ToList();
        double Drift(double[] P, int v, Dictionary<string, double> wa, Dictionary<string, double> wb)
        {
            double s = 0;
            foreach (var b in wa.Keys.Union(wb.Keys).Order(StringComparer.Ordinal))
                if (J.TryGetValue(b, out var jb)) s += Math.Abs(wa.GetValueOrDefault(b, 0.0) - wb.GetValueOrDefault(b, 0.0)) * Dist(P, v, jb);
            return s;
        }
        int nb = BP.Length / 3;
        var own = new List<double>();
        double near0 = GearSkin.Hi * 0.3, near1 = GearSkin.Hi * 0.6;
        foreach (long i in NpRandom.Seed1().ChooseIndices(nb, Math.Min(1500, nb)))
        {
            int c0 = -1;
            for (int q = 0; q < nb && c0 < 0; q++)
            {
                double d = Norm3Seq(BP[3 * q] - BP[3 * i], BP[3 * q + 1] - BP[3 * i + 1], BP[3 * q + 2] - BP[3 * i + 2]);
                if (d > near0 && d < near1) c0 = q;
            }
            if (c0 >= 0) own.Add(Drift(BP, (int)i, BW[(int)i], BW[c0]));
        }
        double limit = own.Count > 0 ? Face.Percentile(own.ToArray(), 99) : 1.0;
        int gearOver = 0;
        string[] skipPre = ["M_Body", "M_Head", "M_EYE", "M_TransB", "M_MouthBag", "mouth_", "M_AEWMouth"];
        foreach (var mn in ms)
        {
            var m = mn!;
            if (skipPre.Any(p => Name(m).StartsWith(p)) || KindOf(m) is "eye" or "lash" or "hair") continue;
            var P = Flat(m, "positions", 3);
            var W = Weights(m).Select(e => e.GroupBy(x => x.B).ToDictionary(g => g.Key, g => g.Last().W)).ToList();
            var (j, d2) = Nearest(P, BP);
            int bad = 0;
            for (int v = 0; v < P.Length / 3; v++)
            {
                double gap = Math.Sqrt(d2[v]);
                if (gap <= GearSkin.Hi)
                {
                    double dr = Drift(P, v, W[v], BW[j[v]]);
                    if (dr > limit && dr * Bend > gap + 0.02) bad++;
                }
            }
            if (bad > 0) notes.Add($"{Name(m)}: {bad} vertices drift from the skin under them");
            gearOver += bad;
        }
        return (faceOver, gearOver, notes);
    }

    // ---------------------------------------------------------------- the full route's sheet and texture pack
    public static readonly (int W, int H) FullSheet = (8192, 4096);
    public static readonly (int W, int H) MouthBlock = (256, 128);
    public int PackSize = 8192;

    /// <summary>the 8192 x 4096 sheet's cells: head / body at full size, the rest packed at their source size (halved until
    /// all fit; skip: textures of left-out meshes); the template's mouth block at the top right's bottom</summary>
    public Dictionary<string, (int X, int Y, int W, int H)> Layout(RockModel R, string chr, string attireDir, ISet<string> skip, Action<string> log)
    {
        var (W_, H_) = FullSheet;
        var cells = new Dictionary<string, (int X, int Y, int W, int H)> { ["head"] = (0, 0, 2048, 2048), ["body"] = (2048, 0, 4096, 2048) };
        int mx = 6144 + 2048 - MouthBlock.W, my = 2048 - MouthBlock.H;
        var want = new List<(string T, int W, int H)>();
        foreach (var t in R.Cells.Keys)
        {
            if (cells.ContainsKey(t) || t == "mouthbag" || skip.Contains(t)) continue;
            var src = R.FindTex(chr, attireDir, t, "_color") ?? R.FindTex(chr, attireDir, System.Text.RegularExpressions.Regex.Replace(t, "(?i)_alpha$", ""), "_rai") ?? R.FindTex(chr, attireDir, t, "");
            int w = 256, h = 256;
            if (src != null)
                try { var im = Imaging.ImageIO.Load(src); (w, h) = (im.W, im.H); } catch (Exception) { (w, h) = (256, 256); }
            want.Add((t, Math.Min(w, 4096), Math.Min(h, 2048)));
        }
        bool done = false;
        for (int shrink = 0; shrink < 6 && !done; shrink++)
        {
            var free = new List<(int X, int Y, int W, int H)> { (6144, 0, 2048, my), (6144, my, 2048 - MouthBlock.W, MouthBlock.H), (0, 2048, W_, H_ - 2048) };
            var got = new List<(string, (int, int, int, int))>(); bool ok = true;
            foreach (var (t, w0, h0) in want.OrderBy(x => -(long)x.W * x.H))
            {
                int w = Math.Max(64, w0 >> shrink), h = Math.Max(64, h0 >> shrink);
                free = free.OrderBy(r => (long)r.W * r.H).ToList();
                int k = free.FindIndex(r => r.W >= w && r.H >= h);
                if (k < 0) { ok = false; break; }
                var (x, y, fw, fh) = free[k];
                free.RemoveAt(k);
                got.Add((t, (x, y, w, h)));
                if (fw - w > 0) free.Add((x + w, y, fw - w, h));
                if (fh - h > 0) free.Add((x, y + h, fw, fh - h));
            }
            if (ok)
            {
                foreach (var (t, c) in got) cells[t] = c;
                if (shrink > 0) log($"texture sheet: the smaller parts at 1/{1 << shrink} of their 2K size (room)");
                done = true;
            }
        }
        if (!done) throw new InvalidOperationException("texture sheet: the parts do not fit");
        var boxes = new List<(string, (int X, int Y, int W, int H))>(cells.Select(kv => (kv.Key, kv.Value))) { ("_mouth", (mx, my, MouthBlock.W, MouthBlock.H)) };
        for (int i = 0; i < boxes.Count; i++)
        {
            var (a, (x, y, w, h)) = boxes[i];
            if (x < 0 || y < 0 || x + w > W_ || y + h > H_) throw new InvalidOperationException($"texture sheet: {a} off the sheet ({x}, {y}, {w}, {h})");
            for (int j = i + 1; j < boxes.Count; j++)
            {
                var (b, (X, Y, Wb, Hb)) = boxes[j];
                if (x < X + Wb && X < x + w && y < Y + Hb && Y < y + h)
                    throw new InvalidOperationException($"texture sheet: {a} ({x}, {y}, {w}, {h}) overlaps {b} ({X}, {Y}, {Wb}, {Hb})");
            }
        }
        R.Sheet = FullSheet;
        R.Cells = new Dictionary<string, (int, int, int, int)>(cells.Select(kv => KeyValuePair.Create(kv.Key, ((int, int, int, int))kv.Value)));
        R.TmplMouthCell = (mx, my);
        R.Cells["mouthbag"] = (mx + 128 + 16, my + 16, 96, 96);
        // (Python returns R.CELLS itself - the dict its mouthbag cell was just added to)
        return R.Cells.ToDictionary(kv => kv.Key, kv => ((int X, int Y, int W, int H))kv.Value);
    }

    /// <summary>the full sheets (work\color.png / normal.png) -&gt; out_dir\&lt;prefix&gt;_&lt;color|normal&gt;_&lt;hash&gt;.dds, at PACK_SIZE</summary>
    public List<string> MakePack(string work, string pac, string outDir, string prefix, IReadOnlyList<(int X, int Y, int W, int H)> cutCells, Action<string> log)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.EnumerateFiles(outDir))
        {
            string n = Path.GetFileName(f);
            if (n.StartsWith(prefix + "_") && n.ToLowerInvariant().EndsWith(".dds")) File.Delete(f);
        }
        var hashes = TexturePackHash.PacTextureHashes(File.ReadAllBytes(pac));
        var files = new List<string>();
        foreach (var nm in new[] { "color", "normal" })
        {
            var im = Imaging.ImageIO.Load(Path.Combine(work, nm + ".png")).ToRgba();
            var cells = nm == "color" ? cutCells.ToList() : [];
            int w = PackSize;
            if (im.W > w)
            {
                double k = (double)w / im.W;
                int h = (int)(im.H * k);
                if (nm == "normal")
                {
                    var rgb = Imaging.Resample.Resize(im.ToRgb(), w, h, Imaging.Filter.Lanczos);
                    im = rgb.WithAlpha(Imaging.Resample.Resize(im.Channel(3), w, h, Imaging.Filter.Lanczos));
                }
                else im = Imaging.Resample.ResizeApart(im, w, h, Imaging.Filter.Lanczos);
                cells = cells.Select(c => ((int)Math.Round(c.X * k, MidpointRounding.ToEven), (int)Math.Round(c.Y * k, MidpointRounding.ToEven),
                                           (int)Math.Round(c.W * k, MidpointRounding.ToEven), (int)Math.Round(c.H * k, MidpointRounding.ToEven))).ToList();
            }
            string f = Path.Combine(outDir, $"{prefix}_{nm}_{hashes[nm]}.dds");
            File.WriteAllBytes(f, Imaging.DdsWriter.Levels(im, "DXT5", cells));
            files.Add(f);
            log($"texture pack: {Path.GetFileName(f)} ({im.W} x {im.H}, {(new FileInfo(f).Length / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} MB)");
        }
        return files;
    }

    /// <summary>pages a wrestler's model may take in this game: its vertex window (the DLL's bigmodel.log) / 4 KB less a
    /// margin; no log: the classic 768 KB window</summary>
    public static int WindowPages(string game, Action<string> log)
    {
        int kb = 768;
        try
        {
            var m = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(game, "bigmodel.log")), @"vertex windows[^:]*: *\d+ *-> *(\d+) *KB");
            if (m.Count > 0) kb = int.Parse(m[^1].Groups[1].Value);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        int pages = kb / 4 - 24;
        log($"vertex window: {kb} KB a wrestler -> models up to {pages} pages{(kb > 768 ? "" : " (no big-model DLL found: install it for full-detail ports)")}");
        return pages;
    }

    /// <summary>Python's str.splitlines for \r\n / \n / \r: no last empty line after a final line break</summary>
    static string[] SplitLines(string s)
    {
        if (s.Length == 0) return [];
        var parts = System.Text.RegularExpressions.Regex.Split(s, "\r\n|\n|\r");
        return s.EndsWith('\n') || s.EndsWith('\r') ? parts[..^1] : parts;
    }

    /// <summary>the first texture pack enabled in the game's svr2011.toml (native_texture_packs), or null</summary>
    public static string? EnabledPack(string game)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(game, "svr2011.toml")))
            {
                int eq = line.IndexOf('=');
                string k = eq < 0 ? line : line[..eq];
                if (k.Trim() != "native_texture_packs") continue;
                string v = eq < 0 ? "" : line[(eq + 1)..].Trim().Trim('"');
                var first = v.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                return first.Count > 0 ? first[0] : null;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>turn the texture pack `name` on in svr2011.toml (that one line changed or added, every other line kept; the
    /// first change keeps svr2011.toml.before_portit) -&gt; true when it is on</summary>
    public static bool EnablePack(string game, string name)
    {
        string path = Path.Combine(game, "svr2011.toml");
        List<string> lines;
        try { lines = SplitLines(File.ReadAllText(path)).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { lines = ["# WWE SmackDown vs. Raw 2011 - settings (the launcher rewrites this file)"]; }
        int? at = null; var packs = new List<string>(); int section = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith('[')) { section = i; break; }
            int eq = lines[i].IndexOf('=');
            if (eq >= 0 && lines[i][..eq].Trim() == "native_texture_packs")
            {
                at = i;
                packs = lines[i][(eq + 1)..].Trim().Trim('"').Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            }
        }
        if (packs.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase))) return true;
        packs.Add(name);
        string line = $"native_texture_packs = \"{string.Join(";", packs)}\"";
        if (at == null)
        {
            while (section > 0 && lines[section - 1].Trim().Length == 0) section--;
            lines.Insert(section, line);
        }
        else lines[at.Value] = line;
        string keep = path + ".before_portit";
        if (File.Exists(path) && !File.Exists(keep)) File.Copy(path, keep);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
        return EnabledPack(game) != null;
    }
}
