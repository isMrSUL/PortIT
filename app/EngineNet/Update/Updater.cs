using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PortIT.Engine.Update;

/// <summary>
/// PortIT keeps itself up to date (the user's request 2026-11-19: "no need to open GitHub and download every time").
///
///   1. at start-up, ask GitHub for the latest release (no account, no key; a failure says nothing)
///   2. a newer one: the app shows what is new and ASKS - Update now / Later / Skip this version
///   3. download the zip, and refuse it unless its SHA-256 matches the .sha256 published beside it
///   4. swap the files. A running program cannot be overwritten but it CAN be renamed, so each old file becomes
///      name.old, the new one takes its place, and the .old files are deleted on the next start. If anything fails
///      half-way, every file is put back as it was.
///
/// Only a real installed copy updates itself - the release zip puts "Read Me First.txt" beside PortIT.exe, a dev
/// build has none. The user's settings and ports live elsewhere (%LOCALAPPDATA%\PortIT, the output folder) and
/// are never touched.
///
/// For testing without publishing anything, PORTIT_UPDATE_FEED can name a local JSON file shaped like GitHub's
/// answer, whose download addresses may be local files.
/// </summary>
public static class Updater
{
    public const string Api = "https://api.github.com/repos/isMrSUL/PortIT/releases/latest";
    public const string Page = "https://github.com/isMrSUL/PortIT/releases/latest";

    public sealed record Release(Version Version, string Tag, string Notes, string ZipUrl, string ShaUrl, string ZipName);

    /// <summary>x.y.z, so 1.4.0 and 1.4.0.0 compare equal</summary>
    public static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>is this an installed copy that may update itself (not a developer's build)</summary>
    public static bool IsInstalledCopy(string root) => File.Exists(Path.Combine(root, "Read Me First.txt"));

    static HttpClient Http() =>
        new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10), DefaultRequestHeaders = { { "User-Agent", "PortIT-updater" } } };

    static bool IsLocal(string s) => !s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>the newest release, when it is newer than <paramref name="current"/>; null otherwise</summary>
    public static async Task<Release?> CheckAsync(Version current, CancellationToken ct)
    {
        string? feed = Environment.GetEnvironmentVariable("PORTIT_UPDATE_FEED");
        string json;
        if (!string.IsNullOrEmpty(feed) && IsLocal(feed)) json = await File.ReadAllTextAsync(feed, ct);
        else
        {
            using var http = Http();
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            json = await http.GetStringAsync(string.IsNullOrEmpty(feed) ? Api : feed, ct);
        }
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var dr) && dr.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) return null;
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v) || Norm(v) <= Norm(current)) return null;
        string? zip = null, zipName = null, sha = null;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "", url = a.GetProperty("browser_download_url").GetString() ?? "";
            // the app itself, not the optional big-model DLL
            if (name.StartsWith("PortIT-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) { zip = url; zipName = name; }
        }
        if (zip == null || zipName == null) return null;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
            if (string.Equals(a.GetProperty("name").GetString(), zipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                sha = a.GetProperty("browser_download_url").GetString();
        if (sha == null) return null;                    // (no checksum, no update: it could not be checked)
        string notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new Release(Norm(v), tag, notes, zip, sha, zipName);
    }

    static async Task Fetch(HttpClient http, string url, string dest, IProgress<string>? say, CancellationToken ct)
    {
        if (IsLocal(url)) { File.Copy(url, dest, true); return; }
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? -1, got = 0;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buf = new byte[1 << 16];
        int n, lastPct = -1;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            got += n;
            if (total > 0 && say != null)
            {
                int pct = (int)(100 * got / total);
                if (pct != lastPct) { lastPct = pct; say.Report($"Downloading… {pct}%  ({got / 1048576.0:F0} of {total / 1048576.0:F0} MB)"); }
            }
        }
    }

    /// <summary>download, check and put the new version in place over <paramref name="root"/>. Throws, with
    /// everything put back, when anything is wrong.</summary>
    public static async Task InstallAsync(Release rel, string root, string workRoot, IProgress<string>? say, CancellationToken ct)
    {
        string work = Path.Combine(workRoot, "update");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        string zipPath = Path.Combine(work, rel.ZipName), shaPath = zipPath + ".sha256";
        using (var http = Http())
        {
            await Fetch(http, rel.ZipUrl, zipPath, say, ct);
            await Fetch(http, rel.ShaUrl, shaPath, null, ct);
        }

        say?.Report("Checking the download…");
        string want = File.ReadAllText(shaPath).Trim().Split(' ', '\t')[0].Trim();
        string have;
        await using (var fs = File.OpenRead(zipPath)) have = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!string.Equals(want, have, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the download is damaged (its checksum does not match the published one) - nothing was changed");

        say?.Report("Unpacking…");
        string unpacked = Path.Combine(work, "new");
        ZipFile.ExtractToDirectory(zipPath, unpacked);
        // (the zip holds one folder, PortIT-x.y.z-win-x64\, with PortIT.exe in it)
        string? top = Directory.EnumerateFiles(unpacked, "PortIT.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault();
        if (top == null || !File.Exists(Path.Combine(top, "PortIT.Engine.dll")))
            throw new InvalidDataException("the download does not look like PortIT - nothing was changed");

        // can we write here at all (PortIT unzipped into Program Files, say)
        string probe = Path.Combine(root, ".portit_write_test");
        try { File.WriteAllText(probe, "x"); File.Delete(probe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new UnauthorizedAccessException("PortIT cannot write to its own folder - download the new version from GitHub by hand"); }

        say?.Report("Putting the new files in place…");
        var done = new List<(string Target, bool HadOld)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel2 = Path.GetRelativePath(top, f);
                string target = Path.GetFullPath(Path.Combine(root, rel2));
                if (!target.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("the download names a file outside PortIT's folder - nothing was changed");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool had = File.Exists(target);
                if (had)
                {
                    if (File.Exists(target + ".old")) File.Delete(target + ".old");
                    File.Move(target, target + ".old");          // (works on a running .exe / .dll; overwriting does not)
                }
                done.Add((target, had));
                File.Move(f, target);
            }
        }
        catch
        {
            // put every file back as it was
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (t, had) = done[i];
                try
                {
                    if (File.Exists(t)) File.Delete(t);
                    if (had && File.Exists(t + ".old")) File.Move(t + ".old", t);
                }
                catch (Exception) { }
            }
            throw;
        }
        try { Directory.Delete(work, true); } catch (Exception) { }
    }

    /// <summary>the .old files an update left behind (in use while the old version was still running)</summary>
    public static int CleanUp(string root)
    {
        int n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.old", SearchOption.AllDirectories))
                try { File.Delete(f); n++; } catch (Exception) { }
        }
        catch (Exception) { }
        return n;
    }
}
