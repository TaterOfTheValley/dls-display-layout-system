namespace DLS;

/// <summary>The same icon used by the executable, installer, tray and app windows.</summary>
internal static class AppIcon
{
    private static Icon? _shared;

    // The icon stays alive for the lifetime of the UI: NotifyIcon and the forms
    // borrow its native handle, so disposing it while they are open is unsafe.
    public static Icon Shared => _shared ??= Load();

    private static Icon Load()
    {
        using var resource = typeof(AppIcon).Assembly.GetManifestResourceStream("DLS.AppIcon.ico")
            ?? throw new InvalidOperationException("The DLS icon is missing from the assembly.");
        return new Icon(resource);
    }
}
