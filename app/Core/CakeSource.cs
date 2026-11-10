using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PortIT.Core;

/// <summary>The wrestlers inside a WWE 2K26 install (its bakedfile*.cak archives), read with BakeIT.Core (beside
/// PortIT.exe in Engine\cake, as AnimIT's Porter\cake) - read only, the game's files are never changed.
///   PortIT.exe --cake request.json response.json
///   {cmd: "index", game, cache}                       -> {ok, wrestlers: [{folder, name, attires: [rel .mcd]}]}
///   {cmd: "extract", game, cache, folder, attire}     -> {ok, path}  (Characters\folder: BaseModel + the attire)
/// A file in several archives: textures (.tex) the largest copy (bakedfile51 / 52 / 80 carry the high-resolution
/// ones, 61 / 100 smaller), anything else the highest-numbered archive (03 updates 01, 100 updates 61).
/// Textures come out as DDS (BakeIT's own conversion); sRGB DXGI codes are written as their plain twins (the same
/// blocks - the image readers PortIT uses know only those). Oodle (oo2core_9_win64.dll) is the user's own, from
/// the 2K26 folder: not shipped with PortIT.</summary>
public static class CakeSource
{
    const string Oodle = "oo2core_9_win64.dll";
    static Type? registry;
    static MethodInfo? open;

    static void Load(string game)
    {
        if (registry != null) return;
        string helper = Path.Combine(AppContext.BaseDirectory, "Engine", "cake");
        if (!File.Exists(Path.Combine(helper, "BakeIT.Core.dll")))
            throw new InvalidOperationException("Engine\\cake\\BakeIT.Core.dll is missing beside PortIT.exe.");
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string file = Path.Combine(helper, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        string oodle = new[] { Path.Combine(game, Oodle), Path.Combine(game, "cakeview", Oodle), Path.Combine(helper, Oodle) }
            .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException(
                "Reading 2K26 archives needs oo2core_9_win64.dll from your WWE 2K26 folder - choose the game folder itself.");
        NativeLibrary.Load(oodle);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(helper, "BakeIT.Core.dll"));
        registry = assembly.GetType("BakeIT.Core.CakeRegistryFile") ?? throw new InvalidOperationException("BakeIT.Core: no archive reader.");
        open = registry.GetMethods().Single(m => m.Name == "Open" && m.GetParameters().Length == 4);
    }

    static string[] Archives(string game) => Directory.GetFiles(game, "bakedfile*.cak", SearchOption.TopDirectoryOnly)
        .OrderBy(p => ArchiveNumber(p)).ToArray();

    static int ArchiveNumber(string path) => int.TryParse(Regex.Match(Path.GetFileNameWithoutExtension(path), @"\d+").Value, out int n) ? n : 0;

