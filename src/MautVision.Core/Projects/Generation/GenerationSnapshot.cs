using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Projects.Storage;

namespace MautVision.Core.Projects.Generation;

/// <summary>
/// The files a run reads, fixed for its lifetime: only the images of its bindings and the backgrounds of its manifest, as hard
/// links in files/generation-runs/&lt;runId&gt;/ (a copy when a link is not possible, for example on another volume). Material
/// files never change, so a link is a stable copy that costs no space; deleting the material later leaves the link readable.
/// The folder is removed when the run finishes or is deleted.
/// </summary>
internal sealed class GenerationSnapshot
{
    internal const string Kind = "generation-runs";
    private const string AssetsFolder = "assets";
    private const string BackgroundsFolder = "backgrounds";

    private GenerationSnapshot(string directory)
    {
        Directory = directory;
    }

    internal string Directory { get; }

    internal string AssetPath(ManifestAsset asset) => Path.Combine(Directory, AssetsFolder, asset.Id.ToString("D") + asset.Extension);

    internal string BackgroundPath(Guid id) => Path.Combine(Directory, BackgroundsFolder, id.ToString("D") + ".png");

    internal static GenerationSnapshot Open(MaterialFiles files, Guid runId) => new(Path.Combine(files.GetDirectory(Kind), runId.ToString("D")));

    /// <summary>Links the manifest files into a fresh run folder; returns how many had to be copied instead of linked.</summary>
    internal static (GenerationSnapshot Snapshot, int Copied) Create(MaterialFiles files, Guid runId, GenerationManifest manifest, CancellationToken cancellationToken)
    {
        var parent = files.CreateDirectory(Kind);
        RequireDirectory(parent);
        var snapshot = Open(files, runId);
        // A preparation interrupted earlier starts over: its links may be incomplete.
        Release(files, runId);
        System.IO.Directory.CreateDirectory(Path.Combine(snapshot.Directory, AssetsFolder));
        System.IO.Directory.CreateDirectory(Path.Combine(snapshot.Directory, BackgroundsFolder));
        var copied = 0;
        foreach (var asset in manifest.Bindings.SelectMany(binding => binding.Assets).DistinctBy(asset => asset.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Link(files.GetPath(ClassAsset.ResourceKind, asset.Id, asset.Extension), snapshot.AssetPath(asset))) copied++;
        }
        foreach (var id in manifest.Backgrounds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Link(files.GetPath(BackgroundItem.ResourceKind, id, ".png"), snapshot.BackgroundPath(id))) copied++;
        }
        return (snapshot, copied);
    }

    /// <summary>Removes the run folder if it exists; a link or junction in its place is never followed.</summary>
    internal static void Release(MaterialFiles files, Guid runId)
    {
        var directory = Open(files, runId).Directory;
        if (ProjectStore.ValidateEntry(directory) is not { } attributes) return;
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException("A generation snapshot must be a directory.");
        System.IO.Directory.Delete(directory, recursive: true);
    }

    /// <summary>True when the file is a hard link of the source, false when it had to be copied.</summary>
    private static bool Link(string source, string target)
    {
        if (HardLink.TryCreate(target, source)) return true;
        File.Copy(source, target, overwrite: true);
        return false;
    }

    private static void RequireDirectory(string path)
    {
        if (ProjectStore.ValidateEntry(path) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
            throw new IOException("The generation snapshot root must be a directory.");
    }
}
