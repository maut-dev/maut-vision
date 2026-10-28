using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MautVision.Core.Projects.Transfer;

/// <summary>A regular file of a project folder: path relative to the scanned root with '/', size, NTFS file ID (0 when unknown).</summary>
internal readonly record struct ScannedFile(string RelativePath, string FullPath, long Length, ulong FileId, DateTime LastWriteTimeUtc);

/// <summary>
/// Lists the files of a folder tree with their sizes and file IDs in one pass: on Windows each directory is read in 64 KB batches
/// (GetFileInformationByHandleEx, FileIdBothDirectoryInfo), so hard links are recognised by their shared file ID without opening
/// any file. Names that start with a dot are temporary and skipped; a reparse point fails the scan, as everywhere in project storage.
/// </summary>
internal static class FolderScan
{
    private const int BufferSize = 64 * 1024;
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int ErrorNoMoreFiles = 18;
    private const uint FileListDirectory = 0x0001;
    private const uint ShareAll = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    /// <param name="prefix">Prepended to every relative path ("files/").</param>
    /// <param name="skipDirectory">Relative directory paths (with the prefix, without a trailing '/') that are left out with their content.</param>
    internal static List<ScannedFile> Scan(string root, string prefix, Func<string, bool> skipDirectory, CancellationToken cancellationToken)
    {
        var result = new List<ScannedFile>();
        if (!Directory.Exists(root)) return result;
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((root, prefix.TrimEnd('/')));
        var buffer = OperatingSystem.IsWindows() ? new byte[BufferSize] : [];
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, relative) = pending.Pop();
            foreach (var entry in OperatingSystem.IsWindows() ? ReadWindows(directory, buffer) : ReadPortable(directory))
            {
                if (entry.Name.StartsWith('.')) continue;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are not permitted in managed project storage.");
                var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                var full = Path.Combine(directory, entry.Name);
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!skipDirectory(path)) pending.Push((full, path));
                    continue;
                }
                result.Add(new ScannedFile(path, full, entry.Length, entry.FileId, entry.LastWriteTimeUtc));
            }
        }
        return result;
    }

    private readonly record struct Entry(string Name, FileAttributes Attributes, long Length, ulong FileId, DateTime LastWriteTimeUtc);

    private static IEnumerable<Entry> ReadPortable(string directory)
    {
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            yield return new Entry(info.Name, info.Attributes, info is FileInfo file ? file.Length : 0, 0, info.LastWriteTimeUtc);
        }
    }

    private static List<Entry> ReadWindows(string directory, byte[] buffer)
    {
        using var handle = CreateFile(@"\\?\" + Path.GetFullPath(directory), FileListDirectory, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"Folder {directory} cannot be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        var entries = new List<Entry>();
        var informationClass = FileIdBothDirectoryRestartInfo;
        while (true)
        {
            if (!GetFileInformationByHandleEx(handle, informationClass, buffer, (uint)buffer.Length))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreFiles) return entries;
                throw new IOException($"Folder {directory} cannot be listed.", new Win32Exception(error));
            }
            informationClass = FileIdBothDirectoryInfo;
            var offset = 0;
            while (true)
            {
                // FILE_ID_BOTH_DIR_INFO: NextEntryOffset 0, LastWriteTime 24, EndOfFile 40, FileAttributes 56, FileNameLength 60,
                // FileId 96, FileName 104 (UTF-16).
                var record = buffer.AsSpan(offset);
                var next = BinaryPrimitives.ReadInt32LittleEndian(record);
                var nameLength = BinaryPrimitives.ReadInt32LittleEndian(record[60..]);
                var name = MemoryMarshal.Cast<byte, char>(record.Slice(104, nameLength)).ToString();
                if (name is not "." and not "..")
                {
                    entries.Add(new Entry(name,
                        (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[56..]),
                        BinaryPrimitives.ReadInt64LittleEndian(record[40..]),
                        BinaryPrimitives.ReadUInt64LittleEndian(record[96..]),
                        DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(record[24..]))));
                }
                if (next == 0) break;
                offset += next;
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, byte[] buffer, uint bufferSize);
}
