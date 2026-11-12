using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PortIT.Engine.Port;

namespace PortIT;

/// <summary>
/// The built model in 3D, turned with the mouse like Blender's viewport (the user's request 2026-11-12): left-drag
/// turns, right-drag (or Shift / middle) moves, the wheel zooms towards the mouse, double-click resets; number keys
/// for the front / side / top views. Drawn by the engine's Viewer3D (software, on all CPU cores) with the HD textures.
/// </summary>
public sealed class ViewerWindow : Window
{
    readonly string modDir;
    readonly bool hasEntrance;
    bool entrance;
    Viewer3D.Scene? scene;
    readonly Viewer3D.Camera cam = new();
    readonly Image view = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    readonly Border host = new() { Background = new SolidColorBrush(Color.Parse("#0C1018")), ClipToBounds = true, Focusable = true };
    readonly TextBlock info = new() { Foreground = new SolidColorBrush(Color.Parse("#98A2B5")), VerticalAlignment = VerticalAlignment.Center };
    readonly ToggleButton texBtn = new() { Content = "Textures", IsChecked = true };
    readonly ToggleButton lightBtn = new() { Content = "Lights", IsChecked = true };
    readonly ToggleButton wireBtn = new() { Content = "Wireframe" };
    readonly ToggleButton outfitBtn = new() { Content = "Entrance outfit" };
    readonly ToggleButton hairBtn = new() { Content = "Hair: head moves", IsEnabled = false };
    readonly ToggleButton turnBtn = new() { Content = "Hair: you turn it", IsEnabled = false };
    int hairMode;            // (0 off, 1 the head turns and nods, 2 the hair reacts as you turn the model)
    HairSim? sim;
    WriteableBitmap? bmp;
    bool pending, dragging;
    Point last;
    PointerUpdateKind dragKind;

