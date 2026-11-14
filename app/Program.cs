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
        if (args.Length >= 2 && args[0] == "--shot")     // (--shot <png> [seconds] [wrestler name] [seconds after] [width] [height])
            return Render.Window(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 4,
                                 args.Length > 5 ? int.Parse(args[5]) : 1500,
                                 args.Length > 6 ? int.Parse(args[6]) : 920,
                                 args.Length > 3 && args[3].Length > 0 ? args[3] : null,
                                 args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 20);
        // (developers: run one engine request from a file, exactly as the window does - for testing ports without the UI)
        if (args.Length == 3 && args[0] == "--run")
        {
            var req = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(args[1]))!.AsObject();
            var res = new Engine.Worker(System.Console.WriteLine) { Cake = Core.CakeSource.Run }.Run(req);
            System.IO.File.WriteAllText(args[2], res.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return res["ok"]?.GetValue<bool>() == true ? 0 : 1;
        }
        // (developers: the body scale and weight class PortIT reads out of a game's chEtc.pac - checked against
        //  the Mod Maker tools' own reader when the height feature was built)
        if (args.Length == 2 && args[0] == "--sizes")
        {
            var sz = Engine.Port.Sizing.BaseSizes(args[1]);
            var keys = new System.Collections.Generic.List<int>(sz.Keys);
            keys.Sort();
            foreach (int id in keys) System.Console.WriteLine($"{id}\t{sz[id].Scale}\t{sz[id].WeightClass}");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--shot-3d") return Render.View3D(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--shot-discord") return Render.Discord(args[1]);
        if (args.Length >= 2 && args[0] == "--shot-report") return Render.Report(args[1], args.Length > 2 && args[2] == "send", args.Length > 3 ? args[3] : null);
        if (args.Length == 4 && args[0] == "--shot-attire") return Render.Attire(args[1], args[2], args[3]);
        Core.Unblock.OwnFiles();     // (the downloaded zip's "from the internet" mark off PortIT's own files)
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
