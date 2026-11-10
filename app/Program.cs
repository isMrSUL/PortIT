using Avalonia;

namespace PortIT;

internal static class Program
{
    [System.STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--logo") return Render.Logo(args[1]);
        // (the engine asks for 2K26 archive work: Core/CakeSource.cs)
        if (args.Length == 3 && args[0] == "--cake") return Core.CakeSource.Run(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--shot")     // (--shot <png> [seconds] [wrestler name] [seconds after])
            return Render.Window(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 4, 1500, 920,
                                 args.Length > 3 ? args[3] : null,
                                 args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 20);
        if (args.Length == 4 && args[0] == "--shot-attire") return Render.Attire(args[1], args[2], args[3]);
        Core.Unblock.OwnFiles();     // (the downloaded zip's "from the internet" mark off PortIT's own files)
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
