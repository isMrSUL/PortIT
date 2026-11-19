using System.IO;
using System.Reflection;

namespace PortIT.Core;

/// <summary>
/// Where the Report and Suggest buttons send to. The address is NOT in the source code (the owner's wish,
/// 2026-11-19: nothing about his Cloudflare on GitHub). The build bakes it in from app\relay.local.txt, a file git
/// never sees (.gitignore). A build made from the GitHub source has no relay, and the two buttons say so instead
/// of sending to the owner's Discord - which is right for anyone's own copy.
/// </summary>
public static class Relay
{
    static string? address;
    static bool read;

    /// <summary>the relay's address, or null in a build that has none</summary>
    public static string? Address
    {
        get
        {
            if (read) return address;
            read = true;
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("relay.txt");
                if (s != null)
                {
                    string a = new StreamReader(s).ReadToEnd().Trim();
                    if (a.StartsWith("https://")) address = a;
                }
            }
            catch (IOException) { }
            return address;
        }
    }

    public const string NotInThisBuild =
        "This copy of PortIT was built from the source code, so it cannot send to SUL. Use the official download from GitHub, or come to the Discord.";
}
