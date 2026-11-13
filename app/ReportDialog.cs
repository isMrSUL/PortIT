using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PortIT;

/// <summary>"Report a problem": a bug report with the model's download link, sent to SUL's private #portit-reports
/// Discord channel. It goes through a small relay (a Cloudflare Worker) that holds the Discord webhook, so PortIT
/// itself never carries it. The report is only what this window shows: the link, the text, the optional contact and
/// log, plus the PortIT version, the wrestler and the game. The model link is required - most bugs can only be fixed
/// with the model itself. Added in 1.3.2.</summary>
public sealed class ReportDialog : Window
{
    const string Relay = "https://portit-report.stopit-straw836.workers.dev/";
    const string RelayKey = "portit-report-v1";    // (not a secret: it only stops random web requests)
    const int MaxLog = 900_000;

    readonly TextBox link = new() { Watermark = "https://…  (Google Drive, MediaFire, Mega, Discord… where the model can be downloaded)" };
    readonly TextBox what = new()
    {
        Watermark = "What went wrong? e.g. \"the hair is black but should be blond\", \"the game crashes in the match\"",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, MaxLength = 1900,
    };
    readonly TextBox contact = new() { Watermark = "your Discord name, so SUL can message you (optional)", MaxLength = 100 };
    readonly CheckBox attachLog = new() { IsChecked = true, Content = "Attach PortIT's log of the last job (your Windows user name is hidden)" };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button send = new() { Content = "Send report", Classes = { "accent" }, MinWidth = 120 };
    readonly string wrestler, game, log;

    /// <param name="wrestler">the wrestler chosen in PortIT (may be empty)</param>
    /// <param name="game">where it comes from: "WWE 2K", "AEW Fight Forever"</param>
    /// <param name="log">the text to attach when the box is ticked (the last job's log and the report on screen)</param>
    public ReportDialog(string wrestler, string game, string log)
    {
        this.wrestler = wrestler;
        this.game = game;
        this.log = log;
        Title = "Report a problem";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        attachLog.IsEnabled = log.Length > 0;
        if (log.Length == 0) { attachLog.IsChecked = false; attachLog.Content = "Attach PortIT's log (no job has run yet)"; }

        var cancel = new Button { Content = "Cancel", MinWidth = 100 };
        cancel.Click += (_, _) => Close();
        send.Click += async (_, _) => await Send();

        Grid Row(string label, Control c)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Classes = { "label" } });
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Report a problem", Classes = { "h" } },
                new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "faint" },
                                Text = "Your report goes privately to SUL (the maker of PortIT), not to a public channel. " +
                                       "Most problems can only be fixed with the model itself, so a link to the model you ported is needed." },
                new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "faint" },
                                Text = "For a question rather than a bug, the #help channel on the Discord (" + Community.Short + ") is faster." },
                Row("Model link *", link),
                Row("What went wrong *", what),
                Row("Contact", contact),
                new TextBlock { Classes = { "faint" }, TextWrapping = TextWrapping.Wrap,
                                Text = "Sent with it: " + string.Join(" · ", new[]
                                {
                                    "PortIT " + AppVersion,
                                    wrestler.Length > 0 ? "wrestler: " + wrestler : "",
                                    game.Length > 0 ? "game: " + game : "",
                                }.Where(s => s.Length > 0)) },
                attachLog,
                status,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, send } },
            },
        };
    }

    public static string AppVersion
    {
        get
        {
            var v = typeof(ReportDialog).Assembly.GetName().Version;
            return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>(screenshots: Render.Report) fill the fields</summary>
    internal void ShotFill(string l, string w, string c)
    {
        link.Text = l;
        what.Text = w;
        contact.Text = c;
    }

    /// <summary>(tests: Render.Report with a send) press Send and return what the window then says</summary>
    internal async Task<string> ShotSend()
    {
        await Send();
        return status.Text ?? "";
    }

    static bool IsLink(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    void Say(string text, bool bad)
    {
        status.Text = text;
        status.Foreground = bad ? new SolidColorBrush(Color.Parse("#FFD27A")) : Brushes.LightGreen;
    }

    async Task Send()
    {
        string l = (link.Text ?? "").Trim(), w = (what.Text ?? "").Trim();
        if (!IsLink(l)) { Say("Add a link to the model you ported (it starts with https://). Upload it to Google Drive, MediaFire, Mega or Discord and paste the link.", true); return; }
        if (w.Length < 5) { Say("Say what went wrong.", true); return; }
        send.IsEnabled = false;
        Say("Sending…", false);
        var body = new System.Collections.Generic.Dictionary<string, string>
        {
            ["model_link"] = l, ["what_happened"] = w, ["version"] = AppVersion,
            ["game"] = game, ["wrestler"] = wrestler, ["contact"] = (contact.Text ?? "").Trim(),
        };
        if (attachLog.IsChecked == true && log.Length > 0) body["log"] = log.Length > MaxLog ? log[^MaxLog..] : log;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            using var msg = new HttpRequestMessage(HttpMethod.Post, Relay)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            msg.Headers.Add("X-PortIT-Key", RelayKey);
            using var res = await http.SendAsync(msg);
            string answer = await res.Content.ReadAsStringAsync();
            string text = "";
            try { text = JsonDocument.Parse(answer).RootElement.GetProperty("message").GetString() ?? ""; } catch (JsonException) { }
            if (res.IsSuccessStatusCode)
            {
                Say((text.Length > 0 ? text : "Report sent.") + " SUL will look at it - you can close this window.", false);
                return;
            }
            Say(text.Length > 0 ? text : $"The report was not sent (error {(int)res.StatusCode}). Try again later.", true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Say("Could not reach the report server. Check your internet connection and try again.", true);
        }
        send.IsEnabled = true;
    }

    /// <summary>the newest engine job's log plus the report on screen, with the user's own folder names hidden</summary>
    public static string CollectLog(string? jobLog, string screen)
    {
        var sb = new StringBuilder();
        if (screen.Trim().Length > 0) sb.Append("=== PortIT window ===\n").Append(screen.Trim()).Append("\n\n");
        if (jobLog != null && File.Exists(jobLog))
        {
            try { sb.Append("=== ").Append(Path.GetFileName(Path.GetDirectoryName(jobLog))).Append("\\worker.log ===\n").Append(File.ReadAllText(jobLog)); }
            catch (IOException) { }
        }
        return Hide(sb.ToString());
    }

    /// <summary>C:\Users\NAME\… -> %USERPROFILE%\…, and the user name itself</summary>
    public static string Hide(string s)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 3)
        {
            s = s.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                 .Replace(home.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        string user = Environment.UserName;
        if (user.Length >= 3) s = s.Replace(user, "USER", StringComparison.OrdinalIgnoreCase);
        return s;
    }
}
