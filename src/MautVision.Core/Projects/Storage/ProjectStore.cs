using System.Diagnostics;
using System.Globalization;
using MautVision.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Storage;

/// <summary>
/// project.db and material files of one project. Construction does no I/O; the first operation opens the
/// store: validates the project root once, creates or migrates the database. Connections come from the pool
/// per operation, so nothing has to be closed when the application exits.
/// </summary>
internal sealed class ProjectStore
{
    internal const string DatabaseFileName = "project.db";

    // Per-connection settings; journal_mode=WAL is stored in the database file and is set once at open.
    private const string ConnectionPragmas = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string projectDirectory;
    private readonly ILogger logger;
    private readonly IReadOnlyList<SchemaMigration> migrations;
    private readonly string createConnectionString;
    private readonly string connectionString;
    private readonly SemaphoreSlim openGate = new(1, 1);
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private volatile MaterialFiles? files;
    private int ended;
    private int closed;
    private int reads;

    internal ProjectStore(string projectId, string projectDirectory, ILogger logger, IReadOnlyList<SchemaMigration>? migrations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        this.migrations = migrations ?? ProjectSchema.Migrations;
        for (var index = 0; index < this.migrations.Count; index++)
        {
            if (this.migrations[index].Version != index + 1)
            {
                throw new ArgumentException("Schema steps must have versions 1, 2, 3… in order.", nameof(migrations));
            }
        }
        ProjectId = projectId;
        this.projectDirectory = Path.GetFullPath(projectDirectory);
        this.logger = logger;
        DatabasePath = Path.Combine(this.projectDirectory, DatabaseFileName);
        var builder = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true };
        createConnectionString = builder.ToString();
        // After open a missing database is an error, not a reason to start an empty one.
        builder.Mode = SqliteOpenMode.ReadWrite;
        connectionString = builder.ToString();
    }

    internal string ProjectId { get; }
    internal string ProjectDirectory => projectDirectory;
    internal string DatabasePath { get; }
    internal int SchemaVersion => migrations.Count;

    internal async Task EnsureOpenAsync(CancellationToken cancellationToken = default)
        => await GetFilesAsync(cancellationToken).ConfigureAwait(false);

    internal async Task<MaterialFiles> GetFilesAsync(CancellationToken cancellationToken = default)
    {
        RequireNotClosed();
        if (files is { } ready)
        {
            return ready;
        }
        await openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireNotClosed();
            // A failed open is not cached: the next operation tries again.
            return files ??= await Task.Run(() => OpenAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            openGate.Release();
        }
    }

    /// <summary>Runs a read on its own pooled connection; reads run concurrently with each other and with the writer.</summary>
    internal async Task<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref reads);
        if (Volatile.Read(ref closed) == 1)
        {
            Interlocked.Decrement(ref reads);
            RequireNotClosed();
        }
        if (Volatile.Read(ref ended) == 1)
        {
            Interlocked.Decrement(ref reads);
            await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
        }
        try
        {
            return await Task.Run(async () =>
            {
                await using var connection = await OpenConnectionAsync(connectionString, cancellationToken).ConfigureAwait(false);
                return await read(connection, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref reads);
        }
    }

    /// <summary>Runs a write in one transaction; writes of a project are serialized and committed only if write returns.</summary>
    internal async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref closed) == 1)
        {
            writerGate.Release();
            RequireNotClosed();
        }
        if (Volatile.Read(ref ended) == 1)
        {
            // The gate goes to EndForTestingAsync; this write never happens, as in a process that is gone.
            writerGate.Release();
            await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
        }
        try
        {
            return await Task.Run(async () =>
            {
                await using var connection = await OpenConnectionAsync(connectionString, cancellationToken).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: false);
                var result = await write(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writerGate.Release();
        }
    }

    /// <summary>
    /// Smoke seam for the end of the application: waits for the read and write in progress, after which this instance never
    /// touches project.db again — later operations wait forever, as the work of a process that is gone. Another composition
    /// opening the same project then sees only what the ended one had committed.
    /// </summary>
    internal async Task EndForTestingAsync()
    {
        Interlocked.Exchange(ref ended, 1);
        await writerGate.WaitAsync().ConfigureAwait(false);
        while (Volatile.Read(ref reads) > 0) await Task.Delay(10).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the store of a project that is being deleted: waits for the write and the reads in progress, releases the pooled
    /// connections (they keep project.db open) and refuses every later operation with <c>projects.not_found</c>.
    /// </summary>
    internal async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 1) return;
        // Writers that got the gate after this see the store closed; the write in progress ends first.
        await writerGate.WaitAsync().ConfigureAwait(false);
        writerGate.Release();
        while (Volatile.Read(ref reads) > 0) await Task.Delay(10).ConfigureAwait(false);
        await openGate.WaitAsync().ConfigureAwait(false);
        openGate.Release();
        SqliteConnection.ClearPool(new SqliteConnection(createConnectionString));
        SqliteConnection.ClearPool(new SqliteConnection(connectionString));
    }

    private void RequireNotClosed()
    {
        if (Volatile.Read(ref closed) == 1) throw new CoreException("projects.not_found", $"Project {ProjectId} was deleted.");
    }

    internal Task WriteAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return WriteAsync<bool>(async (connection, transaction, token) =>
        {
            await write(connection, transaction, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private async Task<MaterialFiles> OpenAsync(CancellationToken cancellationToken)
    {
        // The only full ancestor walk; per-file operations rely on it.
        ProjectsManager.RequireDirectory(ProjectsManager.ValidatePath(projectDirectory)
            ?? throw new CoreException("projects.not_found", $"Project {ProjectId} was not found."));
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        {
            if (ValidateEntry(path) is { } attributes && (attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException("Project database must be a file.");
            }
        }
        var filesRoot = Path.Combine(projectDirectory, MaterialFiles.DirectoryName);
        if (ValidateEntry(filesRoot) is { } rootAttributes)
        {
            ProjectsManager.RequireDirectory(rootAttributes);
            foreach (var entry in new DirectoryInfo(filesRoot).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Reparse points are not permitted in managed project storage.");
                }
            }
        }

        try
        {
            await PrepareDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A refused database must not stay locked by idle pooled connections.
            SqliteConnection.ClearPool(new SqliteConnection(createConnectionString));
            if (error is SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADatabase })
            {
                logger.LogError(error, "Project {ProjectId} database is damaged.", ProjectId);
                throw new CoreException("projects.storage_damaged", $"Project {ProjectId} database is damaged.", error);
            }
            throw;
        }

        Directory.CreateDirectory(filesRoot);
        ProjectsManager.RequireDirectory(ValidateEntry(filesRoot)!.Value);
        return new MaterialFiles(filesRoot);
    }

    private async Task PrepareDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(createConnectionString, cancellationToken).ConfigureAwait(false);
        // The version is read before anything is written, so a newer database stays byte-for-byte unchanged.
        var version = await ReadVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
        RequireKnownVersion(version);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        if (version == SchemaVersion)
        {
            return;
        }

        logger.LogInformation("Project {ProjectId} storage migration from version {FromVersion} to {ToVersion} started.",
            ProjectId, version, SchemaVersion);
        var watch = Stopwatch.StartNew();
        var step = version;
        try
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            version = await ReadVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            RequireKnownVersion(version);
            foreach (var migration in migrations.Skip(version))
            {
                step = migration.Version;
                await migration.ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }
            // user_version is part of the database header, so it commits or rolls back with the steps.
            await ExecuteAsync(connection, transaction,
                "PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture) + ";", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not CoreException)
        {
            logger.LogError(error, "Project {ProjectId} storage migration to version {StepVersion} failed; storage stays at version {FromVersion}.",
                ProjectId, step, version);
            throw new CoreException("projects.storage_migration_failed",
                $"Project {ProjectId} storage migration to version {step} failed; storage stays at version {version}.", error);
        }
        logger.LogInformation("Project {ProjectId} storage migration from version {FromVersion} to {ToVersion} finished in {ElapsedMilliseconds} ms.",
            ProjectId, version, SchemaVersion, watch.ElapsedMilliseconds);
    }

    private void RequireKnownVersion(int version)
    {
        if (version < 0)
        {
            throw new CoreException("projects.storage_damaged", $"Project {ProjectId} database has invalid version {version}.");
        }
        if (version > SchemaVersion)
        {
            logger.LogWarning("Project {ProjectId} storage version {Version} is newer than supported version {SupportedVersion}.",
                ProjectId, version, SchemaVersion);
            throw new CoreException("projects.storage_newer",
                $"Project {ProjectId} storage version {version} is newer than supported version {SchemaVersion}.");
        }
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, ConnectionPragmas, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks one entry inside the already validated project directory, without walking its ancestors again.</summary>
    internal static FileAttributes? ValidateEntry(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Reparse points are not permitted in managed project storage.");
        }
        return attributes;
    }
}
