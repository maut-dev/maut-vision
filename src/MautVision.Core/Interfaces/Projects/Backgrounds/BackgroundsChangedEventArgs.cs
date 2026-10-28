namespace MautVision.Core.Interfaces.Projects.Backgrounds;

/// <summary>Changed or deleted (Revision = null) object: a set or a background.</summary>
public sealed record BackgroundsEntityChange(Guid Id, long? Revision);

/// <summary>
/// What changed in the project backgrounds. Sets — sets with new revisions or deleted ones; Items — backgrounds changed by an explicit operation
/// ("Remove", "Restore", deletion of selected ones); ItemsChangedIn — sets whose content or counters changed (preparation,
/// "Remove", deletion): the UI rereads their set and pages. Preparation reports changes at most once per 500 ms per set.
/// </summary>
public sealed class BackgroundsChangedEventArgs(
    string projectId,
    IReadOnlyList<BackgroundsEntityChange> sets,
    IReadOnlyList<BackgroundsEntityChange> items,
    IReadOnlyList<Guid> itemsChangedIn) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<BackgroundsEntityChange> Sets { get; } = sets;
    public IReadOnlyList<BackgroundsEntityChange> Items { get; } = items;
    public IReadOnlyList<Guid> ItemsChangedIn { get; } = itemsChangedIn;
}
