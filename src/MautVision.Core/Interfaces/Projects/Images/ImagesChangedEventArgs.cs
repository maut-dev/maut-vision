namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>Changed or deleted (Revision = null) object: a group or an image.</summary>
public sealed record ImagesEntityChange(Guid Id, long? Revision);

/// <summary>
/// What changed in the project images. Groups — groups with new revisions or deleted ones; Images — images
/// changed by an explicit operation (annotation, archive, move, deletion of selected ones); ImagesChangedIn — groups whose
/// content or counters changed (import, move, archive, deletion): the UI rereads their group and pages.
/// Import reports changes at most once per 500 ms per group.
/// </summary>
public sealed class ImagesChangedEventArgs(
    string projectId,
    IReadOnlyList<ImagesEntityChange> groups,
    IReadOnlyList<ImagesEntityChange> images,
    IReadOnlyList<Guid> imagesChangedIn) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<ImagesEntityChange> Groups { get; } = groups;
    public IReadOnlyList<ImagesEntityChange> Images { get; } = images;
    public IReadOnlyList<Guid> ImagesChangedIn { get; } = imagesChangedIn;
}
