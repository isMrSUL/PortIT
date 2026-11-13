using System.Text.RegularExpressions;

namespace PortIT.Engine.Readers;

/// <summary>
/// A skinned character from a binary FBX: its bones in the bind pose (world positions / matrices), each mesh's
/// vertices welded per (point, normal, uv) in the bind pose's world space, its triangles, its first uv set (v up, as
/// FBX stores it), per-bone weights and the texture file names its material links to. Units: the file's, times
/// Fbx.UnitCm (= cm).
/// </summary>
public static class SourceFbx
{
    public sealed class Bone { public string Name = ""; public int Parent = -1; public double[] World = new double[16]; public bool Skinned; }

    public sealed class Mesh
    {
        public string Name = "", Material = "";
        public List<string> Textures = [];
        public double[] V = [], N = [], Uv = [];
        public int[] Tris = [];
        public Dictionary<int, double[]> W = [];
        public int Count => V.Length / 3;
    }

    public sealed class Scene
    {
        public List<Bone> Bones = [];
        public List<Mesh> Meshes = [];
        public double UnitCm = 1;
    }

    /// <summary>the name without Blender's / the exporter's "bone0123_" style prefix</summary>
    public static string Clean(string n) => Regex.Replace(n, @"^bone\d+_", "");

    public static Scene Load(string path)
    {
        var f = new Fbx(path);
        f.Gather();
        var sc = new Scene { UnitCm = f.UnitCm };
        // bind pose matrices: the poses' nodes, then the clusters' links
        var bind = new Dictionary<long, double[]>();
        foreach (var p in f.Objects.Values.Where(o => o.Kind == "Pose"))
            foreach (var pn in p.Node.All("PoseNode"))
            {
                long id = pn["Node"]!.L(0);
                var m = pn["Matrix"]?.D();
                if (m != null && m.Length == 16) bind.TryAdd(id, m);
            }
        var linkOf = new Dictionary<long, double[]>();
        foreach (var c in f.Objects.Values.Where(o => o.Kind == "Deformer" && o.Sub == "Cluster"))
        {
            var bone = c.In.Select(x => x.O).FirstOrDefault(x => x.Kind == "Model");
            var tl = c.Node["TransformLink"]?.D();
            if (bone != null && tl != null && tl.Length == 16) linkOf.TryAdd(bone.Id, tl);
        }
        // bones: every LimbNode (and the nulls between them), parents first
        var limbs = f.Objects.Values.Where(o => o.Kind == "Model" && o.Sub is "LimbNode" or "Root" or "Limb").ToList();
        var order = new List<Fbx.Obj>();
        var seen = new HashSet<long>();
        void Add(Fbx.Obj o)
        {
            if (seen.Contains(o.Id)) return;
            var p = f.ParentModel(o);
            if (p != null && limbs.Contains(p)) Add(p);
            seen.Add(o.Id); order.Add(o);
        }
        foreach (var o in limbs) Add(o);
        var index = new Dictionary<long, int>();
        // world in the bind pose: the cluster's link, the pose's matrix, else the parent's bind world times the local
        var bw = new Dictionary<long, double[]>();
        double[] BindWorld(Fbx.Obj o)
        {
            if (bw.TryGetValue(o.Id, out var w)) return w;
            if (linkOf.TryGetValue(o.Id, out w) || bind.TryGetValue(o.Id, out w)) { }
            else
            {
                var p = f.ParentModel(o);
                w = p != null ? Fbx.Mul(BindWorld(p), Fbx.Local(o)) : f.World(o);
            }
            bw[o.Id] = w;
            return w;
        }
        foreach (var o in order)
        {
            var p = f.ParentModel(o);
            index[o.Id] = sc.Bones.Count;
            var w = (double[])BindWorld(o).Clone();
            for (int k = 12; k < 15; k++) w[k] *= sc.UnitCm;
            sc.Bones.Add(new Bone { Name = Clean(o.Name), Parent = p != null && index.TryGetValue(p.Id, out var pi) ? pi : -1, World = w, Skinned = linkOf.ContainsKey(o.Id) });
        }

        foreach (var g in f.Objects.Values.Where(o => o.Kind == "Geometry" && o.Sub == "Mesh"))
        {
            var model = g.Out.Select(x => x.O).FirstOrDefault(x => x.Kind == "Model");
            if (model == null) continue;
            var n = g.Node;
            var cp = n["Vertices"]?.D() ?? [];
            var pvi = n["PolygonVertexIndex"]?.I() ?? [];
            if (cp.Length == 0 || pvi.Length == 0) continue;
            int npv = pvi.Length, ncp = cp.Length / 3;
            // mesh -> world in the bind pose
            double[] mw = bind.TryGetValue(model.Id, out var mb) ? mb : f.World(model);
            var clusters = g.In.Select(x => x.O).Where(x => x.Kind == "Deformer" && x.Sub == "Skin")
                .SelectMany(s => s.In.Select(x => x.O).Where(x => x.Kind == "Deformer" && x.Sub == "Cluster")).ToList();
            var c0 = clusters.FirstOrDefault(c => c.Node["Transform"] != null && c.Node["TransformLink"] != null);
            if (c0 != null) mw = Fbx.Mul(c0.Node["TransformLink"]!.D(), c0.Node["Transform"]!.D());
            // per polygon-vertex: point, normal, uv
            var cpOf = new int[npv];
            for (int i = 0; i < npv; i++) cpOf[i] = pvi[i] < 0 ? ~pvi[i] : pvi[i];
            double[] Layer(string name, string data, string idxName, int k)
            {
                var le = n.All(name).FirstOrDefault();
                var o = new double[npv * k];
                if (le == null) return o;
                var d = le[data]?.D() ?? [];
                var ix = le[idxName]?.I();
                string map = le["MappingInformationType"]?.S(0) ?? "", rf = le["ReferenceInformationType"]?.S(0) ?? "";
                for (int i = 0; i < npv; i++)
                {
                    int at = map is "ByVertice" or "ByVertex" ? cpOf[i] : i;
                    if (rf == "IndexToDirect" && ix != null) at = at < ix.Length ? ix[at] : 0;
                    if (at < 0 || k * at + k > d.Length) continue;
                    for (int c = 0; c < k; c++) o[k * i + c] = d[k * at + c];
                }
                return o;
            }
            var nrm = Layer("LayerElementNormal", "Normals", "NormalsIndex", 3);
            var uv = Layer("LayerElementUV", "UV", "UVIndex", 2);
            // weld
            var key = new Dictionary<(int, long, long, long, long, long), int>();
            var vmap = new int[npv];
            var src = new List<int>();
            for (int i = 0; i < npv; i++)
            {
                var k = (cpOf[i], (long)Math.Round(nrm[3 * i] * 1000), (long)Math.Round(nrm[3 * i + 1] * 1000), (long)Math.Round(nrm[3 * i + 2] * 1000),
                         (long)Math.Round(uv[2 * i] * 100000), (long)Math.Round(uv[2 * i + 1] * 100000));
                if (!key.TryGetValue(k, out int v)) { v = src.Count; key[k] = v; src.Add(i); }
                vmap[i] = v;
            }
            int nv = src.Count;
            var me = new Mesh { Name = Clean(model.Name), V = new double[3 * nv], N = new double[3 * nv], Uv = new double[2 * nv] };
            for (int v = 0; v < nv; v++)
            {
                int i = src[v], c = cpOf[i];
                var p = Fbx.Point(mw, cp[3 * c], cp[3 * c + 1], cp[3 * c + 2]);
                var q = Fbx.Dir(mw, nrm[3 * i], nrm[3 * i + 1], nrm[3 * i + 2]);
                double l = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2]);
                if (l > 0) for (int k = 0; k < 3; k++) q[k] /= l;
                for (int k = 0; k < 3; k++) { me.V[3 * v + k] = p[k] * sc.UnitCm; me.N[3 * v + k] = q[k]; }
                me.Uv[2 * v] = uv[2 * i]; me.Uv[2 * v + 1] = uv[2 * i + 1];
            }
            // triangles: fans over each polygon
            var tris = new List<int>();
            int start = 0;
            for (int i = 0; i < npv; i++)
                if (pvi[i] < 0)
                {
                    for (int k = start + 1; k < i; k++) tris.AddRange([vmap[start], vmap[k], vmap[k + 1]]);
                    start = i + 1;
                }
            me.Tris = [.. tris];
            // weights per bone, per welded vertex
            var cpw = new Dictionary<int, double[]>();
            foreach (var c in clusters)
            {
                var bone = c.In.Select(x => x.O).FirstOrDefault(x => x.Kind == "Model");
                if (bone == null || !index.TryGetValue(bone.Id, out int bi)) continue;
                var ix = c.Node["Indexes"]?.I(); var wv = c.Node["Weights"]?.D();
                if (ix == null || wv == null) continue;
                if (!cpw.TryGetValue(bi, out var arr)) cpw[bi] = arr = new double[ncp];
                for (int k = 0; k < ix.Length && k < wv.Length; k++) if (ix[k] >= 0 && ix[k] < ncp) arr[ix[k]] += wv[k];
            }
            foreach (var (bi, arr) in cpw)
            {
                var w = new double[nv];
                bool any = false;
                for (int v = 0; v < nv; v++) { w[v] = arr[cpOf[src[v]]]; any |= w[v] > 0; }
                if (any) me.W[bi] = w;
            }
            // material and textures
            var mat = model.In.Select(x => x.O).FirstOrDefault(x => x.Kind == "Material");
            me.Material = mat?.Name ?? "";
            foreach (var t in mat?.In.Where(x => x.O.Kind == "Texture") ?? [])
            {
                string fn = t.O.Node["RelativeFilename"]?.S(0) is { Length: > 0 } r ? r : t.O.Node["FileName"]?.S(0) ?? "";
                if (fn.Length > 0) me.Textures.Add(t.Prop + "=" + Path.GetFileName(fn.Replace('\\', '/')));
            }
            sc.Meshes.Add(me);
        }
        return sc;
    }
}
