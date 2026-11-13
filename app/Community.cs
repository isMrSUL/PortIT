using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace PortIT;

/// <summary>SUL's Discord server: the one place the invite link lives. The window's Discord button opens it, and the
/// first start of 1.3.2 says the server exists (once per PC). Added 2026-11-13 at the user's request.</summary>
public static class Community
{
    /// <summary>the server's permanent invite (SUL Modding)</summary>
    public const string Discord = "https://discord.gg/EfprRmbEt2";

    public const string Short = "discord.gg/EfprRmbEt2";

    /// <summary>open a link in the person's own browser (never in PortIT)</summary>
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>"PortIT has a Discord now": shown once, on the first start after updating</summary>
    public static async Task Announce(Window owner) => await Dialog().ShowDialog(owner);

    /// <summary>(screenshots: Render.Discord)</summary>
    internal static Window Dialog()
    {
        var join = new Button { Content = "Open the Discord", Classes = { "accent" }, MinWidth = 150 };
        var later = new Button { Content = "Maybe later", MinWidth = 110 };
        var dlg = new Window
        {
            Title = "PortIT - we have a Discord now",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(22), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "PortIT has a Discord server", Classes = { "h" } },
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "SUL Modding is the home of PortIT and AnimIT. Come in for:",
                    },
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "•  help when a port goes wrong\n" +
                               "•  the newest versions, with a ping when one comes out\n" +
                               "•  a place to show the wrestlers you made\n" +
                               "•  asking for a port you would like to see",
                    },
                    new SelectableTextBlock
                    {
                        Text = Short, FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#7AA7FF")),
                    },
                    new TextBlock
                    {
                        Classes = { "faint" }, TextWrapping = TextWrapping.Wrap,
                        Text = "You can open it any time with the Discord button at the top of PortIT. This message is shown only once.",
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { later, join },
                    },
                },
            },
        };
        join.Click += (_, _) => { Open(Discord); dlg.Close(); };
        later.Click += (_, _) => dlg.Close();
        return dlg;
    }
}
