using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MautVision.Desktop.Mcp;

/// <summary>A path a tool may read: the final path (links and junctions resolved) and whether it is a folder.</summary>
internal sealed record McpResolvedPath(string Path, bool IsDirectory);

/// <summary>
/// Paths given by an AI client. A path must be absolute; it is normalized and resolved to its final target through the file system
/// (symbolic links, junctions, mapped folders), as a file dialog would open it. With the optional restriction only paths inside the
/// allowed folders pass, compared after resolution, so <c>..</c> and links cannot leave them. The folder of MCP uploads is always allowed.
/// </summary>
internal sealed class McpPaths(Func<McpSettings> settings, string uploadsDirectory)
{
    /// <summary>Resolves an existing file or folder. Errors: mcp.path_invalid, mcp.path_not_found, mcp.path_not_allowed, mcp.path_access_denied.</summary>
    internal McpResolvedPath Resolve(string? path, string argument = "path")
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(path))
            throw new McpToolException("mcp.path_invalid", "The path is not an absolute Windows path.", new Dictionary<string, string> { ["argument"] = argument });
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpToolException("mcp.path_invalid", "The path cannot be normalized.", new Dictionary<string, string> { ["argument"] = argument });
        }
        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
            throw new McpToolException("mcp.path_not_found", "The file or folder does not exist.", new Dictionary<string, string> { ["path"] = full });
        var final = FinalPath(full, isDirectory) ?? full;
        var current = settings();
        if (current.RestrictFolders && !IsInside(final, Existing(uploadsDirectory)) && !current.AllowedFolders.Any(folder => IsInside(final, Existing(folder))))
            throw new McpToolException("mcp.path_not_allowed", "The path is outside the allowed folders.", new Dictionary<string, string> { ["path"] = full });
        return new McpResolvedPath(final, isDirectory);
    }

    /// <summary>An existing file (not a folder).</summary>
    internal string ResolveFile(string? path, string argument = "path")
    {
        var resolved = Resolve(path, argument);
        if (resolved.IsDirectory)
            throw new McpToolException("mcp.path_not_file", "The path is a folder, a file is expected.", new Dictionary<string, string> { ["path"] = resolved.Path });
        return resolved.Path;
    }

    /// <summary>An existing folder.</summary>
    internal string ResolveDirectory(string? path, string argument = "path")
    {
        var resolved = Resolve(path, argument);
        if (!resolved.IsDirectory)
            throw new McpToolException("mcp.path_not_folder", "The path is a file, a folder is expected.", new Dictionary<string, string> { ["path"] = resolved.Path });
        return resolved.Path;
    }

    /// <summary>An allowed folder as the file system resolves it; a missing or inaccessible one stays as written (and matches nothing real).</summary>
    private static string Existing(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? FinalPath(folder, true) ?? folder : folder;
        }
        catch (McpToolException)
        {
            return folder;
        }
    }

    internal static bool IsInside(string path, string folder)
    {
        var root = folder.TrimEnd('\\', '/');
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The final path of an existing file or folder by its handle (GetFinalPathNameByHandle): every link and junction on the way is resolved.
    /// Null when the object cannot be opened even for attributes.
    /// </summary>
    internal static string? FinalPath(string path, bool directory)
    {
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            directory ? BackupSemantics : 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 5) throw new McpToolException("mcp.path_access_denied", "Windows denied access to the path.", new Dictionary<string, string> { ["path"] = path });
            return null;
        }
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
        if (length > buffer.Capacity)
        {
            buffer = new StringBuilder((int)length);
            length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
        }
        if (length == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var result = buffer.ToString();
        // \\?\C:\folder → C:\folder; \\?\UNC\server\share → \\server\share.
        if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + result[8..];
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int length, uint flags);
}
