using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PortIT.Core;

/// <summary>PortIT 1.0.1 (2026-11-10): files unzipped from a downloaded zip carry Windows' "from the internet" mark
/// (the Zone.Identifier stream). Smart App Control / SmartScreen then refused the engine's unsigned files on users'
/// PCs - NumPy's DLLs ("Importing the numpy C-extensions failed") and the small helper programs ("ddshash failed").
/// The person already chose to run PortIT.exe; this does what ticking "Unblock" in a file's Properties does, for
/// PortIT's own folder only, once per version.</summary>
public static class Unblock
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DeleteFileW(string path);

    public static void OwnFiles()
    {
        try
        {
            string root = AppContext.BaseDirectory;
            string marker = Path.Combine(root, "Engine", ".unblocked");
            string version = typeof(Unblock).Assembly.GetName().Version?.ToString() ?? "";
            if (File.Exists(marker) && File.ReadAllText(marker) == version) return;
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // (\\?\: paths past 260 characters too - NumPy's deepest files are past it in a long install folder)
                string full = Path.GetFullPath(f);
                string longPath = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
                DeleteFileW(longPath + ":Zone.Identifier");     // (no mark: nothing to do, the call just fails)
            }
            File.WriteAllText(marker, version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // (a read-only folder: PortIT still runs; the user guide explains Unblock by hand)
        }
    }
}