    /// <summary>every Characters/ file: path -> (archive, size) by the rules above; cached per set of archives</summary>
    static Dictionary<string, (string Archive, long Size)> Index(string game, string cache)
    {
        var archives = Archives(game);
        if (archives.Length == 0) throw new InvalidOperationException("No bakedfile*.cak in " + game + " - choose the WWE 2K26 game folder.");
        string stamp = string.Join("|", archives.Select(a => Path.GetFileName(a) + ":" + new FileInfo(a).Length + ":" + File.GetLastWriteTimeUtc(a).Ticks));
        string file = Path.Combine(cache, "index_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..16] + ".tsv");
        var best = new Dictionary<string, (string Archive, long Size)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(file))
        {
            foreach (string line in File.ReadLines(file))
            {
                var f = line.Split('\t');
                if (f.Length == 3) best[f[0]] = (f[1], long.Parse(f[2]));
            }
            return best;
        }
        Load(game);
        foreach (string archive in archives)
        {
            object reader = open!.Invoke(null, new object?[] { archive, null, false, true })!;
            using var lifetime = (IDisposable)reader;
            var enumerate = reader.GetType().GetMethod("EnumerateFiles") ?? registry!.GetMethod("EnumerateFiles")!;
            foreach (object item in (IEnumerable)enumerate.Invoke(reader, null)!)
            {
                var t = item.GetType();
                string path = ((string)t.GetProperty("Path")!.GetValue(item)!).Replace('\\', '/');
                if (!path.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)) continue;
                long size = Convert.ToInt64(t.GetProperty("ExpandedSize")!.GetValue(item));
                string name = Path.GetFileName(archive);
                if (!best.TryGetValue(path, out var had)) best[path] = (name, size);
                else if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ? size > had.Size || (size == had.Size && ArchiveNumber(name) > ArchiveNumber(had.Archive))
                                                                                   : ArchiveNumber(name) > ArchiveNumber(had.Archive))
                    best[path] = (name, size);
            }
        }
        Directory.CreateDirectory(cache);
        File.WriteAllLines(file + ".partial", best.Select(kv => kv.Key + "\t" + kv.Value.Archive + "\t" + kv.Value.Size));
        File.Move(file + ".partial", file, true);
        return best;
    }

    static object Wrestlers(Dictionary<string, (string Archive, long Size)> index)
    {
        var chars = new SortedDictionary<string, (bool Base, SortedSet<string> Attires)>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in index.Keys)
        {
            var parts = path.Split('/');
            if (parts.Length < 4 || !parts[^1].EndsWith(".mcd", StringComparison.OrdinalIgnoreCase)) continue;
            if (!chars.TryGetValue(parts[1], out var c)) chars[parts[1]] = c = (false, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
            if (parts.Length == 4 && parts[2].Equals("BaseModel", StringComparison.OrdinalIgnoreCase) && parts[3].Equals("baseModel.mcd", StringComparison.OrdinalIgnoreCase))
                chars[parts[1]] = (true, c.Attires);
            else if (parts.Length == 4 && !parts[2].Equals("BaseModel", StringComparison.OrdinalIgnoreCase))
                c.Attires.Add(parts[2] + "\\" + parts[3]);
        }
        return chars.Where(kv => kv.Value.Base && kv.Value.Attires.Count > 0).Select(kv => new
        {
            folder = kv.Key,
            name = Regex.Replace(Regex.Replace(kv.Key, @"^\d+_", ""), "_", " "),
            attires = kv.Value.Attires.ToArray(),
        }).ToList();
    }

    static string Extract(string game, string cache, Dictionary<string, (string Archive, long Size)> index, string folder, string attire)
    {
        string attireDir = attire.Replace('/', '\\').Split('\\')[0];
        var prefixes = new List<string> { $"Characters/{folder}/BaseModel/", $"Characters/{folder}/{attireDir}/" };
        // (PortIT 2026-11-10: an attire that is only a model - Tama Tonga's 1220_FacePaint holds just its .mcd / .ycl -
        //  wears the textures and materials of a sibling attire: the Default one, else the first with textures; that
        //  attire's Textures / Materials come out too, beside it, for the port's fallback)
        string charPrefix = $"Characters/{folder}/";
        bool ownTextures = index.Keys.Any(k => k.StartsWith($"{charPrefix}{attireDir}/Textures/", StringComparison.OrdinalIgnoreCase));
        if (!ownTextures)
        {
            var donors = index.Keys.Where(k => k.StartsWith(charPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k.Substring(charPrefix.Length).Split('/'))
                .Where(p => p.Length >= 3 && p[1].Equals("Textures", StringComparison.OrdinalIgnoreCase)
                            && !p[0].Equals("BaseModel", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[0]).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d.EndsWith("Default_Attire", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(d => d).ToList();
            if (donors.Count > 0)
            {
                prefixes.Add($"{charPrefix}{donors[0]}/Textures/");
                prefixes.Add($"{charPrefix}{donors[0]}/Materials/");
            }
        }
        // (what PortIT reads: models, material lists, textures - not the cloth data (.ycl) and not the few entries
        //  some archives list under a doubled path, Characters/X/Y/Characters/X/Y/..., which do not extract)
        string[] keep = { ".mcd", ".jmtl", ".mtls", ".tex" };
        var want = index.Where(kv => prefixes.Any(p => kv.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                                     && keep.Contains(Path.GetExtension(kv.Key), StringComparer.OrdinalIgnoreCase)
                                     && kv.Key.IndexOf("/Characters/", 1, StringComparison.OrdinalIgnoreCase) < 0).ToList();
        if (want.Count == 0) throw new InvalidOperationException($"{folder} / {attireDir}: not in the archives.");
        // (PortIT 2026-11-10: the textures 2K26 shares between wrestlers - Characters/_Shared, 45 files / 106 MB: the
        //  ula necklace of Umaga and Tama Tonga, the ring crew's outfits - extracted once, beside the wrestlers; the
        //  port finds them through the materials' hashes)
        var shared = index.Where(kv => kv.Key.StartsWith("Characters/_Shared/", StringComparison.OrdinalIgnoreCase)
                                       && keep.Contains(Path.GetExtension(kv.Key), StringComparer.OrdinalIgnoreCase)).ToList();
        if (shared.Count > 0) ExtractTo(game, Path.Combine(cache, "chars", "_Shared"), "_Shared", shared, Path.Combine(cache, "chars", "_Shared", ".done"));
        string root = Path.Combine(cache, "chars", folder);
        string stampFile = Path.Combine(root, "." + attireDir + ".done");
        ExtractTo(game, root, folder, want, stampFile);
        return root;
    }

    /// <summary>archive entries under Characters/{folder}/ -> root (DDS from .tex, sRGB codes as their plain twins);
    /// skipped when stampFile already lists exactly these entries</summary>
    static void ExtractTo(string game, string root, string folder, List<KeyValuePair<string, (string Archive, long Size)>> want, string stampFile)
    {
        string stamp = string.Join("\n", want.OrderBy(kv => kv.Key).Select(kv => kv.Key + "\t" + kv.Value.Archive + "\t" + kv.Value.Size));
        if (File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp) return;
        Load(game);
        var exf = registry!.GetMethods().Single(m => m.Name == "ExtractFile" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(Stream));
        foreach (var group in want.GroupBy(kv => kv.Value.Archive))
        {
            object reader = open!.Invoke(null, new object?[] { Path.Combine(game, group.Key), null, false, false })!;
            using var lifetime = (IDisposable)reader;
            foreach (var kv in group)
            {
                string rel = kv.Key.Substring(("Characters/" + folder + "/").Length);
                if (rel.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) rel = rel[..^4] + ".dds";
                string dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An archive path escapes the cache: " + kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var ms = new MemoryStream();
                if (!(bool)exf.Invoke(reader, new object[] { kv.Key, ms })!)
                {
                    if (kv.Key.EndsWith(".mcd", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Could not extract " + kv.Key);
                    continue;   // (a texture / material that won't extract: the port reports a missing texture, not a failure)
                }
                byte[] data = ms.ToArray();
                if (data.Length >= 148 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[84] == 'D' && data[85] == 'X' && data[86] == '1' && data[87] == '0')
                {
                    uint fmt = BitConverter.ToUInt32(data, 128);
                    uint plain = fmt switch { 72 => 71, 75 => 74, 78 => 77, 29 => 28, 91 => 87, 93 => 88, 99 => 98, _ => fmt };
                    if (plain != fmt) BitConverter.GetBytes(plain).CopyTo(data, 128);
                }
                File.WriteAllBytes(dest, data);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
        File.WriteAllText(stampFile, stamp);
    }

    public static int Run(string requestFile, string responseFile)
    {
        try
        {
            using var request = JsonDocument.Parse(File.ReadAllText(requestFile));
            var r = request.RootElement;
            string cmd = r.GetProperty("cmd").GetString()!, game = r.GetProperty("game").GetString()!, cache = r.GetProperty("cache").GetString()!;
            var index = Index(game, cache);
            object result = cmd switch
            {
                "index" => new { ok = true, wrestlers = Wrestlers(index) },
                "extract" => new { ok = true, path = Extract(game, cache, index, r.GetProperty("folder").GetString()!, r.GetProperty("attire").GetString()!) },
                _ => throw new InvalidOperationException("unknown cake command " + cmd),
            };
            File.WriteAllText(responseFile, JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(responseFile, JsonSerializer.Serialize(new { ok = false, error = (e.InnerException ?? e).Message }));
            return 1;
        }
    }
}
