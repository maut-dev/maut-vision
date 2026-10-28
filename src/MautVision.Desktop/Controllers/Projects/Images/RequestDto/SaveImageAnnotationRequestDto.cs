namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Box as fractions 0–1 of the oriented frame from the top-left corner.</summary>
public sealed record ImageBoxRequestDto(double X, double Y, double Width, double Height);

/// <summary>Id — an object of the current revision or null for a new one; ClassVariantId — a variant of this class or null.</summary>
public sealed record AnnotationObjectRequestDto(Guid? Id, Guid ClassId, Guid? ClassVariantId, ImageBoxRequestDto Box);

/// <summary>AnnotationRevision — the current annotation revision the user saw. ReviewState: unreviewed, reviewed.</summary>
public sealed record SaveImageAnnotationRequestDto(
    string ProjectId,
    Guid ImageId,
    long AnnotationRevision,
    AnnotationObjectRequestDto[] Objects,
    ImageBoxRequestDto? Roi,
    string ReviewState);
