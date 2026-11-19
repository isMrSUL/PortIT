using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using PortIT.Engine.Update;

namespace PortIT;

/// <summary>
/// "A new PortIT is ready": what is new, and the choice - Update now / Later / Skip this version. Nothing changes
/// unless the user says yes (the user's choice, 2026-11-19). Update now downloads, checks and swaps the files
/// (Engine\Update\Updater), then starts the new PortIT and closes this one. A failure leaves the old version
/// exactly as it was and says so in plain words.
/// </summary>
public sealed class UpdateDialog : Window
{
    readonly Updater.Release rel;
    readonly Button update = new() { Content = "Update now", Classes = { "accent" }, MinWidth = 120 };
    readonly Button later = new() { Content = "Later", MinWidth = 90 };
    readonly Button skip = new() { Content = "Skip this version", MinWidth = 120 };
    readonly Button page = new() { Content = "Open the download page", IsVisible = false };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>"skip" when the user chose to skip this version, else null</summary>
    public string? Choice { get; private set; }

    public UpdateDialog(Updater.Release rel)
    {
        this.rel = rel;
        Title = "PortIT update";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        later.Click += (_, _) => Close();
        skip.Click += (_, _) => { Choice = "skip"; Close(); };
        page.Click += (_, _) => Community.Open(Updater.Page);
        update.Click += async (_, _) => await Run();
        // (tests only: PORTIT_UPDATE_AUTO=1 photographs this window to PORTIT_UPDATE_SHOT and presses Update now)
        if (Environment.GetEnvironmentVariable("PORTIT_UPDATE_AUTO") == "1")
            Opened += async (_, _) =>
            {
                await Task.Delay(1500);
                if (Environment.GetEnvironmentVariable("PORTIT_UPDATE_SHOT") is { Length: > 0 } shot)
                    try
                    {
                        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                        rtb.Render(this);
                        using var fs = System.IO.File.Create(shot);
                        rtb.Save(fs);
                    }
                    catch (Exception) { }
                await Run();
            };

        var notes = new TextBlock
        {
            Text = Clean(rel.Notes), TextWrapping = TextWrapping.Wrap, Classes = { "faint" },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"PortIT {rel.Version.ToString(3)} is ready", Classes = { "h" } },
                new TextBlock { TextWrapping = TextWrapping.Wrap,
                                Text = $"You have {ReportDialog.AppVersion}. Updating takes a minute: PortIT downloads the new version, " +
                                       "checks it, puts it in place and starts again. Your settings and your ports are not touched." },
                new Border
                {
                    Classes = { "well" }, Padding = new Thickness(12), MaxHeight = 320,
                    Child = new ScrollViewer { Content = notes },
                },
                status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { page, skip, later, update },
                },
            },
        };
    }

    /// <summary>the release notes as plain text (GitHub's markdown: headings, bold and links made readable)</summary>
    static string Clean(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "(no notes for this version)";
        var s = System.Text.RegularExpressions.Regex.Replace(md, @"\[([^\]]+)\]\([^)]+\)", "$1");   // [text](link) -> text
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?m)^#+\s*", "");                    // # headings
        s = s.Replace("**", "").Replace("`", "");
        return s.Trim();
    }

    async Task Run()
    {
        update.IsEnabled = later.IsEnabled = skip.IsEnabled = false;
        var say = new Progress<string>(t => { status.Text = t; status.Foreground = Brushes.White; });
        try
        {
            await Task.Run(() => Updater.InstallAsync(rel, AppContext.BaseDirectory, PortIT.Engine.Worker.LocalRoot, say, CancellationToken.None));
            status.Text = "Done - PortIT starts again now.";
            status.Foreground = Brushes.LightGreen;
            await Task.Delay(900);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(AppContext.BaseDirectory, "PortIT.exe"))
            {
                UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory,
            });
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception e)
        {
            // (plain words: "being used by another process" means nothing to most people)
            string why = e is UnauthorizedAccessException ? e.Message
                       : e is System.IO.InvalidDataException ? "The download was damaged or not PortIT - " + e.Message
                       : e is System.Net.Http.HttpRequestException or TaskCanceledException ? "Could not download it - check your internet connection."
                       : e is System.IO.IOException ? "A PortIT file is in use (another PortIT window, or an antivirus scan)."
                       : e.Message;
            status.Text = why + " Nothing was changed: you still have " + ReportDialog.AppVersion + ". Try again later, or download the new version from GitHub.";
            status.Foreground = new SolidColorBrush(Color.Parse("#FFD27A"));
            page.IsVisible = true;
            later.Content = "Close";
            later.IsEnabled = true;
        }
    }
}