    public ViewerWindow(string modDir, string title, bool hasEntrance, bool startOnEntrance)
    {
        this.modDir = modDir; this.hasEntrance = hasEntrance; entrance = hasEntrance && startOnEntrance;
        Title = "PortIT - 3D view - " + title;
        Width = 1100; Height = 850; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0A0D13"));

        Button B(string text, string tip, Action a) { var b = new Button { Content = text, Padding = new Thickness(10, 4) }; ToolTip.SetTip(b, tip); b.Click += (_, _) => { a(); Redraw(); host.Focus(); }; return b; }
        var bar = new WrapPanel { Margin = new Thickness(10, 8), ItemSpacing = 6, LineSpacing = 6 };
        bar.Children.Add(B("Front", "Look from the front (key 1)", () => View(0, 0)));
        bar.Children.Add(B("Side", "Look from his left side (key 3)", () => View(-Math.PI / 2, 0)));
        bar.Children.Add(B("Back", "Look from behind (Ctrl+1)", () => View(Math.PI, 0)));
        bar.Children.Add(B("Top", "Look from above (key 7)", () => View(0, -Math.PI / 2 + 0.01)));
        bar.Children.Add(B("Head", "A close look at the face (key F)", Head));
        bar.Children.Add(B("Reset", "The whole body again (Home, or double-click)", () => View(0, 0)));
        bar.Children.Add(new Border { Width = 12 });
        foreach (var t in new[] { texBtn, lightBtn, wireBtn }) { t.Padding = new Thickness(10, 4); t.IsCheckedChanged += (_, _) => { Sync(); Redraw(); }; bar.Children.Add(t); }
        ToolTip.SetTip(texBtn, "Show the colours (T)"); ToolTip.SetTip(lightBtn, "Shade by the light, or flat colours (L)");
        ToolTip.SetTip(wireBtn, "Draw the triangles' edges over the model, see-through (W)");
        hairBtn.Padding = new Thickness(10, 4);
        turnBtn.Padding = new Thickness(10, 4);
        ToolTip.SetTip(hairBtn, "The head turns and nods, the hair swings with the game's own hair maths (H) - for a port made with Moving hair");
        ToolTip.SetTip(turnBtn, "Turn the model yourself (left-drag, arrow keys): the hair swings and falls as he turns, like holding him in your hand (J)");
        hairBtn.IsCheckedChanged += (_, _) => { if (hairBtn.IsChecked == true) { turnBtn.IsChecked = false; HairTest(1); } else if (hairMode == 1) HairTest(0); };
        turnBtn.IsCheckedChanged += (_, _) => { if (turnBtn.IsChecked == true) { hairBtn.IsChecked = false; HairTest(2); } else if (hairMode == 2) HairTest(0); };
        bar.Children.Add(new Border { Width = 12 });
        bar.Children.Add(hairBtn);
        bar.Children.Add(turnBtn);
        if (hasEntrance)
        {
            outfitBtn.Padding = new Thickness(10, 4); outfitBtn.IsChecked = entrance;
            ToolTip.SetTip(outfitBtn, "Show what he walks out in instead of the ring attire");
            outfitBtn.IsCheckedChanged += (_, _) => { entrance = outfitBtn.IsChecked == true; hairBtn.IsChecked = turnBtn.IsChecked = false; _ = LoadScene(); };
            bar.Children.Add(new Border { Width = 12 });
            bar.Children.Add(outfitBtn);
        }
        var help = new TextBlock
        {
            Text = "Left-drag: turn   ·   Right-drag (or Shift+drag, middle-drag): move   ·   Wheel: zoom to the mouse   ·   Double-click: reset   ·   " +
                   "Keys: 1 front, 3 side, Ctrl+1 back, 7 top, F head, W wireframe, T textures, L lights, H hair (head moves), J hair (you turn it)",
            Foreground = new SolidColorBrush(Color.Parse("#646E82")), FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var bottom = new DockPanel { Margin = new Thickness(10, 6, 10, 8) };
        DockPanel.SetDock(info, Dock.Right);
        bottom.Children.Add(info);
        bottom.Children.Add(help);
        host.Child = view;
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(bottom); root.Children.Add(host);
        Content = root;

        host.SizeChanged += (_, _) => Redraw();
        host.PointerPressed += OnPress;
        host.PointerMoved += OnMove;
        host.PointerReleased += (_, e) => { if (dragging) { dragging = false; e.Pointer.Capture(null); Redraw(); } };
        host.PointerWheelChanged += OnWheel;
        host.DoubleTapped += (_, _) => { View(0, 0); Redraw(); };
        KeyDown += OnKey;
        Opened += async (_, _) => { host.Focus(); await LoadScene(); };
        Closed += (_, _) => hairMode = 0;
    }

    /// <summary>the hair test: 1 the head turns and nods, 2 the model turns as you drag it (the camera's turn becomes the
    /// model's, so gravity stays down the screen and the hair swings), 0 off (the built pose). The game's hair solver at a
    /// fixed 60 steps a second, driven by the window's frame callback.</summary>
    void HairTest(int mode)
    {
        hairMode = mode;
        loopGen++;
        if (scene == null || sim == null) return;
        sim.Pivot = [scene.Centre[0], scene.Centre[1], scene.Centre[2]];
        sim.Turn = mode == 2 ? HairSim.ViewTurn(cam.Yaw, cam.Pitch) : null;
        sim.Reset();
        if (mode == 0)
        {
            foreach (var p in scene.Parts) { p.PV = null; p.PN = null; }
            Redraw();
            return;
        }
        lastTick = null; carry = 0;
        int gen = ++loopGen;          // (one frame loop at a time: a mode switch starts a new one, the old one ends)
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(t => Tick(t, gen));
    }

    TimeSpan? lastTick;
    double carry;

    int loopGen;

    void Tick(TimeSpan now, int gen)
    {
        if (gen != loopGen || hairMode == 0 || scene == null || sim == null) return;
        double dt = lastTick == null ? 1 / 60.0 : Math.Min((now - lastTick.Value).TotalSeconds, 0.25);
        lastTick = now;
        carry += dt;
        int steps = 0;
        do
        {
            if (hairMode == 2) sim.Turn = HairSim.ViewTurn(cam.Yaw, cam.Pitch);
            sim.Step(hairMode == 1 ? 1.0 : 0.0);
            carry -= 1 / 60.0; steps++;
        } while (carry >= 1 / 60.0 && steps < 8);
        if (carry < 0) carry = 0;
        Viewer3D.Pose(scene, sim);
        Render();
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(t => Tick(t, gen));
    }

    void Sync() { cam.Textured = texBtn.IsChecked == true; cam.Lit = lightBtn.IsChecked == true; cam.Wire = wireBtn.IsChecked == true; }

    void View(double yaw, double pitch) { cam.Yaw = yaw; cam.Pitch = pitch; cam.Zoom = 1; cam.PanX = cam.PanY = 0; }

    void Head()
    {
        // (the head is near the top of the body: zoomed in, the picture moved down to it)
        cam.Yaw = 0; cam.Pitch = 0; cam.Zoom = 4.5;
        var (_, h) = PixelSize();
        cam.PanX = 0; cam.PanY = h * 0.92 * cam.Zoom * 0.42;
    }

    async Task LoadScene()
    {
        info.Text = "Loading…";
        string pac = Path.Combine(modDir, "ch.pac");
        string? dds = Viewer3D.HdColor(modDir, entrance);
        try
        {
            var s = await Task.Run(() => Viewer3D.Load(pac, entrance ? 1 : 0, dds));
            scene = s;
            sim = s.HairSettings != null ? await Task.Run(() => Viewer3D.Sim(s)) : null;
            hairBtn.IsEnabled = turnBtn.IsEnabled = sim != null && sim.Chains > 0;
            info.Text = $"{s.Verts:N0} points  ·  {s.Tris:N0} triangles  ·  texture {s.TW} x {s.TH}{(dds != null ? " (HD pack)" : "")}" +
                        (sim != null && sim.Chains > 0 ? $"  ·  moving hair: {sim.Chains} chains" : "");
        }
        catch (Exception e) { info.Text = "Could not read the model: " + e.Message; scene = null; }
        Redraw();
    }

    (int W, int H) PixelSize()
    {
        double k = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return (Math.Max(1, (int)(host.Bounds.Width * k)), Math.Max(1, (int)(host.Bounds.Height * k)));
    }

    void Redraw()
    {
        if (pending) return;
        pending = true;
        Dispatcher.UIThread.Post(Render, DispatcherPriority.Render);
    }

    void Render()
    {
        pending = false;
        if (scene == null) return;
        var (w, h) = PixelSize();
        if (w < 2 || h < 2) return;
        double k = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var drawCam = cam;
        if (hairMode == 2) { drawCam = cam.Clone(); drawCam.Yaw = 0; drawCam.Pitch = 0; }   // (the turn is in the model now)
        var px = Viewer3D.Draw(scene, w, h, drawCam, smooth: !dragging);
        if (bmp == null || bmp.PixelSize.Width != w || bmp.PixelSize.Height != h)
            bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96 * k, 96 * k), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        view.Source = null;          // (a new frame in the same bitmap: set again so the Image draws it)
        view.Source = bmp;
        view.Width = w / k; view.Height = h / k;
        view.InvalidateVisual();
    }

