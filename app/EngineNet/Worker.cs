using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortIT.Engine.Build;
using PortIT.Engine.Port;
using PortIT.Engine.Readers;

namespace PortIT.Engine;

/// <summary>
/// Engine/worker.py in C#: PortIT's engine commands, the same requests and answers (JSON objects), run inside PortIT.exe.
///   list, inspect, source_preview, port, preview, export_svrmod, install, stars, install_attire, bigmodel.
/// The old "classic" converter route (portit/convert.py) is not here: the app never asks for it.
/// Game files are only read; output goes to new folders (install copies into the game only when asked).
/// </summary>
public sealed class Worker
{
    public const string Version = "1.2.0";
    public const string CakPrefix = "cak:", PacPrefix = "pac:";
    readonly Action<string> log;
    readonly CancellationToken cancel;
    /// <summary>the 2K26 archive reader (PortIT.exe's Core.CakeSource.Run: request file, response file -&gt; exit code)</summary>
    public Func<string, string, int>? Cake;
    /// <summary>the big-model DLL that 'bigmodel install' copies (Engine\bigmodel\xinput1_4.dll beside PortIT.exe)</summary>
    public string BigModelDll = Path.Combine(AppContext.BaseDirectory, "Engine", "bigmodel", "xinput1_4.dll");
    public static readonly string LocalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortIT");
    public static readonly string Previews = Path.Combine(LocalRoot, "previews");
    public static readonly string CakCache = Path.Combine(LocalRoot, "k26cache");

    public Worker(Action<string>? log = null, CancellationToken cancel = default)
    {
        this.cancel = cancel;
        this.log = s => { cancel.ThrowIfCancellationRequested(); log?.Invoke(s); };
    }

