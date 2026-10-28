using System.Buffers.Text;
using System.Security.Cryptography;
using MautVision.Core.Interfaces.Media;

namespace MautVision.Desktop.Controllers;

/// <summary>What the user chose in a native dialog: files (multi-select), one folder, one video, a file to save (an export) or an archive to import.</summary>
internal enum FileSelectionKind
{
    Files,
    Folder,
    Video,
    SaveFile,
    Archive,
}

/// <summary>Size and time of change of a chosen file when it was chosen: a check refuses a file changed since.</summary>
internal sealed record FileStamp(long Length, DateTimeOffset LastWriteTimeUtc)
{
    internal static FileStamp Of(string path)
    {
        var info = new global::System.IO.FileInfo(path);
        return new FileStamp(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }
}

/// <summary>
/// Video — the probe of a chosen video, so an import checks its frame range without reading the file again; Stamps — size and
/// time of change of each path at the choice (model checks).
/// </summary>
internal sealed record FileSelection(FileSelectionKind Kind, IReadOnlyList<string> Paths, VideoInfo? Video = null, IReadOnlyList<FileStamp>? Stamps = null);

/// <summary>
/// Files or a folder the user chose in a native dialog, kept in the host behind an opaque token: paths never reach the
/// page. A token lives <see cref="Lifetime"/> and only in the page session that received it; a navigation or reload drops
/// all. The first import binds a token to its operationId, so a retried request of the same operation gets the same
/// selection and the existing job, while another operation cannot reuse it. Shared by every area that imports files.
/// </summary>
internal sealed class FileSelections(TimeProvider? time = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal const int Capacity = 64;

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Selection> selections = new(StringComparer.Ordinal);

    internal string Register(IReadOnlyList<string> paths, FileSelectionKind kind = FileSelectionKind.Files, VideoInfo? video = null,
        IReadOnlyList<FileStamp>? stamps = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var now = clock.GetUtcNow();
        lock (gate)
        {
            foreach (var expired in selections.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray())
            {
                selections.Remove(expired);
            }
            while (selections.Count >= Capacity)
            {
                selections.Remove(selections.MinBy(entry => entry.Value.ExpiresAt).Key);
            }
            selections.Add(token, new Selection(new FileSelection(kind, paths.ToArray(), video, stamps), now + Lifetime, null));
        }
        return token;
    }

    /// <summary>The selection of a live token for this operation; null when the token is unknown, expired or used by another operation.</summary>
    /// <param name="check">Runs before a free token is bound: a rejected request (for example, a video frame range past the
    /// end) leaves the token free for the corrected request of a new operation.</param>
    internal FileSelection? Resolve(string token, string operationId, Action<FileSelection>? check = null)
    {
        lock (gate)
        {
            if (!selections.TryGetValue(token, out var selection) || selection.ExpiresAt <= clock.GetUtcNow()) return null;
            check?.Invoke(selection.Value);
            if (selection.OperationId is null)
            {
                selections[token] = selection with { OperationId = operationId };
                return selection.Value;
            }
            return selection.OperationId == operationId ? selection.Value : null;
        }
    }

    /// <summary>The selection of a live token without binding it, for previews before the run; null when unknown or expired.</summary>
    internal FileSelection? Peek(string token)
    {
        lock (gate)
        {
            return selections.TryGetValue(token, out var selection) && selection.ExpiresAt > clock.GetUtcNow() ? selection.Value : null;
        }
    }

    internal void ResetSession()
    {
        lock (gate) selections.Clear();
    }

    private sealed record Selection(FileSelection Value, DateTimeOffset ExpiresAt, string? OperationId);
}
