using System.Diagnostics;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>Per-project SQLite storage: when it is created, migrated, refused, and how writes are serialized.</summary>
internal static class ProjectStoreSmoke
{
    internal const double OpenBoundMilliseconds = 500;

    internal static async Task<int> RunAsync(
        string fixture,
        Func<string, ILoggerFactory?, Task<ICore>> createCore,
        Action<string> seedInstalledModel,
        Func<string, string, Task> linkDirectory,
        List<string> junctions)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        async Task<T> Rejects<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T error) { checks++; return error; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
        async Task RejectsCode(string code, Func<Task> action)
        {
            var error = await Rejects<CoreException>(action);
            if (error.Code != code) throw new InvalidOperationException($"Expected {code}, got {error.Code}.");
        }

        var root = Path.Combine(fixture, "storage-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var log = new LogCapture();
        var manager = (await createCore(root, log)).Projects;
        var settings = new ProjectSettings("Хранилище", "", "yolo26n", new InputSize(640, 640));
        var first = await manager.CreateAsync(settings);
        var second = await manager.CreateAsync(settings with { Name = "Второй" });
        string ProjectDirectory(IProject project) => Path.Combine(root, "projects", project.Id);
        string Database(IProject project) => Path.Combine(ProjectDirectory(project), ProjectStore.DatabaseFileName);

        await manager.ListAsync();
        await manager.GetAsync(first.Id);
        Check(!File.Exists(Database(first)) && !File.Exists(Database(second))
            && !Directory.Exists(Path.Combine(ProjectDirectory(first), MaterialFiles.DirectoryName)), "Create, list and get never create project.db or files.");
        Check(manager.GetType().Assembly.GetType("MautVision.Core.Projects.Storage.ProjectStore")?.IsVisible == false, "Storage stays internal.");

        // Cold open: no pooled connection exists for this database yet.
        SqliteConnection.ClearAllPools();
        var watch = Stopwatch.StartNew();
        await manager.OpenAsync(first.Id);
        var createMilliseconds = watch.Elapsed.TotalMilliseconds;
        Check(File.Exists(Database(first)) && Directory.Exists(Path.Combine(ProjectDirectory(first), MaterialFiles.DirectoryName)), "Open creates project.db and the files root.");
        Check(await ScalarAsync(Database(first), "PRAGMA user_version;") == ProjectSchema.CurrentVersion, "A new database is at the current version.");
        Check(await TextAsync(Database(first), "PRAGMA journal_mode;") == "wal", "project.db uses WAL.");
        Check(await ScalarAsync(Database(first), "SELECT count(*) FROM sqlite_master WHERE name = 'project_meta';") == 1, "Version 1 has project_meta.");
        Check(!File.Exists(Database(second)), "Opening one project leaves the others untouched.");
        Check(MigrationRecords(log, first.Id, LogLevel.Information).Length == 2
            && MigrationRecords(log, first.Id, LogLevel.Information).All(message => message.Contains($"from version 0 to {ProjectSchema.CurrentVersion}")), "Migration start and finish are logged with project id and versions.");
        var store = ((Project)first).Store;
        await manager.OpenAsync(first.Id);
        Check(MigrationRecords(log, first.Id, LogLevel.Information).Length == 2, "Opening again does not migrate again.");

        SqliteConnection.ClearAllPools();
        var reopenLog = new LogCapture();
        var reopenManager = (await createCore(root, reopenLog)).Projects;
        await reopenManager.GetAsync(first.Id);
        watch.Restart();
        await reopenManager.OpenAsync(first.Id);
        var reopenMilliseconds = watch.Elapsed.TotalMilliseconds;
        Check(MigrationRecords(reopenLog, first.Id, null).Length == 0, "Reopening an up-to-date database after restart does not migrate.");
        Check(createMilliseconds < OpenBoundMilliseconds && reopenMilliseconds < OpenBoundMilliseconds,
            $"Project open is fast: create {createMilliseconds:F1} ms, reopen {reopenMilliseconds:F1} ms.");
        Console.WriteLine($"Project storage open: create {createMilliseconds:F1} ms, cold reopen {reopenMilliseconds:F1} ms.");

        // Writers are serialized by the store; readers run alongside without "database is locked".
        var active = 0;
        var maxActive = 0;
        var writes = Enumerable.Range(0, 32).Select(_ => store.WriteAsync(async (connection, transaction, token) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            var value = await ReadCounterAsync(connection, transaction, token);
            await Task.Delay(2, token);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO project_meta (key, value) VALUES ('counter', $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$value", (value + 1).ToString());
            await command.ExecuteNonQueryAsync(token);
            Interlocked.Decrement(ref active);
        }));
        var reads = Enumerable.Range(0, 32).Select(_ => store.ReadAsync((connection, token) => ReadCounterAsync(connection, null, token)));
        await Task.WhenAll(writes.Concat(reads.Select(read => (Task)read)));
        Check(maxActive == 1, "Writes of a project never overlap.");
        Check(await store.ReadAsync((connection, token) => ReadCounterAsync(connection, null, token)) == 32, "Concurrent read-modify-write keeps every increment.");
        await Rejects<InvalidOperationException>(() => store.WriteAsync<int>((connection, transaction, token) => throw new InvalidOperationException("Rolled back.")));
        Check(await store.ReadAsync((connection, token) => ReadCounterAsync(connection, null, token)) == 32, "A failed write is rolled back.");

        var files = await store.GetFilesAsync();
        var materialId = Guid.NewGuid();
        Check(files.GetPath("images", materialId, ".png") == Path.Combine(ProjectDirectory(first), "files", "images", materialId.ToString("D") + ".png"), "Material paths are built from kind and ID.");
        Check(files.CreateDirectory("images") == Path.Combine(ProjectDirectory(first), "files", "images") && Directory.Exists(Path.Combine(ProjectDirectory(first), "files", "images")), "A kind directory is created on demand.");
        foreach (var kind in new[] { "", "..", "Images", "a/b", "a\\b", "images\n", "1st" })
            await Rejects<ArgumentException>(() => Task.FromResult(files.GetPath(kind, materialId, ".png")));
        foreach (var extension in new[] { "", "png", ".PNG", "./x", ".png\n", ".toolongextension" })
            await Rejects<ArgumentException>(() => Task.FromResult(files.GetPath("images", materialId, extension)));
        await Rejects<ArgumentException>(() => Task.FromResult(files.GetPath("images", Guid.Empty, ".png")));

        // An older database (version 0 with foreign content) is migrated and keeps its data.
        var older = await manager.CreateAsync(settings with { Name = "Старый" });
        await ExecuteAsync(Database(older), "CREATE TABLE scratch (value TEXT); INSERT INTO scratch VALUES ('kept');");
        await manager.OpenAsync(older.Id);
        Check(await ScalarAsync(Database(older), "PRAGMA user_version;") == ProjectSchema.CurrentVersion
            && await ScalarAsync(Database(older), "SELECT count(*) FROM scratch;") == 1, "A version 0 database is migrated and keeps its rows.");

        // A newer database is refused and left byte-for-byte unchanged.
        var newer = await manager.CreateAsync(settings with { Name = "Новее" });
        await manager.OpenAsync(newer.Id);
        SqliteConnection.ClearAllPools();
        await ExecuteAsync(Database(newer), $"PRAGMA user_version = {ProjectSchema.CurrentVersion + 1};");
        var before = await File.ReadAllBytesAsync(Database(newer));
        var newerLog = new LogCapture();
        var newerManager = (await createCore(root, newerLog)).Projects;
        var opened = 0;
        newerManager.ProjectOpened += (_, _) => opened++;
        await RejectsCode("projects.storage_newer", () => newerManager.OpenAsync(newer.Id));
        await RejectsCode("projects.storage_newer", () => newerManager.OpenAsync(newer.Id));
        SqliteConnection.ClearAllPools();
        var after = await File.ReadAllBytesAsync(Database(newer));
        Check(opened == 0 && before.SequenceEqual(after), "A newer database is not opened and stays unchanged.");
        Check(newerLog.Records.Any(record => record.Level == LogLevel.Warning && record.Message.Contains(newer.Id)), "A refused newer database is logged.");

        // A failing step rolls the whole migration back; a later good list migrates current → current + 1.
        var current = ProjectSchema.CurrentVersion;
        var migrating = await manager.CreateAsync(settings with { Name = "Миграция" });
        await manager.OpenAsync(migrating.Id);
        var failingLog = new LogCapture();
        var failing = new ProjectStore(migrating.Id, ProjectDirectory(migrating), failingLog.CreateLogger("Store"),
        [
            .. ProjectSchema.Migrations,
            SchemaMigration.Sql(current + 1, "CREATE TABLE probe (value TEXT); INSERT INTO probe VALUES ('x');"),
            new SchemaMigration(current + 2, (_, _, _) => throw new InvalidOperationException("Injected migration failure.")),
        ]);
        await RejectsCode("projects.storage_migration_failed", () => failing.EnsureOpenAsync());
        Check(await ScalarAsync(Database(migrating), "PRAGMA user_version;") == current
            && await ScalarAsync(Database(migrating), "SELECT count(*) FROM sqlite_master WHERE name = 'probe';") == 0, "A failed migration leaves the previous version and no partial tables.");
        Check(failingLog.Records.Any(record => record.Level == LogLevel.Error && record.Exception is InvalidOperationException
            && record.Message.Contains(migrating.Id) && record.Message.Contains($"version {current + 2}")), "A failed migration is logged with project id and step.");
        await Rejects<CoreException>(() => failing.ReadAsync((_, _) => Task.FromResult(0)));
        var upgraded = new ProjectStore(migrating.Id, ProjectDirectory(migrating), failingLog.CreateLogger("Store"),
            [.. ProjectSchema.Migrations, SchemaMigration.Sql(current + 1, "CREATE TABLE probe (value TEXT);")]);
        await upgraded.EnsureOpenAsync();
        Check(await ScalarAsync(Database(migrating), "PRAGMA user_version;") == current + 1
            && await ScalarAsync(Database(migrating), "SELECT count(*) FROM sqlite_master WHERE name = 'probe';") == 1, "An up-to-date database is migrated by an appended step.");
        await Rejects<ArgumentException>(() => Task.FromResult(new ProjectStore(migrating.Id, ProjectDirectory(migrating), failingLog.CreateLogger("Store"),
            [SchemaMigration.Sql(2, "SELECT 1;")])));

        var damaged = await manager.CreateAsync(settings with { Name = "Повреждён" });
        await File.WriteAllTextAsync(Database(damaged), new string('x', 4096));
        await RejectsCode("projects.storage_damaged", () => manager.OpenAsync(damaged.Id));
        SqliteConnection.ClearAllPools();

        // Reparse points: a linked project root, files root or kind directory is refused before any write.
        var outside = Path.Combine(fixture, "storage-outside");
        Directory.CreateDirectory(outside);
        var linkedRoot = Path.Combine(fixture, "storage-linked-project");
        await linkDirectory(linkedRoot, outside);
        junctions.Add(linkedRoot);
        await Rejects<IOException>(() => new ProjectStore(Guid.NewGuid().ToString("D"), linkedRoot, NullLogger()).EnsureOpenAsync());
        var linkedFiles = await manager.CreateAsync(settings with { Name = "Ссылка" });
        await linkDirectory(Path.Combine(ProjectDirectory(linkedFiles), MaterialFiles.DirectoryName), outside);
        junctions.Add(Path.Combine(ProjectDirectory(linkedFiles), MaterialFiles.DirectoryName));
        await Rejects<IOException>(() => manager.OpenAsync(linkedFiles.Id));
        var linkedKind = await manager.CreateAsync(settings with { Name = "Ссылка вида" });
        Directory.CreateDirectory(Path.Combine(ProjectDirectory(linkedKind), MaterialFiles.DirectoryName));
        await linkDirectory(Path.Combine(ProjectDirectory(linkedKind), MaterialFiles.DirectoryName, "images"), outside);
        junctions.Add(Path.Combine(ProjectDirectory(linkedKind), MaterialFiles.DirectoryName, "images"));
        await Rejects<IOException>(() => manager.OpenAsync(linkedKind.Id));
        Check(!Directory.EnumerateFileSystemEntries(outside).Any() && !File.Exists(Database(linkedFiles)) && !File.Exists(Database(linkedKind)),
            "A refused reparse point creates no database and writes nothing outside.");
        return checks;
    }

    private static ILogger NullLogger() => new LogCapture().CreateLogger("Store");

    private static string[] MigrationRecords(LogCapture log, string projectId, LogLevel? level) => log.Records
        .Where(record => (level is null || record.Level == level) && record.Message.Contains(projectId) && record.Message.Contains("migration"))
        .Select(record => record.Message)
        .ToArray();

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static async Task<long> ReadCounterAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM project_meta WHERE key = 'counter';";
        return await command.ExecuteScalarAsync(token) is string value ? long.Parse(value) : 0;
    }

    // Fixture connections bypass the pool so that they never hold the file after the check.
    private static SqliteConnection Raw(string database) => new($"Data Source={database};Pooling=False");

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = Raw(database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string database, string sql) => Convert.ToInt64(await QueryAsync(database, sql));

    private static async Task<string?> TextAsync(string database, string sql) => Convert.ToString(await QueryAsync(database, sql));

    private static async Task<object?> QueryAsync(string database, string sql)
    {
        await using var connection = Raw(database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
