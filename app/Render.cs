using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace PortIT;

/// <summary>Off-screen renders: the logo (pngs + the app icon) and a screenshot of the main window.</summary>
internal static class Render
{
    public static int Logo(string dir)
    {
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Directory.CreateDirectory(dir);
        var icon = new System.Collections.Generic.List<(int, byte[])>();
        foreach (int n in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256, 512 })
        {
            var mark = new Controls.LogoMark { Width = n, Height = n };
            mark.Measure(new Size(n, n));
            mark.Arrange(new Rect(0, 0, n, n));
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(n, n), new Vector(96, 96));
            bmp.Render(mark);
            string p = Path.Combine(dir, $"portit_{n}.png");
            bmp.Save(p);
            if (n is 16 or 24 or 32 or 48 or 256) icon.Add((n, File.ReadAllBytes(p)));
        }
        // the .ico: png-compressed entries
        using (var f = File.Create(Path.Combine(dir, "portit.ico")))
        using (var w = new BinaryWriter(f))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)icon.Count);
            int off = 6 + 16 * icon.Count;
            foreach (var (n, png) in icon)
            {
                w.Write((byte)(n >= 256 ? 0 : n)); w.Write((byte)(n >= 256 ? 0 : n)); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(off);
                off += png.Length;
            }
            foreach (var (_, png) in icon) w.Write(png);
        }
        Console.WriteLine("logo written to " + dir);
        return 0;
    }

    static void Pump(double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(30);
        }
    }

    /// <summary>the main window after `seconds` (the list loads by itself); pick: then a wrestler (name contains it)
    /// chosen and `after` more seconds for its meshes and preview - the user guide's screenshots</summary>
    public static int Window(string outPath, double seconds, int w, int h, string? pick = null, double after = 0)
    {
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var win = new MainWindow { Width = w, Height = h };
        win.Show();
        Pump(seconds);
        if (pick != null)
        {
            win.ShotPick(pick);
            Pump(after);
        }
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = win.CaptureRenderedFrame();
        if (frame == null) return 1;
        frame.Save(outPath);
        return 0;
    }

    /// <summary>the "Install as attire" window for the guide: stars = the engine's 'stars' answer (a json file),
    /// star = the superstar shown</summary>
    /// <summary>the 3D view window, driven by real (headless) mouse input: open, drag to turn, wheel to zoom - a png after
    /// each step (out_1 .. out_4)</summary>
    public static int View3D(string outPrefix, string modDir)
    {
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var w = new ViewerWindow(modDir, "test", false, false) { Width = 1000, Height = 760 };
        w.Show();
        Pump(3);
        void Shot(int n) { Pump(0.6); var f = w.CaptureRenderedFrame(); f?.Save($"{outPrefix}_{n}.png"); }
        Shot(1);
        var mid = new Point(500, 400);
        w.MouseDown(mid, Avalonia.Input.MouseButton.Left);
        for (int i = 1; i <= 10; i++) { w.MouseMove(new Point(500 + 15 * i, 400 + 3 * i)); Pump(0.05); }
        w.MouseUp(new Point(650, 430), Avalonia.Input.MouseButton.Left);
        Shot(2);
        for (int i = 0; i < 8; i++) { w.MouseWheel(new Point(500, 180), new Vector(0, 1)); Pump(0.05); }
        Shot(3);
        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.W, Avalonia.Input.RawInputModifiers.None);
        Shot(4);
        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.W, Avalonia.Input.RawInputModifiers.None);
        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.Home, Avalonia.Input.RawInputModifiers.None);
        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.H, Avalonia.Input.RawInputModifiers.None);
        Pump(1.2); Shot(5);
        Pump(0.7); Shot(6);
        // the "you turn it" test: J, then a quick drag (the hair should trail behind the turn)
        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.J, Avalonia.Input.RawInputModifiers.None);
        Pump(1.0); Shot(7);
        w.MouseDown(new Point(500, 400), Avalonia.Input.MouseButton.Left);
        for (int i = 1; i <= 12; i++) { w.MouseMove(new Point(500 + 18 * i, 400)); Pump(0.03); }
        w.MouseUp(new Point(716, 400), Avalonia.Input.MouseButton.Left);
        Pump(0.05); Shot(8);
        Pump(1.5); Shot(9);
        return 0;
    }

    public static int Attire(string outPath, string starsJson, string star)
    {
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(starsJson));
        var stars = StarSlots.From(doc.RootElement);
        var dlg = new AttireDialog(stars, "ADAM COLE", 394) { Height = 640 };
        dlg.Show();
        dlg.ShotPick(star);
        Pump(1.5);
        var frame = dlg.CaptureRenderedFrame();
        if (frame == null) return 1;
        frame.Save(outPath);
        return 0;
    }
}
