using System.Text.Json;

namespace MautVision.Core.Interfaces.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>The run ended together with the process, and the job kind cannot resume.</summary>
    Interrupted,

    /// <summary>This version of the application cannot run the job (an unknown kind, incompatible data); the data is kept.</summary>
    Blocked,
}

/// <summary>Code — a stable error code; Message — a text for the log, the UI chooses the text for the user by the code.</summary>
public sealed record JobError(string Code, string Message);

/// <summary>
/// A snapshot of a job. Sequence grows with every saved change: of two snapshots the one with the larger Sequence is newer.
/// Progress — 0–100 or null when the amount of work is unknown; ProgressMessage — what the job is doing as a code with values
/// (<c>progress.epoch</c> with epoch and total), or a stored text of version 1.0.0; null when the job reports only counters.
/// Result — the summary of the job in JSON defined by its kind (for example, <see cref="ImportClassAssetsResult"/>); null until there is one.
/// Label — what the job works on, given by the one who enqueued it: a name as a text (a group, a dataset) or a code with values
/// (<c>jobLabels.version</c> with number); its text is at most <see cref="MaxLabelLength"/> characters; null for jobs without a label.
/// </summary>
public sealed record JobInfo(
    Guid Id,
    string Kind,
    string ProjectId,
    string OperationId,
    JobStatus Status,
    double? Progress,
    CodedText? ProgressMessage,
    long? Processed,
    long? Total,
    JobError? Error,
    bool CancelRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence,
    JsonElement? Result = null,
    CodedText? Label = null)
{
    public const int MaxLabelLength = 200;
}
