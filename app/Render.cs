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

    public static int Window(string outPath, double seconds, int w, int h)
    {
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var win = new MainWindow { Width = w, Height = h };
        win.Show();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(30);
        }
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = win.CaptureRenderedFrame();
        if (frame == null) return 1;
        frame.Save(outPath);
        return 0;
    }
}
