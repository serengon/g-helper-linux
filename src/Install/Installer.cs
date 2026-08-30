using Avalonia.Controls;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Install;

/// <summary>
/// Phase 1 compatibility surface for the removed upstream self-installer.
/// No payload is embedded and every mutation request returns an explicit
/// unavailable result. The future hardened package path is still under review.
/// </summary>
public static class Installer
{
    public enum FileState
    {
        Ok,
        Missing,
        Outdated,
        WrongPerms,
        Unknown,
        Unavailable,
        NotApplicable,
        Disabled,
    }

    public sealed class ManagedFile
    {
        public string Id = "";
        public string NameKey = "";
        public string Dest = "";
    }

    public sealed class FileResult
    {
        public ManagedFile File = null!;
        public FileState State;
    }

    internal static byte[]? GetEmbedded(string? resource) => null;

    public static List<FileResult> ComputeStatus() => [];

    public static void PopulateIntegrityPanel(
        Panel panel,
        Func<ManagedFile, Task>? onRepair = null,
        Func<ManagedFile, Task>? onRemove = null,
        Func<ManagedFile, Task>? onDiff = null)
        => PopulateIntegrityPanel(panel, [], onRepair, onRemove, onDiff);

    public static void PopulateIntegrityPanel(
        Panel panel,
        List<FileResult> results,
        Func<ManagedFile, Task>? onRepair = null,
        Func<ManagedFile, Task>? onRemove = null,
        Func<ManagedFile, Task>? onDiff = null)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = I18n.Labels.Get("udev_not_installed"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
    }

    public static Task<string> RunFixFromUiAsync() => UnavailableAsync();
    public static Task<string> RepairOneFromUiAsync(ManagedFile file) => UnavailableAsync();
    public static Task<string> RunRemoveFromUiAsync() => UnavailableAsync();
    public static Task<string> RemoveOneFromUiAsync(ManagedFile file) => UnavailableAsync();
    public static Task ShowDiffAsync(Window? owner, ManagedFile file) => Task.CompletedTask;

    public static string StateLabel(FileState state)
        => state == FileState.Ok
            ? I18n.Labels.Get("sysfiles_status_ok")
            : I18n.Labels.Get("udev_not_installed");

    private static Task<string> UnavailableAsync()
    {
        Logger.WriteLine("Installer: mutation refused; hardened installer unavailable");
        return Task.FromResult(I18n.Labels.Get("udev_not_installed"));
    }
}
