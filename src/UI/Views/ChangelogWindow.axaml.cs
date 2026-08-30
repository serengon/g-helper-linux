using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

// Renders the immutable CHANGELOG.md embedded at build time. This hardened
// fork never fetches release notes from a moving branch.
public partial class ChangelogWindow : Window
{
    private const string ChangelogBrowserUrl =
        "https://github.com/utajum/g-helper-linux/blob/9e99e21153a7cf75dd1682425bf1bd3d1b1de43a/CHANGELOG.md";
    private const string EmbeddedResourceName = "GHelper.Linux.CHANGELOG.md";

    private readonly List<Bitmap> _bitmapSink = new();

    public ChangelogWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        Loaded += (_, _) => LoadEmbeddedChangelog();
        Closed += (_, _) => DisposeBitmaps();
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("changelog_title");
        labelTitle.Text = Labels.Get("changelog_title");
        buttonOpenBrowser.Content = Labels.Get("changelog_open_browser");
        labelLoading.Text = Labels.Get("changelog_loading");
    }

    private void LoadEmbeddedChangelog()
    {
        string? markdown = LoadEmbedded();
        if (markdown == null)
        {
            ShowError(Labels.Get("changelog_load_failed"));
            return;
        }
        Logger.WriteLine($"ChangelogWindow: rendering embedded changelog ({markdown.Length} chars)");
        var blocks = ChangelogParser.Parse(markdown);
        Render(blocks);
    }

    private static string? LoadEmbedded()
    {
        try
        {
            var asm = typeof(ChangelogWindow).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
            {
                Logger.WriteLine($"ChangelogWindow: embedded resource '{EmbeddedResourceName}' not found");
                return null;
            }
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"ChangelogWindow: embedded fallback failed: {ex.Message}");
            return null;
        }
    }

    private void Render(List<ChangelogBlock> blocks)
    {
        panelBody.Children.Clear();
        ChangelogRenderer.Render(blocks, panelBody, _bitmapSink);
    }

    private void ShowError(string message)
    {
        panelBody.Children.Clear();
        panelBody.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.Parse("#FF8080")),
            FontSize = 13,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void DisposeBitmaps()
    {
        lock (_bitmapSink)
        {
            foreach (var bmp in _bitmapSink)
            {
                try
                { bmp.Dispose(); }
                catch { }
            }
            _bitmapSink.Clear();
        }
    }

    private void ButtonOpenBrowser_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ChangelogBrowserUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"ChangelogWindow: open in browser failed: {ex.Message}");
        }
    }
}
