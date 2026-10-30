using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Mcp;

/// <summary>A file an AI client sent with upload_file: its ID, the stored file, its size and when it came (Environment.TickCount64).</summary>
internal sealed record McpUpload(Guid Id, string SessionKey, string Name, string Path, long Bytes, long Received);

/// <summary>
/// Files sent by content (upload_file) for clients without access to the disk. A file is at most 10 MB, a session at most 50 MB in total.
/// Files live in <c>mcp-uploads/&lt;session&gt;/&lt;uploadId&gt;/&lt;name&gt;</c> of the work directory; a session's files are removed
/// when the session ends, a file older than an hour by the sweep of the server (clients served per request have no session end), all of them
/// when the server starts or stops and after the application starts.
/// </summary>
internal sealed class McpUploads(string directory, ILogger logger)
{
    internal const long MaxFileBytes = 10L * 1024 * 1024;
    internal const long MaxSessionBytes = 50L * 1024 * 1024;
    internal const string FolderName = "mcp-uploads";

    private readonly ConcurrentDictionary<Guid, McpUpload> uploads = new();
    private readonly Lock gate = new();

    internal string Directory { get; } = directory;

    /// <summary>Stores decoded bytes; errors mcp.upload_too_large, mcp.upload_quota_exceeded, mcp.upload_name_invalid.</summary>
    internal async Task<McpUpload> SaveAsync(string sessionKey, string name, byte[] content, CancellationToken cancellationToken)
    {
        var fileName = SafeName(name);
        if (content.LongLength > MaxFileBytes)
            throw new McpToolException("mcp.upload_too_large", "The file is larger than 10 MB.", new Dictionary<string, string> { ["limitMb"] = "10" });
        McpUpload upload;
        lock (gate)
        {
            var used = uploads.Values.Where(item => item.SessionKey == sessionKey).Sum(item => item.Bytes);
            if (used + content.LongLength > MaxSessionBytes)
                throw new McpToolException("mcp.upload_quota_exceeded", "The session has sent more than 50 MB.", new Dictionary<string, string> { ["limitMb"] = "50" });
            var id = Guid.NewGuid();
            var folder = System.IO.Path.Combine(Directory, SessionFolder(sessionKey), id.ToString("N"));
            upload = new McpUpload(id, sessionKey, fileName, System.IO.Path.Combine(folder, fileName), content.LongLength, Environment.TickCount64);
            uploads[id] = upload;
        }
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(upload.Path)!);
            await File.WriteAllBytesAsync(upload.Path, content, cancellationToken);
        }
        catch
        {
            uploads.TryRemove(upload.Id, out _);
            throw;
        }
        return upload;
    }

    /// <summary>An upload of this server run; an unknown one is mcp.upload_not_found.</summary>
    internal McpUpload Get(Guid id) => uploads.TryGetValue(id, out var upload) && File.Exists(upload.Path)
        ? upload
        : throw new McpToolException("mcp.upload_not_found", "The upload is unknown or was removed: its session ended or it is older than an hour.");

    internal long SessionBytes(string sessionKey) => uploads.Values.Where(item => item.SessionKey == sessionKey).Sum(item => item.Bytes);

    /// <summary>The session ended: its files go.</summary>
    internal void EndSession(string sessionKey)
    {
        foreach (var upload in uploads.Values.Where(item => item.SessionKey == sessionKey).ToArray()) uploads.TryRemove(upload.Id, out _);
        Delete(System.IO.Path.Combine(Directory, SessionFolder(sessionKey)));
    }

    /// <summary>Removes the uploads older than <paramref name="age"/>; returns how many went.</summary>
    internal int RemoveOlderThan(TimeSpan age)
    {
        var limit = Environment.TickCount64 - (long)age.TotalMilliseconds;
        var removed = 0;
        foreach (var upload in uploads.Values.Where(item => item.Received <= limit).ToArray())
        {
            if (!uploads.TryRemove(upload.Id, out _)) continue;
            Delete(System.IO.Path.GetDirectoryName(upload.Path)!);
            removed++;
        }
        return removed;
    }

    /// <summary>Removes every upload (server start and stop, after the application starts).</summary>
    internal void Clear()
    {
        uploads.Clear();
        Delete(Directory);
    }

    private void Delete(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "MCP uploads were not removed; they are removed with the next start of the server.");
        }
    }

    /// <summary>The session ID comes from the transport; only its safe characters name the folder.</summary>
    private static string SessionFolder(string sessionKey)
    {
        var safe = new string(sessionKey.Where(char.IsAsciiLetterOrDigit).Take(64).ToArray());
        return safe.Length == 0 ? "session" : safe;
    }

    private static string SafeName(string name)
    {
        var fileName = System.IO.Path.GetFileName((name ?? "").Replace('/', '\\'));
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        fileName = new string(fileName.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        if (fileName.Length == 0) throw new McpToolException("mcp.upload_name_invalid", "The file name is empty.");
        return fileName.Length > 120 ? fileName[..120] : fileName;
    }
}