    void OnPress(object? sender, PointerPressedEventArgs e)
    {
        host.Focus();
        var pt = e.GetCurrentPoint(host);
        dragKind = pt.Properties.PointerUpdateKind;
        if (dragKind == PointerUpdateKind.LeftButtonPressed && (e.KeyModifiers & KeyModifiers.Shift) != 0) dragKind = PointerUpdateKind.MiddleButtonPressed;
        dragging = true; last = pt.Position;
        e.Pointer.Capture(host);
    }

    void OnMove(object? sender, PointerEventArgs e)
    {
        if (!dragging) return;
        var p = e.GetPosition(host);
        double dx = p.X - last.X, dy = p.Y - last.Y;
        last = p;
        if (dragKind == PointerUpdateKind.LeftButtonPressed)
        {
            cam.Yaw -= dx * 0.01;
            cam.Pitch = Math.Clamp(cam.Pitch - dy * 0.01, -1.55, 1.55);
        }
        else
        {
            double k = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            cam.PanX += dx * k; cam.PanY += dy * k;
        }
        Redraw();
    }

    void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        double f = Math.Pow(1.15, e.Delta.Y);
        double nz = Math.Clamp(cam.Zoom * f, 0.3, 40);
        f = nz / cam.Zoom;
        // (zoom towards the mouse: the point under it stays there)
        double k = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var (w, h) = PixelSize();
        var m = e.GetPosition(host);
        double mx = m.X * k - w / 2.0, my = m.Y * k - h / 2.0;
        cam.PanX = mx - (mx - cam.PanX) * f;
        cam.PanY = my - (my - cam.PanY) * f;
        cam.Zoom = nz;
        Redraw();
        e.Handled = true;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1: View(ctrl ? Math.PI : 0, 0); break;
            case Key.D3 or Key.NumPad3: View(ctrl ? Math.PI / 2 : -Math.PI / 2, 0); break;
            case Key.D7 or Key.NumPad7: View(0, ctrl ? Math.PI / 2 - 0.01 : -Math.PI / 2 + 0.01); break;
            case Key.Home: View(0, 0); break;
            case Key.F: Head(); break;
            case Key.W: wireBtn.IsChecked = wireBtn.IsChecked != true; return;
            case Key.T: texBtn.IsChecked = texBtn.IsChecked != true; return;
            case Key.L: lightBtn.IsChecked = lightBtn.IsChecked != true; return;
            case Key.H: if (hairBtn.IsEnabled) hairBtn.IsChecked = hairBtn.IsChecked != true; return;
            case Key.J: if (turnBtn.IsEnabled) turnBtn.IsChecked = turnBtn.IsChecked != true; return;
            case Key.Left: cam.Yaw += 0.2; break;
            case Key.Right: cam.Yaw -= 0.2; break;
            case Key.Up: cam.Pitch = Math.Clamp(cam.Pitch + 0.2, -1.55, 1.55); break;
            case Key.Down: cam.Pitch = Math.Clamp(cam.Pitch - 0.2, -1.55, 1.55); break;
            default: return;
        }
        e.Handled = true;
        Redraw();
    }
}
