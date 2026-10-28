using System.Collections.Concurrent;
using MautVision.Core.Projects.Storage;

namespace MautVision.Core.Resources;

/// <summary>
/// Finds the stored extension of a material, e.g. ".png" from its row in project.db; null means the material does not exist.
/// It never returns a path: the path is built by <see cref="MaterialFiles"/> from the kind, the ID and this extension.
/// </summary>
internal delegate ValueTask<string?> ResolveOriginalAsync(ProjectStore store, MaterialFiles files, Guid id, CancellationToken cancellationToken);

/// <summary>
/// Finds the file of a material stored in nested folders, e.g. "&lt;versionId&gt;/images/train/&lt;imageId&gt;.jpg" under
/// files/&lt;kind&gt;/ from its row in project.db; null means the material does not exist. Segments are separated by '/'.
/// </summary>
internal delegate ValueTask<string?> ResolveNestedAsync(ProjectStore store, MaterialFiles files, Guid id, CancellationToken cancellationToken);

/// <summary>
/// Material kinds whose files may be served. The Core composition registers a kind together with the manager that owns
/// it (images, backgrounds, …); a request for any other kind is refused before a path is built.
/// </summary>
internal sealed class ResourceKinds
{
    /// <summary>Registered only for smoke checks (<see cref="Interfaces.CoreOptions.SmokeFixtures"/>).</summary>
    internal const string SmokeFixtureKind = "smoke-fixture";

    private readonly ConcurrentDictionary<string, ResolveOriginalAsync> kinds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResolveNestedAsync> nested = new(StringComparer.Ordinal);

    internal void Register(string kind, ResolveOriginalAsync resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (!MaterialFiles.IsValidKind(kind))
        {
            throw new ArgumentException("Material kind must be a lowercase identifier.", nameof(kind));
        }
        if (nested.ContainsKey(kind) || !kinds.TryAdd(kind, resolve))
        {
            throw new InvalidOperationException($"Resource kind {kind} is already registered.");
        }
    }

    /// <summary>A kind whose files lie in nested folders (dataset samples); its path is checked level by level when served.</summary>
    internal void RegisterNested(string kind, ResolveNestedAsync resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (!MaterialFiles.IsValidKind(kind))
        {
            throw new ArgumentException("Material kind must be a lowercase identifier.", nameof(kind));
        }
        if (kinds.ContainsKey(kind) || !nested.TryAdd(kind, resolve))
        {
            throw new InvalidOperationException($"Resource kind {kind} is already registered.");
        }
    }

    internal ResolveOriginalAsync? Find(string? kind) => kind is not null && kinds.TryGetValue(kind, out var resolve) ? resolve : null;

    internal ResolveNestedAsync? FindNested(string? kind) => kind is not null && nested.TryGetValue(kind, out var resolve) ? resolve : null;

    /// <summary>
    /// A kind without database rows: the first existing files/&lt;kind&gt;/&lt;id&gt;&lt;extension&gt; wins.
    /// Used by fixtures; real kinds read the extension from their table instead of probing the disk.
    /// </summary>
    internal static ResolveOriginalAsync ByExtensions(string kind, params string[] extensions) => (_, files, id, _) =>
    {
        foreach (var extension in extensions)
        {
            if (File.Exists(files.GetPath(kind, id, extension)))
            {
                return ValueTask.FromResult<string?>(extension);
            }
        }
        return ValueTask.FromResult<string?>(null);
    };
}
