using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GHelper.Linux.I18n;
using GHelper.Linux.Install;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// BIOS and Driver Updates window - Linux port of G-Helper's Updates form.
/// Queries ASUS ROG support API for BIOS and driver updates,
/// compares versions, shows download links.
/// 
/// API URLs:
///   BIOS:    https://rog.asus.com/support/webapi/product/GetPDBIOS?website=global&model={model}&cpu={model}&systemCode=rog
///   Drivers: https://rog.asus.com/support/webapi/product/GetPDDrivers?website=global&model={model}&cpu={model}&osid=52&systemCode=rog
/// 
/// Model comes from DMI BIOS version string split on ".": first part is model, second is BIOS version.
/// </summary>
public partial class UpdatesWindow : Window
{
    private static readonly IBrush ColorGreen = new SolidColorBrush(Color.Parse("#06B48A"));
    private static readonly IBrush ColorRed = new SolidColorBrush(Color.Parse("#FF2020"));
    private static readonly IBrush ColorGray = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush ColorWhite = new SolidColorBrush(Color.Parse("#F0F0F0"));
    private static readonly IBrush ColorDim = new SolidColorBrush(Color.Parse("#999999"));
    private static readonly IBrush RowBg1 = new SolidColorBrush(Color.Parse("#2A2A2A"));
    private static readonly IBrush RowBg2 = new SolidColorBrush(Color.Parse("#232323"));
    private static readonly Geometry ChevronCollapsed = Geometry.Parse("M 0,0 L 0,12 L 12,6 Z");
    private static readonly Geometry ChevronExpanded = Geometry.Parse("M 0,0 L 12,0 L 6,12 Z");

    private static readonly List<string> SkipList = new()
    {
        "Armoury Crate & Aura Creator Installer",
        "Armoury Crate Control Interface",
        "MyASUS",
        "ASUS Smart Display Control",
        "Aura Wallpaper",
        "Virtual Pet",
        "Virtual Pet- Ultimate Edition",
        "Virtual Assistant",
        "ROG Font V1.5",
    };


    private string? _model;
    private string? _biosVersion;
    private int _updatesCount;
    private bool _sysFilesExpanded;

