namespace MautVision.Core.Interfaces.Jobs;

/// <summary>
/// Files chosen by the user for one class variant. Files — absolute paths; they are stored in the job input
/// in project.db and never leave it (into the job snapshot or the UI).
/// </summary>
public sealed record ImportClassAssetsInput(Guid ClassVariantId, IReadOnlyList<string> Files);

/// <summary>
/// Import of source images of a class variant (CPU). PNG, JPEG, WebP and BMP are accepted; bytes are copied unchanged.
/// An identical file (SHA-256) in the same binding is skipped. The result is <see cref="ImportClassAssetsResult"/> in
/// <see cref="JobInfo.Result"/>, including after cancellation. If the binding is archived or deleted, the job ends
/// with <c>classes.variant_unavailable</c>.
/// </summary>
[JobKind("classes.import_assets")]
public interface IImportClassAssetsJob : IJobDefinition<ImportClassAssetsInput>
{
}

/// <summary>Rejected file: only the name without the folder and the reason code (<c>classes.asset_invalid</c>, <c>classes.asset_unreadable</c>).</summary>
public sealed record ImportRejectedFile(string Name, string Reason);

/// <summary>
/// Import result. Rejected — number of rejected files; RejectedFiles — the first <see cref="MaxRejectedFiles"/> of them.
/// </summary>
public sealed record ImportClassAssetsResult(int Total, int Added, int Duplicates, int Rejected, IReadOnlyList<ImportRejectedFile> RejectedFiles)
{
    public const int MaxRejectedFiles = 50;
}
