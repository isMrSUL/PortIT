using System.Buffers.Binary;
using System.Text.RegularExpressions;
using PortIT.Engine.Imaging;
using PortIT.Engine.Util;

namespace PortIT.Engine.Readers;

/// <summary>
/// portit/source_aew.py: an AEW Fight Forever wrestler (PS4, UE 4.27 IoStore) read straight from the game's containers -
/// its SkeletalMesh's sections as meshes, each section's material instance's textures (colour, normal, ORM(S), the eyes'
/// pupil layer laid on), the hair's Level applied.
/// </summary>
public static class SourceAew
{
    public sealed class SourceMesh
    {
        public string Name = "", Material = "", Kind = "", Source = "";
        public bool Cutout;
        public double[] Positions = [], Normals = [], Uvs = [];   // n x 3, n x 3, n x 2
        public int[] Tris = [];                                   // n x 3
        public long[] BoneIdx = [];                               // n x MaxInf
        public double[] BoneW = [];                               // n x MaxInf
        public int MaxInf;
        public Dictionary<string, Img> Images = [];
        public Dictionary<string, double> Scalars = [];
        // (2K sources, portit/source_2k.py: texture files by role, vertex colours (n x 4, 0 - 1), alternate looks, the
        //  model file and the mesh's index in it - render_source draws a lower LOD from it)
        public Dictionary<string, string> Textures = [];
        public double[]? Colors;
        public int ColorC;
        public bool Variant;
        public string? SourcePath;
        public int Index;
        public int N => Positions.Length / 3;
    }

    public sealed class Bone { public string Name = ""; public int Parent; public double[] World = new double[16]; }

    public sealed class Model
    {
        public string Game = "aew", Name = "";
        public string? Attire;
        public List<string> Attires = [];
        public List<Bone> Bones = [];
        public List<SourceMesh> Meshes = [];
        public bool Female;
    }

    static readonly Dictionary<string, List<IoContainer>> containers = [];
    static readonly Dictionary<string, List<(IoContainer C, List<(string Low, int I, string Name)> Names)>> lower = [];

    public static List<IoContainer> Containers(string paks)
    {
        lock (containers)
        {
            if (!containers.TryGetValue(paks, out var cs))
            {
                cs = [];
                foreach (var u in Directory.EnumerateFiles(paks, "*.utoc").Order(StringComparer.Ordinal))
                {
                    try { cs.Add(new IoContainer(u)); } catch (Exception) { }
                }
                containers[paks] = cs;
            }
            return cs;
        }
    }

    /// <summary>the package whose path ends with suffix (case ignored)</summary>
    public static (IoContainer C, int Idx, string Name)? Find(string paks, string suffix)
    {
        List<(IoContainer, List<(string, int, string)>)> all;
        lock (lower)
        {
            if (!lower.TryGetValue(paks, out all!))
            {
                all = [];
                foreach (var c in Containers(paks))
                {
                    // (a Python dict by lower-case name: the first place kept, the last entry's value)
                    var pos = new Dictionary<string, int>(); var l = new List<(string, int, string)>();
                    foreach (var (i, n) in c.Names)
                    {
                        string low = n.ToLowerInvariant();
                        if (pos.TryGetValue(low, out int at)) l[at] = (low, i, n);
                        else { pos[low] = l.Count; l.Add((low, i, n)); }
                    }
                    all.Add((c, l));
                }
                lower[paks] = all;
            }
        }
        string suf = suffix.ToLowerInvariant();
        foreach (var (c, names) in all)
            foreach (var (low, i, n) in names)
                if (low.EndsWith(suf)) return (c, i, n);
        return null;
    }

    public static string? ReadFile(string paks, string nameSuffix, string tmp)
    {
        var hit = Find(paks, nameSuffix);
        if (hit == null) return null;
        var (c, idx, name) = hit.Value;
        string p = Path.Combine(tmp, name.Split('/')[^1]);
        File.WriteAllBytes(p, c.Read(idx));
        string ub = (name[..^7] + ".ubulk").ToLowerInvariant();
        if (name.EndsWith(".uasset"))
        {
            int? by = null;
            foreach (var (i, n) in c.Names) if (n.ToLowerInvariant() == ub) by = i;
            if (by != null) File.WriteAllBytes(p[..^7] + ".ubulk", c.Read(by.Value));
        }
        return p;
    }

