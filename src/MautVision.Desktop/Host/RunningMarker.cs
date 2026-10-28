namespace MautVision.Desktop.Host;

/// <summary>
/// Fixed mutexes that exist while Maut Vision runs, whatever its work directory: the uninstaller checks them (Inno Setup
/// CheckForMutexes) before removing the files. The session name sees this session, the global one other sessions as well. They are
/// held for the life of the process; the handles go away with it.
/// </summary>
internal static class RunningMarker
{
    internal const string Name = "MautVision-Running";

    internal const string GlobalName = @"Global\" + Name;

    private static readonly List<Mutex> Held = [];

    /// <summary>Creates both mutexes; a global name another user's process owns with a stricter access is skipped.</summary>
    internal static void Hold()
    {
        foreach (var name in new[] { Name, GlobalName })
        {
            try
            {
                Held.Add(new Mutex(initiallyOwned: false, name));
            }
            catch (Exception error) when (error is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                // Another session created it; the installer still sees it.
            }
        }
    }
}
