using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Storage;

/// <summary>A material file queued for removal: files/&lt;kind&gt;/&lt;id&gt;&lt;extension&gt;.</summary>
internal sealed record QueuedFile(string Kind, Guid Id, string Extension)
{
    internal string Key => Kind + "/" + Id.ToString("D") + Extension;
}

/// <summary>
/// Second phase of permanent deletion for every material kind of one project. A deletion removes its rows and queues their
/// files (table file_cleanup) in one transaction; after the commit a background pass removes the files and their cached
/// thumbnails at low priority. A crash or a busy file never leaves a row without its file; queued files are retried on the
/// next deletion and when the project is opened, never at application startup.
/// </summary>
internal sealed class FileCleanup(string projectId, ProjectStore store, Action<string, Guid> evictThumbnails, ILogger logger)
{
    /// <summary>
    /// The extension of a queued folder: files/&lt;kind&gt;/&lt;id&gt;/ is removed with everything in it (a dataset version). A busy file
    /// keeps the folder queued; the next pass removes what is left.
    /// </summary>
    internal const string DirectoryMarker = ".directory";

    /// <summary>A small deletion usually finishes its files within this wait and reports no pending cleanup.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);

    /// <summary>Queue rows read and committed together; a pass interrupted by exit loses at most one chunk of progress.</summary>
    private const int Chunk = 2048;
    private const int Workers = 4;

    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<string, bool> busy = new(StringComparer.Ordinal);
    private Task task = Task.CompletedTask;
    private bool running;
    private bool requested;

    /// <summary>The current or last background pass; it completes when the queue was processed once.</summary>
    internal Task CleanupTask
    {
        get
        {
            lock (gate) return task;
        }
    }

    /// <summary>Smoke seam: while true no pass starts, as if the application exited right after the commit.</summary>
    internal bool Suspended { get; set; }

