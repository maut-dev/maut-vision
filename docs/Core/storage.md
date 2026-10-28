# Project storage

Project data lives in its folder and has three parts.

```text
projects/<projectId>/
  project.json      descriptor for the library: name, settings, revision
  project.db        SQLite: material metadata, relations, job state
  files/<kind>/     material files: files/images/<id>.png
  cache/            derived files (thumbnails); always safe to delete
```

- **project.json** is read by the library. Listing, getting and creating a project never open or create project.db.
- **project.db** holds everything that must be searched, paged and changed in a transaction. Large data (images, weights) never goes
  into the database.
- **files/** contains ordinary files. A path is built only from the material kind and its ID, never from a user string.
- **cache/** is rebuilt from files/ and is not project data; thumbnail rules are in the
  [resource channel](../Desktop/resources.md#core-files-and-thumbnails).

## Opening

`ProjectStore` is created with the project and does no I/O. The first operation that needs the database (or
`IProjectsManager.OpenAsync`) opens the storage:

1. The project root is checked for reparse points once, together with `project.db*`, `files/` and the kind folders.
2. `PRAGMA user_version` is read. A version newer than known — `projects.storage_newer`; the database is not changed and the project
   is not opened.
3. WAL is enabled; missing schema steps are applied in one transaction together with the new `user_version`. A failed step —
   `projects.storage_migration_failed`; the database stays at its previous version.

Application startup does not touch storage: schema upgrades and recovery happen only when a specific project is opened. A failed
opening is not cached; the next operation tries again.

## Reading and writing

```csharp
var count = await store.ReadAsync(async (connection, ct) => { /* SELECT */ }, ct);
await store.WriteAsync(async (connection, transaction, ct) => { /* INSERT/UPDATE */ }, ct);
```

Each operation takes a connection from the pool and returns it; there is no long-lived connection, so Core needs no shutdown. Writes
of one project are serialized through one gate and run in a transaction: an exception rolls everything back. Reads run in parallel
with each other and with the write (WAL). Connections use `foreign_keys=ON`, `busy_timeout` and `synchronous=NORMAL`.

`MaterialFiles` (`store.GetFilesAsync`) turns a kind and an ID into a path without re-checking ancestors: the root was checked when
the storage opened.

## Child managers

The project creates the storage and passes it to child managers through the constructor. There is no repository per table: a manager
writes the SQL of its own tables.

```csharp
internal sealed class ImagesManager(ProjectStore store, ILogger logger) : IImagesManager
{
    public Task<long> CountAsync(CancellationToken ct) => store.ReadAsync(async (connection, token) =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM images;";
        return (long)(await command.ExecuteScalarAsync(token))!;
    }, ct);
}
```

## A new schema step

The schema is an ordered list in `ProjectSchema`. A change adds a step with the next version at the end; applied steps never change.

```csharp
SchemaMigration.Sql(11, """
    CREATE TABLE example (
        id TEXT NOT NULL PRIMARY KEY,
        created_at INTEGER NOT NULL
    ) STRICT;
    """),
```

A step runs when a project is opened and must be fast. A conversion whose time depends on the number of materials (recomputing
files, filling every row) is a [job](jobs.md); the schema step only adds the structure.
