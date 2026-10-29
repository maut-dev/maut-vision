namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>ClassIds — all project classes, including archived ones, in the new order. OrderRevision — classOrderRevision from the catalog snapshot.</summary>
public sealed record ReorderClassesRequestDto(
    string ProjectId,
    Guid[] ClassIds,
    long OrderRevision);