    /// <summary>every wrestler attire folder that has a SkeletalMesh -&gt; [(id, name, female, attire)] by name, attire</summary>
    public static List<(string Id, string Name, bool Female, string Attire)> Roster(string paks)
    {
        var o = new Dictionary<string, (string, string, bool, string)>();
        foreach (var c in Containers(paks))
            foreach (var (_, name) in c.Names)
            {
                var m = Regex.Match(name, @"WrestlerData/([MF]\d{4}_\d+_[^/]+)/");
                if (!m.Success) continue;
                string f = m.Groups[1].Value;
                if (o.ContainsKey(f)) continue;
                var parts = f.Split('_');
                o[f] = (f, string.Join(" ", parts[2..]).Replace('_', ' '), f.StartsWith('F'), parts[1]);
            }
        return o.Values.OrderBy(r => r.Item2, StringComparer.Ordinal).ThenBy(r => r.Item4, StringComparer.Ordinal).ToList();
    }

    /// <summary>the attire's SkeletalMesh package name (the biggest package in the folder that is not a material /
    /// texture / physics / PAL / SW one)</summary>
    public static string? MeshPackage(string paks, string folder)
    {
        (long Size, string Name)? best = null;
        foreach (var c in Containers(paks))
            foreach (var (idx, name) in c.Names)
            {
                string key = $"/WrestlerData/{folder}/";
                if (!name.Contains(key) || !name.EndsWith(".uasset")) continue;
                string tail = name.Split(key)[1];
                if (tail.Contains('/') || Regex.IsMatch(tail, "(Physics|PAL_|_SW_)")) continue;
                long size = c.OffLen[idx].Len;
                if (best == null || size > best.Value.Size) best = (size, name);
            }
        return best?.Name;
    }

