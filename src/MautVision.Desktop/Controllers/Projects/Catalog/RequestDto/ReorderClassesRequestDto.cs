namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>ClassIds — все классы проекта, включая архивные, в новом порядке. OrderRevision — classOrderRevision из снимка каталога.</summary>
public sealed record ReorderClassesRequestDto(
    string ProjectId,
    Guid[] ClassIds,
    long OrderRevision);