    /// <summary>Queues files inside the deletion transaction; they are removed only after it commits.</summary>
    internal static async Task QueueAsync(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<QueuedFile> files,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO file_cleanup (kind, id, extension, queued_at) VALUES ($kind, $id, $extension, $now);";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var extension = command.Parameters.Add("$extension", SqliteType.Text);
        foreach (var file in files)
        {
            kind.Value = file.Kind;
            id.Value = file.Id.ToString("D");
            extension.Value = file.Extension;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the pass after a committed deletion and waits for it at most <see cref="Grace"/>. True — some of these files
    /// are still being removed in the background (many files, or a file is busy).
    /// </summary>
    internal async Task<bool> RemoveAfterCommitAsync(IReadOnlyCollection<QueuedFile> files)
    {
        if (files.Count == 0) return false;
        var pass = Request();
        var finished = pass is not null && await Task.WhenAny(pass, Task.Delay(Grace, CancellationToken.None)).ConfigureAwait(false) == pass;
        return !finished || files.Any(file => busy.ContainsKey(file.Key));
    }

    /// <summary>Called when the project is opened: one indexed query, and only when files are queued a background pass runs.</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        var queued = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM file_cleanup);";
            return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) == 1;
        }, cancellationToken).ConfigureAwait(false);
        if (queued) _ = Request();
    }

    /// <summary>
    /// Starts the background pass unless it is running; a running one reads the queue once more before it ends, so a
    /// request is never lost. Returns the task that completes after the queue was processed, or null when suspended.
    /// </summary>
    internal Task? Request()
    {
        lock (gate)
        {
            if (Suspended) return null;
            requested = true;
            if (!running)
            {
                running = true;
                task = Task.Run(RunAsync);
            }
            return task;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (gate)
            {
                if (!requested)
                {
                    running = false;
                    return;
                }
                requested = false;
            }
            try
            {
                await PassAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Project {ProjectId} file cleanup failed; queued files stay for the next attempt.", projectId);
            }
        }
    }

    /// <summary>
    /// One pass over the queue by key order in chunks. Each chunk is removed by a few low-priority workers; the removed
    /// files leave the queue in one transaction, so progress survives an exit. Busy files stay queued.
    /// </summary>
    private async Task PassAsync()
    {
        var watch = Stopwatch.StartNew();
        var files = await store.GetFilesAsync(CancellationToken.None).ConfigureAwait(false);
        var checkedKinds = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        StoredRow? after = null;
        long removedTotal = 0, busyTotal = 0;
        while (true)
        {
            var chunk = await ReadQueueAsync(after).ConfigureAwait(false);
            if (chunk.Count == 0) break;
            after = chunk[^1];
            var removed = new ConcurrentBag<StoredRow>();
            await Task.Run(() => Parallel.ForEach(chunk, new ParallelOptions { MaxDegreeOfParallelism = Workers },
                () =>
                {
                    // Pool threads are lent: their priority is lowered for this partition and restored after it.
                    var thread = Thread.CurrentThread;
                    var previous = thread.Priority;
                    thread.Priority = ThreadPriority.BelowNormal;
                    return previous;
                },
                (row, loop, previous) =>
                {
                    if (Remove(files, checkedKinds, row))
                    {
                        removed.Add(row);
                        busy.TryRemove(row.Key, out _);
                        // Thumbnails are disposable; eviction never fails the cleanup.
                        if (Guid.TryParseExact(row.Id, "D", out var id)) evictThumbnails(row.Kind, id);
                    }
                    else
                    {
                        busy[row.Key] = true;
                    }
                    return previous;
                },
                previous => Thread.CurrentThread.Priority = previous)).ConfigureAwait(false);

            if (!removed.IsEmpty)
            {
                await store.WriteAsync(async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM file_cleanup WHERE kind = $kind AND id = $id AND extension = $extension;";
                    var kind = command.Parameters.Add("$kind", SqliteType.Text);
                    var id = command.Parameters.Add("$id", SqliteType.Text);
                    var extension = command.Parameters.Add("$extension", SqliteType.Text);
                    foreach (var row in removed)
                    {
                        kind.Value = row.Kind;
                        id.Value = row.Id;
                        extension.Value = row.Extension;
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }, CancellationToken.None).ConfigureAwait(false);
            }
            removedTotal += removed.Count;
            busyTotal += chunk.Count - removed.Count;
            if (chunk.Count < Chunk) break;
        }
        if (busyTotal > 0)
        {
            logger.LogWarning("Project {ProjectId} file cleanup: {Removed} removed in {ElapsedMilliseconds} ms, {Pending} still busy and queued.",
                projectId, removedTotal, watch.ElapsedMilliseconds, busyTotal);
        }
        else if (removedTotal > 0)
        {
            logger.LogInformation("Project {ProjectId} file cleanup: {Removed} removed in {ElapsedMilliseconds} ms.",
                projectId, removedTotal, watch.ElapsedMilliseconds);
        }
    }

    private Task<List<StoredRow>> ReadQueueAsync(StoredRow? after)
        => store.ReadAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = after is null
                ? "SELECT kind, id, extension FROM file_cleanup ORDER BY kind, id, extension LIMIT $limit;"
                : "SELECT kind, id, extension FROM file_cleanup WHERE (kind, id, extension) > ($kind, $id, $extension) ORDER BY kind, id, extension LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Chunk);
            if (after is not null)
            {
                command.Parameters.AddWithValue("$kind", after.Kind);
                command.Parameters.AddWithValue("$id", after.Id);
                command.Parameters.AddWithValue("$extension", after.Extension);
            }
            var result = new List<StoredRow>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                result.Add(new StoredRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            return result;
        }, CancellationToken.None);

    /// <summary>True when the file is gone (or was never there); false when it is busy and must stay queued.</summary>
    private static bool Remove(MaterialFiles files, ConcurrentDictionary<string, bool> checkedKinds, StoredRow row)
    {
        try
        {
            // The kind directory may appear after the store was opened; a link there must not redirect deletion.
            checkedKinds.GetOrAdd(row.Kind, kind =>
            {
                ProjectStore.ValidateEntry(files.GetDirectory(kind));
                return true;
            });
            if (row.Extension == DirectoryMarker)
            {
                var folder = Path.Combine(files.GetDirectory(row.Kind), Guid.ParseExact(row.Id, "D").ToString("D"));
                // A link in place of the folder is refused, never followed; links inside it are removed as entries.
                if (ProjectStore.ValidateEntry(folder) is { } attributes)
                {
                    if ((attributes & FileAttributes.Directory) == 0) File.Delete(folder);
                    else Directory.Delete(folder, recursive: true);
                }
                return true;
            }
            File.Delete(files.GetPath(row.Kind, Guid.ParseExact(row.Id, "D"), row.Extension));
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing is left to delete when the kind directory itself is gone.
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>A queue row as stored; the ID stays text so that a damaged row is skipped instead of failing the pass.</summary>
    private sealed record StoredRow(string Kind, string Id, string Extension)
    {
        internal string Key => Kind + "/" + Id + Extension;
    }
}