    public static double[] QuatMat(double x, double y, double z, double w) =>
    [
        1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y),
        2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x),
        2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y),
    ];

    /// <summary>MI package -&gt; ({role: image}, {scalar parameter: value}); roles: color, normal, orms, orm, skinmask, pupil...</summary>
    public static (Dictionary<string, Img> Images, Dictionary<string, double> Scalars) MaterialTextures(string paks, string miName, string tmp, string? miFolder = null)
    {
        string rel = miName.StartsWith("/Game/") ? miName.Split("/Game/", 2)[1].Split('/', 2)[1] : miName;
        var p = ReadFile(paks, "/" + rel + ".uasset", tmp);
        if (p == null) return ([], []);
        var pkg = new ZenPackage(File.ReadAllBytes(p));
        Dictionary<string, object?> props = [];
        foreach (var (e, s, end) in pkg.ExportData()) { props = new ZenReader(pkg, s, end).Props(); break; }
        string[] bad = ["dummy", "checker", "/_concept/", "/engine/", "t_default", "blood", "bruise", "wrinkle", "pore_normal"];
        string? own = miFolder != null ? "/" + miFolder.ToLowerInvariant() + "/" : null;
        var imports = pkg.Names.Where(n => n.StartsWith("/Game/") && n.ToLowerInvariant().Contains("/texture/") && !bad.Any(b => n.ToLowerInvariant().Contains(b)))
            .OrderBy(n => own != null && n.ToLowerInvariant().Contains(own) ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal).ToList();
        var scal = new Dictionary<string, double>();
        if (props.TryGetValue("ScalarParameterValues", out var spv) && spv is List<object?> sl)
            foreach (var t in sl)
                if (t is Dictionary<string, object?> td && td.TryGetValue("ParameterValue", out var pv) && pv is double dv
                    && td.TryGetValue("ParameterInfo", out var pi) && pi is Dictionary<string, object?> pid && pid.TryGetValue("Name", out var nmo) && nmo is string nm)
                    scal[nm] = dv;
        var o = new Dictionary<string, Img>();
        foreach (var n in imports)
        {
            string stem = n[(n.LastIndexOf('/') + 1)..];
            string low = stem.ToLowerInvariant();
            string? role = low.EndsWith("_pupil_col") ? "pupil" : low.EndsWith("_pupil_msk") ? "pupilmask" : low.EndsWith("_col") ? "color" : low.EndsWith("_n") ? "normal"
                : low.EndsWith("_orms") ? "orms" : low.EndsWith("_orm") ? "orm" : low.EndsWith("_smsk") ? "skinmask" : null;
            if (role == null || o.ContainsKey(role)) continue;
            string r2 = n.Split("/Game/", 2)[1];
            string? tp = r2.Contains('/') ? ReadFile(paks, "/" + r2.Split('/', 2)[1] + ".uasset", tmp) : null;
            tp ??= ReadFile(paks, $"/{stem}.uasset", tmp);
            if (tp == null) continue;
            try
            {
                var t = AewTexture.Read(tp);
                var m = t.Mips[0];
                var data = AewTexture.UntilePs4(t.Fmt, m.W, m.H, t.MipBytes(0));
                o[role] = AewTexture.ToImage(t.Fmt, m.W, m.H, data).ToRgba();
            }
            catch (Exception) { }
        }
        if (o.ContainsKey("pupil") && o.ContainsKey("color"))
        {
            // (AEW eyes: the white + the iris / pupil layer, by its mask - Image.composite: Pillow's paste blend)
            var white = o["color"].ToRgb();
            var pupil = Resample.Resize(o["pupil"].ToRgb(), white.W, white.H, Filter.Lanczos);
            var maskSrc = (o.TryGetValue("pupilmask", out var pm) ? pm : o["pupil"]).ToRgba();
            var mask = maskSrc.W == white.W && maskSrc.H == white.H ? maskSrc.Copy() : Resample.ResizeRgbaPremultiplied(maskSrc, white.W, white.H, Filter.Lanczos);
            int band = o.ContainsKey("pupilmask") ? 0 : 3;
            var c = new Img(white.W, white.H, 4);
            for (int i = 0; i < white.W * white.H; i++)
            {
                int m = mask.Px[4 * i + band];
                for (int k = 0; k < 3; k++)
                {
                    int tmp1 = white.Px[3 * i + k] * (255 - m) + pupil.Px[3 * i + k] * m + 128;
                    c.Px[4 * i + k] = (byte)(((tmp1 >> 8) + tmp1) >> 8);
                }
                c.Px[4 * i + 3] = 255;
            }
            o["color"] = c;
        }
        return (o, scal);
    }

    /// <summary>the colour map x Level, in linear light (alpha kept)</summary>
    public static Img HairLevel(Img im, double level)
    {
        var a = im.ToRgba();
        var o = new Img(a.W, a.H, 4);
        for (int i = 0; i < a.W * a.H; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                double c = a.Px[4 * i + k] / 255.0;
                double lin = (c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4)) * level;
                lin = Math.Clamp(lin, 0, 1);
                double s = lin <= 0.0031308 ? 12.92 * lin : 1.055 * Math.Pow(lin, 1 / 2.4) - 0.055;
                o.Px[4 * i + k] = (byte)(s * 255 + 0.5);
            }
            o.Px[4 * i + 3] = (byte)(a.Px[4 * i + 3] / 255.0 * 255 + 0.5);
        }
        return o;
    }

    public static string KindOf(string slot)
    {
        string s = slot.ToLowerInvariant();
        if (s.Contains("eye_ov")) return "overlay";
        if (s.Contains("eyelash")) return "lash";
        if (s.Contains("eye")) return "eye";
        if (s.Contains("hair")) return "hair";
        if (s.Contains("mouth")) return "teeth";
        if (s.Contains("skin")) return "head";
        return "gear";
    }

    public static Model Load(string paks, string folder)
    {
        string tmp = Directory.CreateTempSubdirectory("portit_aew_").FullName;
        var pkgname = MeshPackage(paks, folder) ?? throw new InvalidDataException($"No SkeletalMesh for {folder}");
        var p = ReadFile(paks, "WrestlerData/" + pkgname.Split("/WrestlerData/")[1], tmp)!;
        var mesh = AewMesh.Read(p);
        var L = mesh.Lods[0];
        var world = new List<double[]>();
        var bones = new List<Bone>();
        for (int i = 0; i < mesh.Bones.Count; i++)
        {
            var b = mesh.Bones[i];
            var R = QuatMat(b.Q[0], b.Q[1], b.Q[2], b.Q[3]);
            double[] M = [R[0], R[1], R[2], 0, R[3], R[4], R[5], 0, R[6], R[7], R[8], 0, b.T[0], b.T[1], b.T[2], 1];
            world.Add(b.Parent < 0 ? M : Blas.MatMul(M, world[b.Parent], 4));
            bones.Add(new Bone { Name = b.Name, Parent = b.Parent, World = world[i] });
        }
        var gb = AewMesh.GlobalBones(L);
        int nv = L.Positions.Length / 3, ntc = L.UvChannels, mi_ = L.SkinSlots.Length / Math.Max(nv, 1);
        var pkg = new ZenPackage(File.ReadAllBytes(p));
        var matsPkgs = pkg.Names.Where(n => n.StartsWith("/Game/") && n.ToLowerInvariant().Contains("/material/")).ToList();
        var linked = new Dictionary<int, string>();
        try
        {
            int nimp = (pkg.ExportsOff - pkg.ImportsOff) / 8;
            var ids = new Dictionary<ulong, int>();
            for (int i = 0; i < nimp; i++) ids[BinaryPrimitives.ReadUInt64LittleEndian(pkg.D.AsSpan(pkg.ImportsOff + 8 * i)) & ~(3UL << 62)] = i;
            foreach (var nm in pkg.Names)
                if (nm.StartsWith("/Game/") && ids.TryGetValue(CityHash.ImportId(nm + "/" + nm[(nm.LastIndexOf('/') + 1)..]), out int i)) linked[i] = nm;
        }
        catch (Exception) { linked = []; }
        var meshes = new List<SourceMesh>();
        var texCache = new Dictionary<string, (Dictionary<string, Img>, Dictionary<string, double>)>();
        foreach (var s in L.Sections)
        {
            if (s.Disabled != 0) continue;
            string slot = s.Material < mesh.Materials.Count ? mesh.Materials[s.Material].Slot : $"mat{s.Material}";
            string kind = KindOf(slot);
            if (kind == "overlay") continue;
            int a = s.BaseVertex, b = s.BaseVertex + s.Vertices;
            var tri = new int[3 * s.Triangles];
            for (int t = 0; t < tri.Length; t++) tri[t] = (int)(L.Indices[s.BaseIndex + t] - a);
            string key = Regex.Replace(slot, "^(mt|s)_", "", RegexOptions.IgnoreCase);
            key = Regex.Replace(key, @"_\d+$", "").ToLowerInvariant();
            var cand = matsPkgs.Where(m => m[(m.LastIndexOf('/') + 1)..].ToLowerInvariant().Contains(key)).ToList();
            if (cand.Count == 0)
            {
                var words = key.Split('_').ToHashSet();
                cand = matsPkgs.Where(m => words.IsSubsetOf(m[(m.LastIndexOf('/') + 1)..].ToLowerInvariant().Split('_'))).ToList();
            }
            string? mi = cand.Count > 0 ? cand.OrderBy(x => x.Length).First() : null;
            if (s.Material < mesh.Materials.Count)
            {
                int rf = mesh.Materials[s.Material].Index;
                if (rf < 0 && linked.TryGetValue(-rf - 1, out var ln) && ln.ToLowerInvariant().Contains("/material/")) mi = ln;
            }
            string ck = mi ?? "\0none";
            if (!texCache.ContainsKey(ck)) texCache[ck] = mi != null ? MaterialTextures(paks, mi, tmp, folder) : ([], []);
            var (tex, scal) = texCache[ck];
            double lvl = scal.GetValueOrDefault("Level", 1.0);
            string name = key;
            if (kind == "hair" && tex.ContainsKey("color") && Math.Abs(lvl - 1.0) > 1e-3)
            {
                // (Python: the mesh's name becomes the cache key (mi, 'level') here - a tuple; kept as text)
                string lk = ck + "\0level";
                if (!texCache.ContainsKey(lk)) texCache[lk] = (new Dictionary<string, Img>(tex) { ["color"] = HairLevel(tex["color"], lvl) }, scal);
                (tex, scal) = texCache[lk];
                name = $"('{mi}', 'level')";
            }
            var sm = new SourceMesh
            {
                Name = name, Material = slot, Kind = kind, Cutout = kind is "hair" or "lash", Source = folder, MaxInf = mi_,
                Positions = new double[3 * (b - a)], Normals = new double[3 * (b - a)], Uvs = new double[2 * (b - a)], Tris = tri,
                BoneIdx = new long[mi_ * (b - a)], BoneW = new double[mi_ * (b - a)], Images = tex, Scalars = scal,
            };
            for (int v = a; v < b; v++)
            {
                for (int c = 0; c < 3; c++) { sm.Positions[3 * (v - a) + c] = L.Positions[3 * v + c]; sm.Normals[3 * (v - a) + c] = L.TangentZ[4 * v + c]; }
                sm.Uvs[2 * (v - a)] = L.Uvs[(v * ntc) * 2]; sm.Uvs[2 * (v - a) + 1] = L.Uvs[(v * ntc) * 2 + 1];
                for (int k = 0; k < mi_; k++) { sm.BoneIdx[mi_ * (v - a) + k] = gb[mi_ * v + k]; sm.BoneW[mi_ * (v - a) + k] = L.SkinWeights[mi_ * v + k]; }
            }
            meshes.Add(sm);
        }
        return new Model { Name = folder, Bones = bones, Meshes = meshes, Female = folder.StartsWith('F'), Attire = folder, Attires = [folder] };
    }
}