    public UpdatesWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        Loaded += (_, _) =>
        {
            LoadUpdates();
        };
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("updates_title");
        labelTitle.Text = Labels.Get("updates_header");
        buttonDiagnostics.Content = Labels.Get("copy_diagnostics");
        buttonExportDiag.Content = Labels.Get("export_diagnostics");
        buttonRefresh.Content = Labels.Get("refresh");
        buttonChangelog.Content = Labels.Get("changelog_title");
        labelLegendUpToDate.Text = Labels.Get("up_to_date");
        labelLegendUpdateAvailable.Text = Labels.Get("update_available");
        labelLegendCantCheck.Text = Labels.Get("cant_check");
        labelGHelperSection.Text = Labels.Get("ghelper_linux");
        labelBiosSection.Text = Labels.Get("bios");
        labelDriversSection.Text = Labels.Get("drivers_software");
        labelSystemFilesSection.Text = Labels.Get("sysfiles_section");
        buttonSysFilesRecheck.Content = Labels.Get("sysfiles_recheck");
        buttonSysFilesFix.Content = Labels.Get("sysfiles_fix");
        buttonSysFilesUninstall.Content = Labels.Get("sysfiles_uninstall");
        buttonSysFilesUninstall.IsVisible = false;
    }

    private void ButtonRefresh_Click(object? sender, RoutedEventArgs e)
    {
        LoadUpdates();
    }

    private async void RefreshSystemFiles()
    {
        try
        {
            // One background status pass (spawns a sudo probe, hashes every
            // managed file); the panel build resumes on the UI thread.
            var results = await Task.Run(Installer.ComputeStatus);
            Installer.PopulateIntegrityPanel(panelSystemFiles, results,
                OnRepairOneAsync, OnRemoveOneAsync, OnShowDiffAsync);
            AppendSteamRow();
            _sysFilesExpanded = results.Any(r =>
                r.State != Installer.FileState.Ok &&
                r.State != Installer.FileState.Unknown &&
                r.State != Installer.FileState.NotApplicable &&
                r.State != Installer.FileState.Unavailable);
            ApplySysFilesExpanded();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.RefreshSystemFiles failed: {ex.Message}");
        }
    }

    /// <summary>Optional "Steam library shortcut" row at the bottom of the
    /// integrity list. Hidden when Steam is not installed. The toggle adds or
    /// removes G-Helper as a non-Steam app (SteamShortcuts); it is never part
    /// of Install / Repair and never triggers the auto-expand or the startup
    /// popup.</summary>
    private void AppendSteamRow()
    {
        if (!SteamShortcuts.IsSteamAvailable())
            return;

        bool added = SteamShortcuts.IsAdded();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Avalonia.Thickness(0, 2, 0, 2),
        };

        var mark = new TextBlock
        {
            Text = added ? "\u2713" : "\u2013",
            Foreground = added ? ColorGreen : ColorGray,
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        Grid.SetColumn(mark, 0);
        grid.Children.Add(mark);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        info.Children.Add(new TextBlock
        {
            Text = Labels.Get("sysfiles_name_steam"),
            FontSize = 13,
            Foreground = ColorWhite,
        });
        info.Children.Add(new TextBlock
        {
            Text = SteamShortcuts.UserdataPath() ?? "",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var status = new TextBlock
        {
            Text = added ? Labels.Get("sysfiles_status_ok") : "",
            FontSize = 11,
            Foreground = ColorGreen,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        // Content set to null so the default "On"/"Off" strings never show.
        var toggle = new ToggleSwitch
        {
            IsChecked = added,
            OnContent = null,
            OffContent = null,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        toggle.IsCheckedChanged += async (_, _) => await OnSteamToggleAsync(toggle);
        Grid.SetColumn(toggle, 3);
        grid.Children.Add(toggle);

        panelSystemFiles.Children.Add(grid);
    }

    private async Task OnSteamToggleAsync(ToggleSwitch toggle)
    {
        bool turnOn = toggle.IsChecked == true;
        if (turnOn == SteamShortcuts.IsAdded())
            return; // programmatic rebuild, no change

        toggle.IsEnabled = false;
        try
        {
            string error = "";
            bool ok = await Task.Run(() =>
                turnOn ? SteamShortcuts.Add(out error) : SteamShortcuts.Remove(out error));
            labelSysFilesResult.Text = ok
                ? Labels.Get(turnOn ? "steam_added" : "steam_removed")
                : Labels.Get("steam_failed");
            labelSysFilesResult.IsVisible = true;
            if (!string.IsNullOrEmpty(error))
                Helpers.Logger.WriteLine($"UpdatesWindow: Steam toggle error: {error}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.OnSteamToggleAsync failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("steam_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            RefreshSystemFiles();
        }
    }

    private async Task OnRepairOneAsync(Installer.ManagedFile file)
    {
        buttonSysFilesFix.IsEnabled = false;
        buttonSysFilesRecheck.IsEnabled = false;
        try
        {
            string msg = await Installer.RepairOneFromUiAsync(file);
            labelSysFilesResult.Text = msg;
            labelSysFilesResult.IsVisible = true;
            RefreshSystemFiles();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.OnRepairOneAsync failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("sysfiles_apply_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            buttonSysFilesFix.IsEnabled = true;
            buttonSysFilesRecheck.IsEnabled = true;
        }
    }

    private async Task OnRemoveOneAsync(Installer.ManagedFile file)
    {
        buttonSysFilesFix.IsEnabled = false;
        buttonSysFilesRecheck.IsEnabled = false;
        buttonSysFilesUninstall.IsEnabled = false;
        try
        {
            string msg = await Installer.RemoveOneFromUiAsync(file);
            labelSysFilesResult.Text = msg;
            labelSysFilesResult.IsVisible = true;
            RefreshSystemFiles();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.OnRemoveOneAsync failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("sysfiles_remove_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            buttonSysFilesFix.IsEnabled = true;
            buttonSysFilesRecheck.IsEnabled = true;
            buttonSysFilesUninstall.IsEnabled = true;
        }
    }

    private async Task OnShowDiffAsync(Installer.ManagedFile file)
    {
        try
        {
            await Installer.ShowDiffAsync(this, file);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.OnShowDiffAsync failed: {ex.Message}");
        }
    }

    private void ButtonSysFilesRecheck_Click(object? sender, RoutedEventArgs e)
    {
        labelSysFilesResult.IsVisible = false;
        RefreshSystemFiles();
    }

    private void SysFilesHeader_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _sysFilesExpanded = !_sysFilesExpanded;
        ApplySysFilesExpanded();
    }

    private void ApplySysFilesExpanded()
    {
        panelSysFilesBody.IsVisible = _sysFilesExpanded;
        iconSysFilesToggle.Data = _sysFilesExpanded ? ChevronExpanded : ChevronCollapsed;
    }

    private async void ButtonSysFilesFix_Click(object? sender, RoutedEventArgs e)
    {
        buttonSysFilesFix.IsEnabled = false;
        try
        {
            string msg = await Installer.RunFixFromUiAsync();
            labelSysFilesResult.Text = msg;
            labelSysFilesResult.IsVisible = true;
            RefreshSystemFiles();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.ButtonSysFilesFix failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("sysfiles_apply_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            buttonSysFilesFix.IsEnabled = true;
        }
    }

    private async void ButtonSysFilesUninstall_Click(object? sender, RoutedEventArgs e)
    {
        bool confirmed = await Dialogs.ConfirmDialog.ShowAsync(
            this,
            Labels.Get("sysfiles_uninstall_title"),
            Labels.Get("sysfiles_uninstall_message"));
        if (!confirmed)
            return;

        buttonSysFilesUninstall.IsEnabled = false;
        buttonSysFilesFix.IsEnabled = false;
        buttonSysFilesRecheck.IsEnabled = false;
        try
        {
            string msg = await Installer.RunRemoveFromUiAsync();
            // Expand the panel so the result line (which lives in the collapsed
            // body) is actually visible.
            _sysFilesExpanded = true;
            ApplySysFilesExpanded();
            labelSysFilesResult.Text = msg;
            labelSysFilesResult.IsVisible = true;
            RefreshSystemFiles();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.ButtonSysFilesUninstall failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("sysfiles_remove_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            buttonSysFilesUninstall.IsEnabled = true;
            buttonSysFilesFix.IsEnabled = true;
            buttonSysFilesRecheck.IsEnabled = true;
        }
    }

    private ChangelogWindow? _changelogWindow;

    private void ButtonChangelog_Click(object? sender, RoutedEventArgs e)
    {
        if (_changelogWindow is { IsVisible: true })
        {
            _changelogWindow.Activate();
            return;
        }
        _changelogWindow = new ChangelogWindow();
        _changelogWindow.Closed += (_, _) => _changelogWindow = null;
        _changelogWindow.Show(this);
    }

    private async void ButtonDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            buttonDiagnostics.IsEnabled = false;
            buttonDiagnostics.Content = Labels.Get("collecting");

            var report = await Task.Run(() => Helpers.Diagnostics.GenerateReport());

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(report);
                buttonDiagnostics.Content = Labels.Get("copied");
                Helpers.Logger.WriteLine($"Diagnostics: copied {report.Length} chars to clipboard");
            }
            else
            {
                buttonDiagnostics.Content = Labels.Get("clipboard_unavailable");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Diagnostics failed: {ex.Message}");
            buttonDiagnostics.Content = Labels.Get("failed");
        }

        // Reset button after 2 seconds
        _ = Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                buttonDiagnostics.Content = Labels.Get("copy_diagnostics");
                buttonDiagnostics.IsEnabled = true;
            }));
    }

    private async void ButtonExportDiag_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            buttonExportDiag.IsEnabled = false;
            buttonExportDiag.Content = Labels.Get("collecting");

            var report = await Task.Run(() => Helpers.Diagnostics.GenerateReport());

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var suggestedName = $"ghelper-diagnostics-{timestamp}.txt";

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { } storage)
            {
                buttonExportDiag.Content = Labels.Get("failed");
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Labels.Get("export_diagnostics"),
                SuggestedFileName = suggestedName,
                DefaultExtension = "txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Text files") { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } },
                },
            });

            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new System.IO.StreamWriter(stream);
                await writer.WriteAsync(report);
                buttonExportDiag.Content = Labels.Get("saved");
                Helpers.Logger.WriteLine($"Diagnostics: exported {report.Length} chars to {file.Name}");
            }
            else
            {
                buttonExportDiag.Content = Labels.Get("export_diagnostics");
                buttonExportDiag.IsEnabled = true;
                return;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Diagnostics export failed: {ex.Message}");
            buttonExportDiag.Content = Labels.Get("failed");
        }

        _ = Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                buttonExportDiag.Content = Labels.Get("export_diagnostics");
                buttonExportDiag.IsEnabled = true;
            }));
    }

    private void LoadUpdates()
    {
        bool isLenovo = Helpers.AppConfig.IsLenovoDevice();

        // Get model and BIOS from DMI sysfs
        var biosRaw = App.System?.GetBiosVersion() ?? "";
        if (isLenovo)
        {
            // Lenovo: the support catalog is keyed by machine type (DMI
            // product_name, e.g. "83DX"); BIOS version is the whole DMI
            // string (e.g. "NZCN26WW"), not a dot-separated pair.
            _model = Helpers.AppConfig.GetModel();
            _biosVersion = string.IsNullOrWhiteSpace(biosRaw) ? null : biosRaw.Trim();
        }
        else
        {
            var parts = biosRaw.Split('.');
            if (parts.Length >= 2)
            {
                _model = parts[0];
                _biosVersion = parts[1];
            }
            else
            {
                _model = biosRaw;
                _biosVersion = null;
            }
        }

        var modelName = App.System?.GetModelName() ?? Labels.Get("unknown");
        Title = Labels.Format("updates_title_format", modelName, _model ?? "", _biosVersion ?? "?");
        labelAppVersion.Text = Labels.Format("app_version_format", Helpers.AppConfig.AppVersion, modelName);

        _updatesCount = 0;
        labelUpdates.Text = Labels.Get("checking");
        labelUpdates.Foreground = ColorGreen;

        // Clear tables
        panelBios.Children.Clear();
        panelBios.Children.Add(new TextBlock { Text = Labels.Get("loading_bios"), Foreground = ColorDim, FontSize = 12 });
        panelDrivers.Children.Clear();
        panelDrivers.Children.Add(new TextBlock { Text = Labels.Get("loading_drivers"), Foreground = ColorDim, FontSize = 12 });

        ShowPackageManagedStatus();

        if (isLenovo)
        {
            // One catalog call covers BIOS and drivers.
            Task.Run(async () => await FetchLenovoUpdatesAsync());
            return;
        }

        string rogParam = Helpers.AppConfig.IsROG() ? "&systemCode=rog" : "";

        // Fetch BIOS
        Task.Run(async () =>
        {
            await FetchDriversAsync(
                $"https://rog.asus.com/support/webapi/product/GetPDBIOS?website=global&model={_model}&cpu={_model}{rogParam}",
                isBios: true);
        });

        // Fetch Drivers
        Task.Run(async () =>
        {
            await FetchDriversAsync(
                $"https://rog.asus.com/support/webapi/product/GetPDDrivers?website=global&model={_model}&cpu={_model}&osid=52{rogParam}",
                isBios: false);
        });
    }

    private void ShowPackageManagedStatus()
    {
        panelSelfUpdate.Children.Clear();
        panelSelfUpdate.Children.Add(labelSelfUpdateStatus);
        labelSelfUpdateStatus.Text =
            $"G-Helper X13 v{Helpers.AppConfig.AppVersion}: {Labels.Get("udev_not_installed")}";
        labelSelfUpdateStatus.Foreground = ColorDim;
        Helpers.Logger.WriteLine("Package management: hardened installer unavailable; no runtime self-update");
    }

    private struct DriverInfo
    {
        public string Category;
        public string Title;
        public string Version;
        public string DownloadUrl;
        public string Date;
    }

    private async Task FetchDriversAsync(string url, bool isBios)
    {
        var panel = isBios ? panelBios : panelDrivers;

        try
        {
            Helpers.Logger.WriteLine($"Updates: fetching {url}");

            using var httpClient = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            });
            httpClient.DefaultRequestHeaders.Add("User-Agent", "G-Helper-Linux/1.0");
            httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var json = await httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement;
            var result = data.GetProperty("Result");

            // Fallback for bugged API (empty result)
            JsonDocument? doc2 = null;
            if (result.ToString() == "" || !result.TryGetProperty("Obj", out var objProp)
                || objProp.ValueKind != System.Text.Json.JsonValueKind.Array || objProp.GetArrayLength() == 0)
            {
                var urlFallback = url + "&tag=" + new Random().Next(10, 99);
                Helpers.Logger.WriteLine($"Updates: retrying with fallback {urlFallback}");
                json = await httpClient.GetStringAsync(urlFallback);
                doc2 = JsonDocument.Parse(json);
                data = doc2.RootElement;
            }

            var resultObj = data.GetProperty("Result");
            var drivers = new List<DriverInfo>();

            if (!resultObj.TryGetProperty("Obj", out var groups)
                || groups.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                // API returned no data for this model (non-ASUS hardware, unknown model)
                groups = default;
            }

            for (int i = 0; i < (groups.ValueKind == System.Text.Json.JsonValueKind.Array ? groups.GetArrayLength() : 0); i++)
            {
                var categoryName = groups[i].GetProperty("Name").GetString() ?? "";
                var files = groups[i].GetProperty("Files");
                string? oldTitle = null;

                for (int j = 0; j < files.GetArrayLength(); j++)
                {
                    var file = files[j];
                    var title = file.GetProperty("Title").GetString() ?? "";

                    if (title != oldTitle && !SkipList.Contains(title))
                    {
                        var version = (file.GetProperty("Version").GetString() ?? "").Replace("V", "");
                        var downloadUrl = "";
                        if (file.TryGetProperty("DownloadUrl", out var dlProp) &&
                            dlProp.TryGetProperty("Global", out var globalProp))
                        {
                            downloadUrl = globalProp.GetString() ?? "";
                        }
                        var date = file.GetProperty("ReleaseDate").GetString() ?? "";

                        drivers.Add(new DriverInfo
                        {
                            Category = categoryName,
                            Title = title,
                            Version = version,
                            DownloadUrl = downloadUrl,
                            Date = date,
                        });
                    }
                    oldTitle = title;
                }
            }

            // Compare versions for BIOS entries
            int localUpdates = 0;
            foreach (var driver in drivers)
            {
                int status = 0; // 0 = can't check, 1 = newer available, -1 = up to date
                string tooltip = driver.Version;

                if (isBios && !driver.Title.Contains("Firmware") && _biosVersion != null)
                {
                    try
                    {
                        int remote = int.Parse(driver.Version);
                        int local = int.Parse(_biosVersion);
                        status = remote > local ? 1 : -1;
                        tooltip = Labels.Format("download_tooltip", driver.Version, _biosVersion);
                    }
                    catch
                    {
                        status = 0;
                    }
                }
                else if (!isBios)
                {
                    // On Linux we can't easily check installed driver versions via WMI,
                    // so we show them all as "can't check" (gray) - user can click to download
                    status = 0;
                }

                if (status == 1)
                    localUpdates++;

                // Must capture for closure
                var d = driver;
                int s = status;
                string t = tooltip;

                Dispatcher.UIThread.Post(() => AddDriverRow(panel, d, s, t));
            }

            if (localUpdates > 0)
            {
                _updatesCount += localUpdates;
                Dispatcher.UIThread.Post(() =>
                {
                    labelUpdates.Text = Labels.Format("updates_available_format", _updatesCount);
                    labelUpdates.Foreground = ColorRed;
                    labelUpdates.FontWeight = FontWeight.Bold;
                });
            }

            // Clear loading label
            Dispatcher.UIThread.Post(() =>
            {
                // Remove the "Loading..." text if it's still there
                var loading = isBios ? labelBiosLoading : labelDriversLoading;
                if (panel.Children.Contains(loading))
                    panel.Children.Remove(loading);

                if (drivers.Count == 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = Labels.Get("no_entries"),
                        Foreground = ColorDim,
                        FontSize = 12
                    });
                }

                // Update header if no updates found
                if (_updatesCount == 0)
                {
                    labelUpdates.Text = Labels.Get("no_new_updates");
                    labelUpdates.Foreground = ColorGreen;
                }
            });

            doc2?.Dispose();

            Helpers.Logger.WriteLine($"Updates: fetched {drivers.Count} entries from {(isBios ? "BIOS" : "Drivers")} API");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Updates fetch error: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                panel.Children.Clear();
                panel.Children.Add(new TextBlock
                {
                    Text = Labels.Format("fetch_failed", ex.Message),
                    Foreground = ColorRed,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            });
        }
    }

    private const string LenovoCatalogBaseUrl = "https://pcsupport.lenovo.com/us/en/api/v4/downloads/drivers?productId=";

    /// <summary>
    /// Lenovo pcsupport catalog. One call returns BIOS and drivers together; entries
    /// with category "BIOS/UEFI" go to the BIOS panel with a version compare
    /// against the DMI BIOS string (NZCN26WW vs NZCN37WW), the rest land in
    /// the drivers panel as download links.
    /// </summary>
    private async Task FetchLenovoUpdatesAsync()
    {
        string url = LenovoCatalogBaseUrl + WebUtility.UrlEncode(_model ?? "");
        try
        {
            Helpers.Logger.WriteLine($"Updates: fetching {url}");

            using var httpClient = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            });
            // The catalog endpoint rejects non-browser user agents.
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Referrer = new Uri("https://pcsupport.lenovo.com/");
            httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
            httpClient.Timeout = TimeSpan.FromSeconds(20);

            var json = await httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var biosEntries = new List<DriverInfo>();
            var driverEntries = new List<DriverInfo>();

            if (doc.RootElement.TryGetProperty("body", out var body)
                && body.TryGetProperty("DownloadItems", out var items))
            {
                for (int i = 0; i < items.GetArrayLength(); i++)
                {
                    var entry = ParseLenovoDownloadItem(items[i]);
                    if (entry == null)
                        continue;
                    if (entry.Value.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase))
                        biosEntries.Add(entry.Value);
                    else
                        driverEntries.Add(entry.Value);
                }
            }

            int localUpdates = 0;
            foreach (var entry in biosEntries)
            {
                int status = CompareLenovoBiosVersions(entry.Version, _biosVersion);
                string tooltip = status == 0
                    ? entry.Version
                    : Labels.Format("download_tooltip", entry.Version, _biosVersion ?? "");
                if (status == 1)
                    localUpdates++;
                var d = entry;
                int s = status;
                string t = tooltip;
                Dispatcher.UIThread.Post(() => AddDriverRow(panelBios, d, s, t));
            }
            foreach (var entry in driverEntries)
            {
                var d = entry;
                Dispatcher.UIThread.Post(() => AddDriverRow(panelDrivers, d, 0, d.Version));
            }

            if (localUpdates > 0)
            {
                _updatesCount += localUpdates;
                Dispatcher.UIThread.Post(() =>
                {
                    labelUpdates.Text = Labels.Format("updates_available_format", _updatesCount);
                    labelUpdates.Foreground = ColorRed;
                    labelUpdates.FontWeight = FontWeight.Bold;
                });
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (panelBios.Children.Contains(labelBiosLoading))
                    panelBios.Children.Remove(labelBiosLoading);
                if (panelDrivers.Children.Contains(labelDriversLoading))
                    panelDrivers.Children.Remove(labelDriversLoading);

                if (biosEntries.Count == 0)
                    panelBios.Children.Add(new TextBlock { Text = Labels.Get("no_entries"), Foreground = ColorDim, FontSize = 12 });
                if (driverEntries.Count == 0)
                    panelDrivers.Children.Add(new TextBlock { Text = Labels.Get("no_entries"), Foreground = ColorDim, FontSize = 12 });

                if (_updatesCount == 0)
                {
                    labelUpdates.Text = Labels.Get("no_new_updates");
                    labelUpdates.Foreground = ColorGreen;
                }
            });

            Helpers.Logger.WriteLine($"Updates: fetched {biosEntries.Count} BIOS + {driverEntries.Count} driver entries from Lenovo catalog");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Updates fetch error (Lenovo): {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var panel in new[] { panelBios, panelDrivers })
                {
                    panel.Children.Clear();
                    panel.Children.Add(new TextBlock
                    {
                        Text = Labels.Format("fetch_failed", ex.Message),
                        Foreground = ColorRed,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            });
        }
    }

    /// One catalog DownloadItem -> DriverInfo. Prefers the EXE file, then ZIP,
    /// then the first file for the download link. Null when no file exists.
    private static DriverInfo? ParseLenovoDownloadItem(JsonElement item)
    {
        try
        {
            string category = item.TryGetProperty("Category", out var cat)
                && cat.TryGetProperty("Name", out var catName)
                ? catName.GetString() ?? "" : "";
            string title = item.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
            string version = item.TryGetProperty("SummaryInfo", out var si)
                && si.TryGetProperty("Version", out var v)
                ? v.GetString() ?? "" : "";

            if (!item.TryGetProperty("Files", out var files) || files.GetArrayLength() == 0)
                return null;

            JsonElement? mainFile = null;
            foreach (var preferred in new[] { "exe", "zip" })
            {
                for (int i = 0; i < files.GetArrayLength() && mainFile == null; i++)
                {
                    if (files[i].TryGetProperty("TypeString", out var ts)
                        && string.Equals(ts.GetString(), preferred, StringComparison.OrdinalIgnoreCase))
                        mainFile = files[i];
                }
                if (mainFile != null)
                    break;
            }
            mainFile ??= files[0];

            string downloadUrl = mainFile.Value.TryGetProperty("URL", out var u) ? u.GetString() ?? "" : "";
            string date = "";
            if (mainFile.Value.TryGetProperty("Date", out var dateNode)
                && dateNode.TryGetProperty("Unix", out var unix)
                && long.TryParse(unix.ToString(), out long unixMs))
            {
                date = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy/MM/dd");
            }

            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(downloadUrl))
                return null;

            return new DriverInfo
            {
                Category = category,
                Title = title,
                Version = version,
                DownloadUrl = downloadUrl,
                Date = date,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compare Lenovo BIOS version strings like "NZCN37WW" vs "NZCN26WW":
    /// same alpha prefix, numeric build in the middle. Returns 1 when the
    /// remote is newer, -1 when up to date, 0 when not comparable.
    /// </summary>
    private static int CompareLenovoBiosVersions(string? remote, string? local)
    {
        var (remotePrefix, remoteNum) = SplitLenovoBiosVersion(remote);
        var (localPrefix, localNum) = SplitLenovoBiosVersion(local);
        if (remoteNum < 0 || localNum < 0)
            return 0;
        if (!string.Equals(remotePrefix, localPrefix, StringComparison.OrdinalIgnoreCase))
            return 0;
        return remoteNum > localNum ? 1 : -1;
    }

    private static (string prefix, int number) SplitLenovoBiosVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return ("", -1);
        string s = version.Trim();
        int start = 0;
        while (start < s.Length && !char.IsDigit(s[start]))
            start++;
        int end = start;
        while (end < s.Length && char.IsDigit(s[end]))
            end++;
        if (start == end)
            return ("", -1);
        return (s.Substring(0, start), int.Parse(s.Substring(start, end - start)));
    }

    private int _rowIndex;

    private void AddDriverRow(StackPanel panel, DriverInfo driver, int status, string tooltip)
    {
        // Row: [Category | Title | Date | Version (link)]
        var rowBg = (_rowIndex % 2 == 0) ? RowBg1 : RowBg2;
        _rowIndex++;

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("100,*,80,120"),
            Background = rowBg,
            Margin = new Avalonia.Thickness(0, 1),
        };

        // Category
        grid.Children.Add(new TextBlock
        {
            Text = driver.Category,
            Foreground = ColorDim,
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [Grid.ColumnProperty] = 0,
        });

        // Title
        grid.Children.Add(new TextBlock
        {
            Text = driver.Title,
            Foreground = ColorWhite,
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            [Grid.ColumnProperty] = 1,
        });

        // Date
        grid.Children.Add(new TextBlock
        {
            Text = driver.Date,
            Foreground = ColorDim,
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 2,
        });

        // Version (clickable link)
        var versionColor = status switch
        {
            1 => ColorRed,     // newer available
            -1 => ColorGreen,  // up to date
            _ => ColorGray     // can't check
        };

        var versionText = driver.Version.Replace("latest version at the ", "");
        var versionBtn = new Button
        {
            Content = versionText,
            Foreground = versionColor,
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(6, 4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            FontSize = 11,
            FontWeight = status == 1 ? FontWeight.Bold : FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 3,
        };

        if (!string.IsNullOrEmpty(driver.DownloadUrl))
        {
            string url = driver.DownloadUrl;
            versionBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Helpers.Logger.WriteLine($"Failed to open URL: {ex.Message}");
                }
            };
            ToolTip.SetTip(versionBtn, tooltip + "\n" + Labels.Get("click_to_download"));
        }
        else
        {
            ToolTip.SetTip(versionBtn, tooltip);
        }

        grid.Children.Add(versionBtn);
        panel.Children.Add(grid);
    }

}
