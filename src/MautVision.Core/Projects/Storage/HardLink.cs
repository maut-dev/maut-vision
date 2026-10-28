using System.Runtime.InteropServices;

namespace MautVision.Core.Projects.Storage;

/// <summary>
/// CreateHardLinkW with both paths in the \\?\ form. Without it the call refuses paths longer than MAX_PATH (ERROR_PATH_NOT_FOUND)
/// unless the process is long-path aware, and callers silently fall back to a copy: a work directory a few folders deeper turned
/// every dataset frame into a second copy of the image.
/// </summary>
internal static class HardLink
{
    /// <summary>True — target is a new hard link of source; false — not possible (another volume, not Windows, an existing target).</summary>
    internal static bool TryCreate(string target, string source)
        => OperatingSystem.IsWindows() && CreateHardLink(Extended(target), Extended(source), IntPtr.Zero);

    private static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