    // ---------------------------------------------------------------- the protocol
    /// <summary>request -&gt; answer {ok: true, ...} or {ok: false, error}</summary>
    public JsonObject Run(JsonObject req)
    {
        JsonObject res;
        try
        {
            string cmd = Str(req, "command");
            res = cmd switch
            {
                "list" => List(req), "inspect" => Inspect(req), "port" => PortCmd(req), "preview" => new JsonObject { ["preview"] = Preview.Render(Str(req, "pac"), Str(req, "out")) },
                "source_preview" => new JsonObject { ["preview"] = SourcePreview(req, LoadSource(ref req)) }, "export_svrmod" => ExportSvrmod(Str(req, "mod_dir"), StrOr(req, "out")),
                "install" => Install(req), "stars" => new JsonObject { ["stars"] = Attires.StarsJson(Str(req, "game")) },
                "install_attire" => Attires.Install(req, (s, g, _) => CopyTexturePack(s, g, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Str(req, "mod_dir")))))),
                "bigmodel" => BigModel(req),
                _ => throw new ArgumentException($"unknown command {cmd}"),
            };
            res["ok"] = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log2(e.ToString());
            res = new JsonObject { ["ok"] = false, ["error"] = $"{e.GetType().Name}: {e.Message}" };
        }
        return res;
    }

    void log2(string s) { try { log(s); } catch (OperationCanceledException) { } }

    static string Str(JsonObject o, string k) => o[k] is JsonNode n ? (n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString()) : throw new KeyNotFoundException(k);
    static string? StrOr(JsonObject o, string k) => o[k] is JsonNode n && n.GetValueKind() != JsonValueKind.Null ? (n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString()) : null;
    static bool Truthy(JsonObject o, string k) => o[k] is JsonNode n && n.GetValueKind() switch
    {
        JsonValueKind.True => true, JsonValueKind.String => n.GetValue<string>().Length > 0, JsonValueKind.Number => n.GetValue<double>() != 0,
        JsonValueKind.Array => n.AsArray().Count > 0, JsonValueKind.Object => true, _ => false,
    };

    // ---------------------------------------------------------------- list
    static IEnumerable<(string Root, List<string> Dirs, List<string> Files)> Walk(string top)
    {
        // (os.walk, top-down: a folder, then its sub folders in order; a caller may clear Dirs to skip them)
        var dirs = new List<string>(); var files = new List<string>();
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(top))
                if (Directory.Exists(e)) dirs.Add(Path.GetFileName(e)); else files.Add(Path.GetFileName(e));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        yield return (top, dirs, files);
        foreach (var d in dirs.ToList())
            foreach (var x in Walk(Path.Combine(top, d))) yield return x;
    }

    JsonArray List2k(string path)
    {
        var o = new List<JsonObject>();
        foreach (var (root, dirs, files) in Walk(path))
        {
            if (Path.GetFileName(root).ToLowerInvariant() == "basemodel" && files.Any(Source2k.IsBaseModel))
            {
                string folder = Path.GetDirectoryName(root)!;
                var (bas, att) = Source2k.FindWrestler(folder);
                string name = Path.GetFileName(folder);
                var parts = name.Split('_');
                string nice = string.Join(" ", parts[0].All(char.IsAsciiDigit) && parts[0].Length > 0 ? parts[1..] : parts);
                bool female = folder.ToLowerInvariant().Contains("female") || (bas != null && Mcd.LooksFemale(bas));
                o.Add(new JsonObject
                {
                    ["id"] = folder, ["name"] = nice, ["female"] = female,
                    ["attires"] = new JsonArray(att.Select(a => (JsonNode)Path.GetRelativePath(folder, a)).ToArray()), ["group"] = Path.GetFileName(Path.GetDirectoryName(folder)),
                });
                dirs.Clear();
                continue;
            }
            var packs = files.Where(f => f.ToLowerInvariant().EndsWith(".pac") && YukesPack.IsCharacterPack(f) && YukesPack.IsPac(Path.Combine(root, f)))
                             .Order(StringComparer.Ordinal).ToList();
            foreach (var f in packs)
            {
                string pac = Path.Combine(root, f), name = Path.GetFileName(root);
                string nice = name + (packs.Count > 1 ? $" ({Path.GetFileNameWithoutExtension(f)})" : "");
                var cached = YukesPack.Cached(pac);
                bool female = cached != null && Mcd.LooksFemale(Path.Combine(cached, "BaseModel", "baseModel.mdl"));
                o.Add(new JsonObject { ["id"] = PacPrefix + pac, ["name"] = nice, ["female"] = female, ["attires"] = new JsonArray("Attire"), ["group"] = Path.GetFileName(Path.GetDirectoryName(root)) });
            }
        }
        return new JsonArray(o.OrderBy(r => r["name"]!.GetValue<string>().ToLowerInvariant(), StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray());
    }

    static bool IsGameFolder(string path) =>
        !string.IsNullOrEmpty(path) && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName)
            .Any(f => f!.ToLowerInvariant().StartsWith("bakedfile") && f.ToLowerInvariant().EndsWith(".cak"));

    JsonObject CakeCall(string cmd, string game, JsonObject? kw = null)
    {
        if (Cake == null) throw new InvalidOperationException("the 2K26 archive reader is not available");
        Directory.CreateDirectory(CakCache);
        string job = Path.Combine(CakCache, "job_" + Environment.ProcessId);
        var rq = kw?.DeepClone().AsObject() ?? new JsonObject();
        rq["cmd"] = cmd; rq["game"] = game; rq["cache"] = CakCache;
        File.WriteAllText(job + "_req.json", rq.ToJsonString(), new UTF8Encoding(false));
        if (File.Exists(job + "_resp.json")) File.Delete(job + "_resp.json");
        Cake(job + "_req.json", job + "_resp.json");
        if (!File.Exists(job + "_resp.json")) throw new InvalidOperationException("reading the 2K26 archives failed (no answer from the archive reader)");
        var r = JsonNode.Parse(File.ReadAllText(job + "_resp.json"))!.AsObject();
        if (r["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("2K26 archives: " + (StrOr(r, "error") ?? "failed"));
        return r;
    }

    JsonArray ListGame(string game)
    {
        log("reading the 2K26 archives (the first time takes a little longer)...");
        var ws = CakeCall("index", game)["wrestlers"]!.AsArray();
        var count = new Dictionary<string, int>();
        foreach (var w in ws) { string k = w!["name"]!.GetValue<string>().ToLowerInvariant(); count[k] = count.GetValueOrDefault(k) + 1; }
        var o = new List<JsonObject>();
        foreach (var w in ws)
        {
            string name = w!["name"]!.GetValue<string>();
            string folder = w["folder"]!.GetValue<string>();
            var atts = w["attires"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
            if (count[name.ToLowerInvariant()] > 1) name += " #" + folder.Split('_')[0];
            if (atts.All(a => a.ToLowerInvariant().Contains("mattel"))) name += " (Mattel figure)";
            o.Add(new JsonObject { ["id"] = CakPrefix + folder, ["name"] = name, ["female"] = false, ["attires"] = new JsonArray(atts.Select(a => (JsonNode)a).ToArray()), ["group"] = "WWE 2K26 (game)" });
        }
        return new JsonArray(o.OrderBy(r => r["name"]!.GetValue<string>().ToLowerInvariant(), StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray());
    }

    static string? ModelFileFolder(string path)
    {
        var d = Path.GetDirectoryName(Path.GetFullPath(path));
        for (int i = 0; i < 4 && d != null; i++)
        {
            if (Directory.Exists(Path.Combine(d, "BaseModel"))) return d;
            d = Path.GetDirectoryName(d);
        }
        return null;
    }

    JsonObject List(JsonObject req)
    {
        string src = Str(req, "source"), path = Str(req, "path");
        if (src == "2k26" && File.Exists(path))
        {
            if (path.ToLowerInvariant().EndsWith(".pac"))
            {
                if (!YukesPack.IsPac(path)) throw new ArgumentException($"{Path.GetFileName(path)} is not a WWE 2K19 - 2K22 character pack");
                var cached = YukesPack.Cached(path);
                return new JsonObject { ["wrestlers"] = new JsonArray(new JsonObject
                {
                    ["id"] = PacPrefix + path, ["name"] = Path.GetFileName(Path.GetDirectoryName(path)) + $" ({Path.GetFileName(path)[..^4]})",
                    ["female"] = cached != null && Mcd.LooksFemale(Path.Combine(cached, "BaseModel", "baseModel.mdl")), ["attires"] = new JsonArray("Attire"), ["group"] = "chosen file",
                }) };
            }
            var folder = ModelFileFolder(path) ?? throw new ArgumentException($"No BaseModel folder beside {path}: choose a wrestler's baseModel / attire model file");
            return new JsonObject { ["wrestlers"] = List2k(folder) };
        }
        if (src == "2k26") return new JsonObject { ["wrestlers"] = IsGameFolder(path) ? ListGame(path) : List2k(path) };
        return new JsonObject { ["wrestlers"] = new JsonArray(SourceAew.Roster(path).Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id, ["name"] = r.Name, ["female"] = r.Female, ["attire"] = r.Attire,
        }).ToArray()) };
    }

    // ---------------------------------------------------------------- the source
    JsonObject ResolveCak(JsonObject req)
    {
        string id = StrOr(req, "id") ?? "";
        if (StrOr(req, "source") == "2k26" && id.StartsWith(PacPrefix))
        {
            string pac = id[PacPrefix.Length..];
            string name = Path.GetFileName(Path.GetDirectoryName(pac)) + " " + Path.GetFileNameWithoutExtension(pac);
            var r = req.DeepClone().AsObject();
            r["id"] = YukesPack.Extract(pac, name, log);
            r["attire"] = Path.Combine("Attire", "Attire.mdl");
            return r;
        }
        if (StrOr(req, "source") != "2k26" || !id.StartsWith(CakPrefix)) return req;
        string folder = id[CakPrefix.Length..];
        string att = StrOr(req, "attire") ?? "";
        if (att.Length == 0)
        {
            var w = CakeCall("index", Str(req, "path"))["wrestlers"]!.AsArray().FirstOrDefault(x => x!["folder"]!.GetValue<string>() == folder)
                    ?? throw new InvalidOperationException(folder + ": not in the 2K26 archives");
            var atts = w["attires"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
            att = atts.FirstOrDefault(a => !a.ToLowerInvariant().Contains("mattel")) ?? atts[0];
        }
        log($"extracting {folder} / {att.Replace('\\', '/').Split('/')[0]} from the 2K26 archives...");
        var p = CakeCall("extract", Str(req, "path"), new JsonObject { ["folder"] = folder, ["attire"] = att })["path"]!.GetValue<string>();
        var o = req.DeepClone().AsObject();
        o["id"] = p; o["attire"] = att;
        return o;
    }

    SourceAew.Model LoadSource(ref JsonObject req)
    {
        req = ResolveCak(req);
        if (Str(req, "source") == "2k26")
        {
            string id = Str(req, "id");
            var att = StrOr(req, "attire");
            return Source2k.Load(id, string.IsNullOrEmpty(att) ? null : Path.Combine(id, att), Previews);
        }
        return SourceAew.Load(Str(req, "path"), Str(req, "id"));
    }

    static readonly double[] AewT = [1, 0, 0, 0, 0, -1, 0, -1, 0];   // (aew_bridge.AX with Y and Z turned: 2K's axes)

    SourceAew.Model AewView(SourceAew.Model src)
    {
        string tdir = Path.Combine(Previews, "aew_tex");
        Directory.CreateDirectory(tdir);
        var o = new List<SourceAew.SourceMesh>();
        for (int i = 0; i < src.Meshes.Count; i++)
        {
            var me = src.Meshes[i];
            var tex = new Dictionary<string, string>();
            if (me.Images.TryGetValue("color", out var im))
            {
                string p = Path.Combine(tdir, $"{src.Name}_{i}.png");
                if (!File.Exists(p) || (DateTime.Now - File.GetLastWriteTime(p)).TotalSeconds > 3600)
                {
                    var im2 = im.ToRgba();
                    double k = Math.Min(1.0, 512.0 / Math.Max(im2.W, im2.H));
                    int w = Math.Max(1, (int)(im2.W * k)), h = Math.Max(1, (int)(im2.H * k));
                    var rgb = Imaging.Resample.Resize(im2.ToRgb(), w, h, Imaging.Filter.Bilinear);
                    Imaging.ImageIO.SavePng(rgb.WithAlpha(Imaging.Resample.Resize(im2.Channel(3), w, h, Imaging.Filter.Bilinear)), p);
                }
                tex["color"] = p;
            }
            o.Add(new SourceAew.SourceMesh
            {
                Name = me.Name, Material = me.Material, Kind = me.Kind == "hair" ? "gear" : me.Kind, Cutout = me.Cutout, Source = me.Source,
                Positions = Util.Rows.TimesMatT(me.Positions, AewT), Normals = Util.Rows.TimesMatT(me.Normals, AewT), Uvs = me.Uvs, Tris = me.Tris,
                Images = me.Images, Scalars = me.Scalars, Textures = tex, SourcePath = null, Index = i, Variant = false,
            });
        }
        return new SourceAew.Model { Game = src.Game, Name = src.Name, Bones = src.Bones, Meshes = o, Female = src.Female, Attire = src.Attire, Attires = src.Attires };
    }

    string? SourcePreview(JsonObject req, SourceAew.Model src)
    {
        string? source = StrOr(req, "source");
        if (source is not ("2k26" or "aew")) return null;
        var gone = (req["leave_out"] as JsonArray ?? []).Select(n => n!.GetValue<string>().ToLowerInvariant()).ToHashSet();
        var use = (req["use"] as JsonArray ?? []).Select(n => n!.GetValue<string>().ToLowerInvariant()).ToHashSet();
        foreach (var m in src.Meshes) if (use.Contains(m.Material.ToLowerInvariant())) m.Variant = false;   // (alternates chosen: drawn)
        var view = new SourceAew.Model { Game = src.Game, Name = src.Name, Bones = src.Bones, Female = src.Female, Attire = src.Attire, Attires = src.Attires,
                                         Meshes = src.Meshes.Where(m => !gone.Contains(m.Material.ToLowerInvariant())).ToList() };
        if (source == "aew") view = AewView(view);
        Directory.CreateDirectory(Previews);
        foreach (var root in new[] { Previews, Path.Combine(Previews, "aew_tex") })
            if (Directory.Exists(root))
                foreach (var f in Directory.EnumerateFiles(root))
                    try { if ((DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds > 3600) File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        string stem = Regex.Replace(Path.GetFileName(Str(req, "id")), "[^A-Za-z0-9_]+", "_");
        if (stem.Length > 60) stem = stem[^60..];
        string outPath = Path.Combine(Previews, $"{stem}_{DateTime.Now:HHmmss}.png");
        try { return Preview.RenderSource(view, outPath, title: "", highlight: StrOr(req, "highlight")); }
        catch (Exception e) when (e is not OperationCanceledException) { log("preview: " + e.Message); return null; }
    }

    JsonObject Inspect(JsonObject req)
    {
        var src = LoadSource(ref req);
        var meshes = new JsonArray();
        foreach (var m in src.Meshes)
            meshes.Add(new JsonObject
            {
                ["name"] = m.Name, ["material"] = m.Material, ["kind"] = m.Kind, ["verts"] = m.N, ["tris"] = m.Tris.Length / 3, ["variant"] = m.Variant,
                ["textures"] = new JsonArray(m.Textures.Keys.Union(m.Images.Keys).Order(StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray()),
            });
        return new JsonObject
        {
            ["name"] = src.Name, ["meshes"] = meshes,
            ["attires"] = new JsonArray(src.Attires.Select(a => (JsonNode)(Path.GetFileName(Path.GetDirectoryName(a) ?? "") + "/" + Path.GetFileName(a))).ToArray()),
            ["female"] = src.Female, ["preview"] = SourcePreview(req, src),
        };
    }

    // ---------------------------------------------------------------- the mod folder
    static string? AewName(JsonObject req)
    {
        if (StrOr(req, "source") != "aew") return null;
        var parts = Str(req, "id").Split('_');
        return parts.Length > 2 ? string.Join(" ", parts[2..]) : null;
    }

    static string Py31(string s) => s.Length > 31 ? s[..31] : s;

    public static string Manifest(string modId, string name, string bas, bool female, string? pac = null)
    {
        var lines = new List<string> { "type=superstar", $"id={modId}", $"name={Py31(name)}", $"short={Py31(name)}", $"base={bas}", "author=", "version=1.0",
                                       $"made_with=PortIT {Version}", "ratings=80,80,80,80,80,80,80", "call=48" };
        if (pac != null)
        {
            string? need = null;
            try { need = Budget.Needs(pac); } catch (Exception) { }
            if (need != null) lines.Add($"needs={need}");
        }
        return string.Join("\n", lines) + "\n";
    }

    static string WithNeeds(string manifestPath, string pac)
    {
        string text = File.ReadAllText(manifestPath).Replace("\r\n", "\n").Replace("\r", "\n");   // (Python reads it in text mode)
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var keys = lines.Where(l => l.Contains('=')).Select(l => l.Split('=', 2)[0]).ToHashSet();
        if (lines.Contains("type=superstar") && !keys.Contains("needs") && File.Exists(pac))
        {
            string? need = null;
            try { need = Budget.Needs(pac); } catch (Exception) { }
            if (need != null) text = text.TrimEnd('\n') + $"\nneeds={need}\n";
        }
        return text;
    }

    /// <summary>a mod folder -&gt; &lt;id&gt;.svrmod (a zip of the folder, stored entries, manifest.txt at its root) and, with an
    /// HD texture pack, &lt;id&gt;_HD_textures.zip</summary>
    public static JsonObject ExportSvrmod(string modDir, string? outDir = null)
    {
        modDir = Path.GetFullPath(modDir).TrimEnd('\\', '/');
        string mid = Path.GetFileName(modDir);
        outDir ??= Path.GetDirectoryName(modDir)!;
        var res = new JsonObject();
        string path = Path.Combine(outDir, mid + ".svrmod");
        if (File.Exists(path)) File.Delete(path);
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string dir)
            {
                foreach (var f in Directory.EnumerateFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                {
                    if (f == "portit_report.txt" || f!.StartsWith('.')) continue;
                    string full = Path.Combine(dir, f);
                    string rel = Path.GetRelativePath(modDir, full).Replace('\\', '/');
                    var e = z.CreateEntry(rel, CompressionLevel.NoCompression);
                    e.LastWriteTime = File.GetLastWriteTime(full);
                    using var s = e.Open();
                    if (rel == "manifest.txt") { var b = Encoding.UTF8.GetBytes(WithNeeds(full, Path.Combine(modDir, "ch.pac"))); s.Write(b); }
                    else { using var fs = File.OpenRead(full); fs.CopyTo(s); }
                }
                foreach (var d in Directory.EnumerateDirectories(dir).Select(Path.GetFileName).Where(d => d != "texture_pack" && !d!.StartsWith('.')).Order(StringComparer.Ordinal))
                    Add(Path.Combine(dir, d!));
            }
            Add(modDir);
        }
        res["svrmod"] = path;
        string tp = Path.Combine(modDir, "texture_pack");
        if (Directory.Exists(tp) && Directory.EnumerateFileSystemEntries(tp).Any())
        {
            string zpath = Path.Combine(outDir, mid + "_HD_textures.zip");
            if (File.Exists(zpath)) File.Delete(zpath);
            using (var z = ZipFile.Open(zpath, ZipArchiveMode.Create))
                foreach (var f in Directory.EnumerateFiles(tp).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                    z.CreateEntryFromFile(Path.Combine(tp, f!), $"{mid} HD/{f}", CompressionLevel.NoCompression);
            res["texture_zip"] = zpath;
        }
        return res;
    }

    // ---------------------------------------------------------------- port
    JsonObject PortCmd(JsonObject req)
    {
        var sw = Stopwatch.StartNew();
        string outDir = Str(req, "out");
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(outDir, "_work");
        Directory.CreateDirectory(work);
        string game = Str(req, "game");
        var asked = req;
        req = ResolveCak(req);
        string source = Str(req, "source");
        if (source == "2k26" && (StrOr(req, "attire") ?? "").ToLowerInvariant().Contains("mattel"))
            throw new InvalidOperationException("This is a Mattel action-figure model: its toy-like face (tiny eyes) has no human face for the face rig to fit. Choose another attire or wrestler.");
        if (source is not ("2k26" or "aew") || Truthy(req, "classic"))
            throw new NotSupportedException("the classic converter route is not part of the C# engine");
        AewBridge? bridge = null;
        string att;
        if (source == "aew")
        {
            log("reading the AEW wrestler...");
            bridge = new AewBridge(Str(req, "path"), Str(req, "id"), work, log);
            att = bridge.Attire;
        }
        else
        {
            att = StrOr(req, "attire") ?? "";
            if (att.Length == 0) { var a = Source2k.FindWrestler(Str(req, "id")).Attires; att = a.Count > 0 ? Path.GetRelativePath(Str(req, "id"), a[0]) : "Default_Attire"; }
            if (att.ToLowerInvariant().EndsWith(".mcd") || att.ToLowerInvariant().EndsWith(".mdl")) att = Path.GetDirectoryName(att) ?? "";
        }
        int budget = req["budget"] is JsonNode bn ? (int)bn.GetValue<double>() : 190;
        int hd = req["hd_size"] is JsonNode hn && hn.GetValueKind() == JsonValueKind.Number ? (int)hn.GetValue<double>() : 4096;
        string modId = StrOr(req, "mod_id") is { Length: > 0 } mi ? mi : "portit_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        log($"porting with the Rock / Brock pipeline ({(budget >= Port2k.FullBudget ? "full detail + texture pack" : $"{budget} pages")})...");
        var p2 = new Port2k();
        var leave = (req["leave_out"] as JsonArray ?? []).Select(n => n!.GetValue<string>());
        var res = p2.Build(Str(req, "id"), att, work, game, budget, template: StrOr(req, "template") is { Length: > 0 } t ? t : null, log: log, print: log,
                           packPrefix: modId + "_hd", leaveOut: leave, bridge: bridge, packSize: hd is 2048 or 4096 or 8192 ? hd : 4096,
                           use: (req["use"] as JsonArray ?? []).Select(n => n!.GetValue<string>()));
        var pack = new List<string>(res.Pack);
        var entranceLines = new List<string>();
        int? entrancePages = null;
        if (req["entrance"] is JsonObject ent)
        {
            // the entrance outfit: a second build (the same route and memory as the ring model) -> ch.pac EMD kind 4
            string? entAsked = StrOr(ent, "attire");
            var entLeave = (ent["leave_out"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
            string entId = Str(req, "id"), entAtt = att;
            AewBridge? entBridge = null;
            string ework = Path.Combine(work, "_entrance");
            if (source == "aew")
            {
                entBridge = new AewBridge(Str(req, "path"), Str(req, "id"), ework, log);
                entAtt = entBridge.Attire;
            }
            else if (!string.IsNullOrEmpty(entAsked))
            {
                if (entAsked.ToLowerInvariant().Contains("mattel"))
                    throw new InvalidOperationException("The entrance outfit is a Mattel action-figure model: its toy-like face has no human face for the face rig to fit. Choose another attire.");
                string? asId = StrOr(asked, "id");
                if (asId != null && asId.StartsWith(CakPrefix))
                {
                    log($"extracting the entrance attire {entAsked.Replace('\\', '/').Split('/')[0]} from the 2K26 archives...");
                    entId = CakeCall("extract", Str(asked, "path"), new JsonObject { ["folder"] = asId[CakPrefix.Length..], ["attire"] = entAsked })["path"]!.GetValue<string>();
                }
                entAtt = entAsked;
                if (entAtt.ToLowerInvariant().EndsWith(".mcd") || entAtt.ToLowerInvariant().EndsWith(".mdl")) entAtt = Path.GetDirectoryName(entAtt) ?? "";
            }
            log($"the entrance outfit ({(entAtt == att && entId == Str(req, "id") ? "this attire" + (entLeave.Count == 0 ? " with all its gear" : "") : entAtt)})...");
            var eres = new Port2k().Build(entId, entAtt, ework, game, budget, template: StrOr(req, "template") is { Length: > 0 } t2 ? t2 : null, log: log, print: log,
                                          packPrefix: modId + "_hd_entrance", leaveOut: entLeave, bridge: entBridge, packSize: hd is 2048 or 4096 or 8192 ? hd : 4096,
                                          use: (ent["use"] as JsonArray ?? []).Select(n => n!.GetValue<string>()));
            string entry = Entrance.AddTo(res.Pac, eres.Pac);
            entrancePages = eres.Rep["attires"]![0]!["pages"]!.GetValue<int>();
            pack.AddRange(eres.Pack);
            entranceLines.Add($"entrance outfit: {(entAtt == att && entId == Str(req, "id") ? "this attire" + (entLeave.Count == 0 ? " with all its gear" : "") : entAtt)} - " +
                              $"{entrancePages} memory pages, in ch.pac as {entry} (worn for the entrance, the ring attire after it)");
            entranceLines.AddRange(eres.Report.Where(l => l.StartsWith("checks:") || l.StartsWith("warning")).Select(l => "entrance " + l));
        }
        string final = Path.Combine(outDir, modId);
        Directory.CreateDirectory(final);
        File.Copy(res.Pac, Path.Combine(final, "ch.pac"), true);
        if (pack.Count > 0)
        {
            string tp = Path.Combine(final, "texture_pack");
            Directory.CreateDirectory(tp);
            foreach (var f in pack) File.Copy(f, Path.Combine(tp, Path.GetFileName(f)), true);
        }
        string name = StrOr(req, "mod_name") is { Length: > 0 } mn ? mn : AewName(req) ?? Path.GetFileName(Str(req, "id"));
        File.WriteAllText(Path.Combine(final, "manifest.txt"), Manifest(modId, name, "125", false, Path.Combine(final, "ch.pac")).Replace("\n", "\r\n"), new UTF8Encoding(false));   // (Windows text mode, as the Python wrote it)
        var pv = Preview.Render(Path.Combine(final, "ch.pac"), Path.Combine(outDir, modId + "_preview.png"));
        var a0 = res.Rep["attires"]![0]!;
        var report = res.Report;
        report.Add($"built: {a0["meshes"]} meshes, {a0["pages"]} memory pages");
        report.AddRange(entranceLines);
        string? epv = entrancePages != null ? Preview.Render(Path.Combine(final, "ch.pac"), Path.Combine(outDir, modId + "_entrance_preview.png"), attire: 1) : null;
        var exp = ExportSvrmod(final, outDir);
        report.Add($"saved as {Path.GetFileName(exp["svrmod"]!.GetValue<string>())} (the Mod Maker opens it to edit the name, stats, moves...)" +
                   (exp["texture_zip"] is JsonNode tz ? $"; HD textures: {Path.GetFileName(tz.GetValue<string>())} (unzip into the game's Texture Packs folder)" : ""));
        report.Add($"took {sw.Elapsed.TotalSeconds:F0} s");
        File.WriteAllText(Path.Combine(final, "portit_report.txt"), string.Join("\r\n", report), new UTF8Encoding(false));
        var checks = new JsonObject { ["face_over"] = res.Checks.FaceOver, ["gear_over"] = res.Checks.GearOver, ["notes"] = new JsonArray(res.Checks.Notes.Select(x => (JsonNode)x).ToArray()) };
        return new JsonObject
        {
            ["out"] = final, ["pac"] = Path.Combine(final, "ch.pac"), ["pages"] = a0["pages"]!.DeepClone(), ["report"] = new JsonArray(report.Select(x => (JsonNode)x).ToArray()),
            ["preview"] = pv, ["checks"] = checks, ["texture_pack"] = new JsonArray(pack.Select(f => (JsonNode)Path.GetFileName(f)).ToArray()),
            ["entrance_preview"] = epv, ["entrance_pages"] = entrancePages,
            ["svrmod"] = exp["svrmod"]!.DeepClone(), ["texture_zip"] = exp["texture_zip"]?.DeepClone(),
        };
    }

    // ---------------------------------------------------------------- install
    static void CopyTree(string src, string dst, Func<string, bool> skip)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) if (!skip(Path.GetFileName(f))) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src)) if (!skip(Path.GetFileName(d))) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)), skip);
    }

    JsonObject Install(JsonObject req)
    {
        string src = Str(req, "mod_dir"), game = Str(req, "game"), modId = Str(req, "mod_id");
        string dest = Path.Combine(game, "Mods", "Superstars", modId);
        if (Directory.Exists(dest) || File.Exists(dest))
        {
            string bak = Path.Combine(game, "Mods", "_portit_backup", modId + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
            Directory.Move(dest, bak);
        }
        CopyTree(src, dest, n => n == "texture_pack");
        var o = new JsonObject { ["installed"] = dest };
        foreach (var (k, v) in CopyTexturePack(src, game, modId)) o[k] = v?.DeepClone();
        return o;
    }

    /// <summary>the HD texture pack of a full-route port: into the first pack the game has enabled, else 'PortIT HD'
    /// (turned on in svr2011.toml)</summary>
    JsonObject CopyTexturePack(string src, string game, string modId)
    {
        var o = new JsonObject();
        string tp = Path.Combine(src, "texture_pack");
        if (!Directory.Exists(tp) || !Directory.EnumerateFileSystemEntries(tp).Any()) return o;
        var pack = FullPort.EnabledPack(game);
        if (pack == null)
        {
            try
            {
                if (FullPort.EnablePack(game, "PortIT HD")) { o["pack_turned_on"] = true; pack = "PortIT HD"; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"could not turn the texture pack on in svr2011.toml: {e.Message}"); }
        }
        o["pack_enabled"] = pack != null;
        pack ??= "PortIT HD";
        string pdir = Path.Combine(game, "Texture Packs", pack);
        Directory.CreateDirectory(pdir);
        foreach (var f in Directory.EnumerateFiles(pdir)) if (Path.GetFileName(f).StartsWith(modId + "_hd_")) File.Delete(f);
        foreach (var f in Directory.EnumerateFiles(tp)) File.Copy(f, Path.Combine(pdir, Path.GetFileName(f)), true);
        o["pack"] = pdir;
        return o;
    }

    // ---------------------------------------------------------------- the big-model DLL
    static string Sha(string p) { using var f = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(f)); }

    JsonObject BigModel(JsonObject req)
    {
        string game = Str(req, "game");
        if (!File.Exists(Path.Combine(game, "svr2011.exe"))) throw new ArgumentException($"svr2011.exe is not in {game} - choose the SvR 2011 game folder");
        string dst = Path.Combine(game, "xinput1_4.dll"), keep = dst + ".before_portit";
        string action = StrOr(req, "action") ?? "status";
        if (action is "install" or "remove" && Process.GetProcessesByName("svr2011").Length > 0)
            throw new InvalidOperationException("Close the game first (svr2011.exe is running): Windows keeps the DLL in use");
        if (action == "install")
        {
            if (File.Exists(dst) && Sha(dst) != Sha(BigModelDll) && !File.Exists(keep)) File.Copy(dst, keep);
            File.Copy(BigModelDll, dst, true);
        }
        else if (action == "remove")
        {
            if (File.Exists(dst) && Sha(dst) == Sha(BigModelDll))
            {
                File.Delete(dst);
                if (File.Exists(keep)) File.Move(keep, dst, true);
            }
        }
        string state = "none";
        if (File.Exists(dst)) state = File.Exists(BigModelDll) && Sha(dst) == Sha(BigModelDll) ? "portit" : "other";
        bool active = false; string note = "";
        string logf = Path.Combine(game, "bigmodel.log");
        if (File.Exists(logf))
        {
            var m = Regex.Matches(File.ReadAllText(logf), @"vertex windows[^:]*: *\d+ *-> *(\d+) *KB");
            active = m.Count > 0 && int.Parse(m[^1].Groups[1].Value) > 768;
            note = m.Count > 0 ? $"the last game start used it: {m[^1].Groups[1].Value} KB a wrestler" : "its log shows no model memory line yet";
        }
        string version = "";
        string rf = Path.Combine(game, "release_files.txt");
        if (File.Exists(rf)) version = (File.ReadLines(rf).FirstOrDefault() ?? "").Trim();
        return new JsonObject { ["installed"] = state, ["active"] = active, ["version"] = version, ["note"] = note, ["has_backup"] = File.Exists(keep) };
    }
}
