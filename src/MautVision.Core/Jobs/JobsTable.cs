using System.Globalization;
using System.Text.Json;
using MautVision.Core.Interfaces.Jobs;
using Microsoft.Data.Sqlite;

namespace MautVision.Core.Jobs;

/// <summary>Job row with the data a run needs; only unfinished jobs are read with input and checkpoint.</summary>
internal sealed record StoredJob(JobInfo Info, string Input, string? Checkpoint);

/// <summary>
/// SQL of the jobs and job_steps tables of project.db (schema step 2; the result column comes with step 3, the label column and the
/// jobs_active index with step 10).
/// </summary>
internal static class JobsTable
{
    private const string Columns = "id, kind, operation_id, status, progress, progress_text, processed, total, error_code, error_message, "
        + "cancel_requested, revision, created_at, updated_at, started_at, finished_at, result, label";

    // The SQL repeats this condition literally: SQLite uses the partial index jobs_active only for the same expression.
    private const string ActiveCondition = "status IN ('queued', 'running')";

    internal static async Task<JobInfo?> FindByOperationAsync(SqliteConnection connection, SqliteTransaction transaction,
        string projectId, string operationId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {Columns} FROM jobs WHERE operation_id = $operation;");
        command.Parameters.AddWithValue("$operation", operationId);
        return await ReadSingleAsync(command, projectId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<JobInfo?> GetAsync(SqliteConnection connection, SqliteTransaction? transaction, string projectId, Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {Columns} FROM jobs WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return await ReadSingleAsync(command, projectId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<JobInfo>> ListAsync(SqliteConnection connection, string projectId, int limit, Guid? before,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null, before is null
            ? $"SELECT {Columns} FROM jobs ORDER BY seq DESC LIMIT $limit;"
            : $"SELECT {Columns} FROM jobs WHERE seq < (SELECT seq FROM jobs WHERE id = $before) ORDER BY seq DESC LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", limit);
        if (before is { } cursor) command.Parameters.AddWithValue("$before", Key(cursor));
        var result = new List<JobInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Read(reader, projectId));
        return result;
    }

    /// <summary>
    /// Kinds whose jobs are listed by the object they fill, with the input field that names it. Each pair has a partial
    /// expression index (jobs_image_import_group of step 4, jobs_background_import_set of step 5, jobs_datasets_*_version of step 7); the SQL repeats the
    /// literal kind and path so that SQLite can use it.
    /// </summary>
    private static readonly Dictionary<string, string> TargetFields = new(StringComparer.Ordinal)
    {
        ["images.import"] = "$.groupId",
        ["backgrounds.import"] = "$.setId",
        ["datasets.verify"] = "$.versionId",
        ["datasets.export"] = "$.versionId",
        ["checks.evaluate"] = "$.versionId",
    };

    /// <summary>Jobs of one indexed kind whose input names the target (an image group, a background set), newest first.</summary>
    internal static async Task<IReadOnlyList<JobInfo>> ListByTargetAsync(SqliteConnection connection, string projectId, string kind, Guid targetId,
        int limit, Guid? before, CancellationToken cancellationToken)
    {
        var filter = TargetFilter(kind);
        await using var command = Command(connection, null, before is null
            ? $"SELECT {Columns} FROM jobs WHERE {filter} ORDER BY seq DESC LIMIT $limit;"
            : $"SELECT {Columns} FROM jobs WHERE {filter} AND seq < (SELECT seq FROM jobs WHERE id = $before) ORDER BY seq DESC LIMIT $limit;");
        command.Parameters.AddWithValue("$target", Key(targetId));
        command.Parameters.AddWithValue("$limit", limit);
        if (before is { } cursor) command.Parameters.AddWithValue("$before", Key(cursor));
        var result = new List<JobInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Read(reader, projectId));
        return result;
    }

    /// <summary>Removes the finished history of a deleted target; queued and running jobs block that deletion.</summary>
    internal static async Task DeleteFinishedByTargetAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, Guid targetId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"DELETE FROM jobs WHERE {TargetFilter(kind)} AND status NOT IN ('queued', 'running');");
        command.Parameters.AddWithValue("$target", Key(targetId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string TargetFilter(string kind) => TargetFields.TryGetValue(kind, out var field)
        ? $"kind = '{kind}' AND json_extract(input, '{field}') = $target"
        : throw new ArgumentException($"Jobs of kind {kind} are not indexed by target.", nameof(kind));

    /// <summary>
    /// Kinds listed on their own, newest first, with input and checkpoint (the generation runs); each has a partial index on
    /// seq (jobs_generation of step 6, jobs_datasets_* of step 7, jobs_training of step 8), and the SQL repeats the literal kind so that SQLite uses it.
    /// </summary>
    private static readonly HashSet<string> ListedKinds = new(StringComparer.Ordinal)
    {
        "generation.run", "datasets.build", "datasets.verify", "datasets.export", "training.run", "checks.evaluate",
    };

    /// <param name="condition">An extra SQL condition of the caller (over json_extract of input), its parameters set by bind.</param>
    internal static async Task<IReadOnlyList<StoredJob>> ListStoredByKindAsync(SqliteConnection connection, string projectId, string kind, int limit,
        Guid? before, CancellationToken cancellationToken, string? condition = null, Action<SqliteCommand>? bind = null)
    {
        var filter = KindFilter(kind) + (condition is null ? "" : " AND " + condition);
        await using var command = Command(connection, null, before is null
            ? $"SELECT {Columns}, input, checkpoint FROM jobs WHERE {filter} ORDER BY seq DESC LIMIT $limit;"
            : $"SELECT {Columns}, input, checkpoint FROM jobs WHERE {filter} AND seq < (SELECT seq FROM jobs WHERE id = $before) ORDER BY seq DESC LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", limit);
        if (before is { } cursor) command.Parameters.AddWithValue("$before", Key(cursor));
        bind?.Invoke(command);
        return await ReadStoredAsync(command, projectId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<long> CountByKindAsync(SqliteConnection connection, string kind, CancellationToken cancellationToken, string? condition = null,
        Action<SqliteCommand>? bind = null)
    {
        await using var command = Command(connection, null, $"SELECT count(*) FROM jobs WHERE {KindFilter(kind)}{(condition is null ? "" : " AND " + condition)};");
        bind?.Invoke(command);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>A job with its input and checkpoint, whatever its state; null when there is none.</summary>
    internal static async Task<StoredJob?> GetStoredAsync(SqliteConnection connection, SqliteTransaction? transaction, string projectId, Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {Columns}, input, checkpoint FROM jobs WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return (await ReadStoredAsync(command, projectId, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    internal static async Task<StoredJob?> FindStoredByOperationAsync(SqliteConnection connection, SqliteTransaction? transaction, string projectId,
        string operationId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {Columns}, input, checkpoint FROM jobs WHERE operation_id = $operation;");
        command.Parameters.AddWithValue("$operation", operationId);
        return (await ReadStoredAsync(command, projectId, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    /// <summary>Deletes a finished job with its receipts; an unfinished one stays. Returns whether a row was deleted.</summary>
    internal static async Task<bool> DeleteFinishedAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "DELETE FROM jobs WHERE id = $id AND status NOT IN ('queued', 'running', 'blocked');");
        command.Parameters.AddWithValue("$id", Key(id));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static string KindFilter(string kind) => ListedKinds.Contains(kind)
        ? $"kind = '{kind}'"
        : throw new ArgumentException($"Jobs of kind {kind} are not listed by kind.", nameof(kind));

    private static async Task<IReadOnlyList<StoredJob>> ReadStoredAsync(SqliteCommand command, string projectId, CancellationToken cancellationToken)
    {
        var result = new List<StoredJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StoredJob(Read(reader, projectId), reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetString(19)));
        }
        return result;
    }

    /// <summary>
    /// Queued and running jobs in queue order after the cursor job, at most limit, with input (its kind decides the GPU queue) — and the
    /// count of all of them by kind. Both queries read only the partial index jobs_active.
    /// </summary>
    internal static async Task<(IReadOnlyList<JobInfo> Items, IReadOnlyDictionary<string, int> Counts)> ListActiveAsync(SqliteConnection connection,
        string projectId, int limit, Guid? after, CancellationToken cancellationToken)
    {
        var items = new List<JobInfo>();
        await using (var command = Command(connection, null, after is null
            ? $"SELECT {Columns} FROM jobs WHERE {ActiveCondition} ORDER BY seq LIMIT $limit;"
            : $"SELECT {Columns} FROM jobs WHERE {ActiveCondition} AND seq > (SELECT seq FROM jobs WHERE id = $after) ORDER BY seq LIMIT $limit;"))
        {
            command.Parameters.AddWithValue("$limit", limit);
            if (after is { } cursor) command.Parameters.AddWithValue("$after", Key(cursor));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(Read(reader, projectId));
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = Command(connection, null, $"SELECT kind, count(*) FROM jobs WHERE {ActiveCondition} GROUP BY kind;"))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) counts[reader.GetString(0)] = reader.GetInt32(1);
        }
        return (items, counts);
    }

    /// <summary>The only query of recovery at open; it is served by the partial index jobs_unfinished.</summary>
    internal static async Task<IReadOnlyList<StoredJob>> ReadUnfinishedAsync(SqliteConnection connection, string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null,
            $"SELECT {Columns}, input, checkpoint FROM jobs WHERE status IN ('queued', 'running', 'blocked') ORDER BY seq;");
        var result = new List<StoredJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StoredJob(Read(reader, projectId), reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetString(19)));
        }
        return result;
    }

    internal static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, JobInfo job, string input,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            INSERT INTO jobs ({Columns}, input)
            VALUES ($id, $kind, $operation, $status, $progress, $text, $processed, $total, $errorCode, $errorMessage,
                $cancel, $revision, $created, $updated, $started, $finished, $result, $label, $input);
            """);
        Bind(command, job);
        command.Parameters.AddWithValue("$kind", job.Kind);
        command.Parameters.AddWithValue("$operation", job.OperationId);
        command.Parameters.AddWithValue("$created", Time(job.CreatedAt));
        command.Parameters.AddWithValue("$label", (object?)job.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$input", input);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the mutable fields of a snapshot. A finished job no longer needs its step receipts.</summary>
    internal static async Task UpdateAsync(SqliteConnection connection, SqliteTransaction transaction, JobInfo job,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            UPDATE jobs SET status = $status, progress = $progress, progress_text = $text, processed = $processed, total = $total,
                error_code = $errorCode, error_message = $errorMessage, cancel_requested = $cancel, revision = $revision,
                updated_at = $updated, started_at = $started, finished_at = $finished, result = $result
            WHERE id = $id;
            """);
        Bind(command, job);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException($"Job {job.Id} row is missing in project {job.ProjectId}.");
        }
        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Interrupted)
        {
            await using var steps = Command(connection, transaction, "DELETE FROM job_steps WHERE job_id = $id;");
            steps.Parameters.AddWithValue("$id", Key(job.Id));
            await steps.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task SaveCheckpointAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, string checkpoint,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "UPDATE jobs SET checkpoint = $checkpoint WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        command.Parameters.AddWithValue("$checkpoint", checkpoint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> HasStepAsync(SqliteConnection connection, Guid id, string stepKey, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null, "SELECT EXISTS (SELECT 1 FROM job_steps WHERE job_id = $id AND step_key = $step);");
        command.Parameters.AddWithValue("$id", Key(id));
        command.Parameters.AddWithValue("$step", stepKey);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    internal static async Task AddStepAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, string stepKey,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "INSERT OR IGNORE INTO job_steps (job_id, step_key) VALUES ($id, $step);");
        command.Parameters.AddWithValue("$id", Key(id));
        command.Parameters.AddWithValue("$step", stepKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string StatusKey(JobStatus status) => status switch
    {
        JobStatus.Queued => "queued",
        JobStatus.Running => "running",
        JobStatus.Succeeded => "succeeded",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled => "cancelled",
        JobStatus.Interrupted => "interrupted",
        JobStatus.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown job status."),
    };

    private static JobStatus ParseStatus(string value) => value switch
    {
        "queued" => JobStatus.Queued,
        "running" => JobStatus.Running,
        "succeeded" => JobStatus.Succeeded,
        "failed" => JobStatus.Failed,
        "cancelled" => JobStatus.Cancelled,
        "interrupted" => JobStatus.Interrupted,
        "blocked" => JobStatus.Blocked,
        _ => throw new InvalidDataException($"Unknown stored job status '{value}'."),
    };

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Bind(SqliteCommand command, JobInfo job)
    {
        command.Parameters.AddWithValue("$id", Key(job.Id));
        command.Parameters.AddWithValue("$status", StatusKey(job.Status));
        command.Parameters.AddWithValue("$progress", (object?)job.Progress ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", (object?)job.ProgressText ?? DBNull.Value);
        command.Parameters.AddWithValue("$processed", (object?)job.Processed ?? DBNull.Value);
        command.Parameters.AddWithValue("$total", (object?)job.Total ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)job.Error?.Code ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)job.Error?.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$cancel", job.CancelRequested ? 1 : 0);
        command.Parameters.AddWithValue("$revision", job.Sequence);
        command.Parameters.AddWithValue("$updated", Time(job.UpdatedAt));
        command.Parameters.AddWithValue("$started", job.StartedAt is { } started ? Time(started) : DBNull.Value);
        command.Parameters.AddWithValue("$finished", job.FinishedAt is { } finished ? Time(finished) : DBNull.Value);
        command.Parameters.AddWithValue("$result", job.Result is { } result ? result.GetRawText() : DBNull.Value);
    }

    private static async Task<JobInfo?> ReadSingleAsync(SqliteCommand command, string projectId, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader, projectId) : null;
    }

    private static JobInfo Read(SqliteDataReader reader, string projectId) => new(
        Guid.ParseExact(reader.GetString(0), "D"),
        reader.GetString(1),
        projectId,
        reader.GetString(2),
        ParseStatus(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetDouble(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.IsDBNull(8) ? null : new JobError(reader.GetString(8), reader.IsDBNull(9) ? "" : reader.GetString(9)),
        reader.GetInt64(10) == 1,
        Time(reader.GetInt64(12)),
        Time(reader.GetInt64(13)),
        reader.IsDBNull(14) ? null : Time(reader.GetInt64(14)),
        reader.IsDBNull(15) ? null : Time(reader.GetInt64(15)),
        reader.GetInt64(11),
        reader.IsDBNull(16) ? null : ParseResult(reader.GetString(16)),
        reader.IsDBNull(17) ? null : reader.GetString(17));

    private static JsonElement ParseResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Key(Guid id) => id.ToString("D");

    private static long Time(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset Time(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
