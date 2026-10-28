namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>Box as fractions 0–1 of the oriented frame from the top-left corner.</summary>
public sealed record ImageBoxResponseDto(double X, double Y, double Width, double Height);

/// <summary>Annotation object: ClassVariantId — a class variant (binding) or null ("No variant").</summary>
public sealed record AnnotationObjectResponseDto(Guid Id, Guid ClassId, Guid? ClassVariantId, ImageBoxResponseDto Box);

/// <summary>Annotation revision (immutable). Roi — area box or null. ReviewState: unreviewed, reviewed.</summary>
public sealed record ImageAnnotationResponseDto(
    Guid ImageId,
    long Revision,
    AnnotationObjectResponseDto[] Objects,
    ImageBoxResponseDto? Roi,
    string ReviewState,
    DateTimeOffset CreatedAt);

/// <summary>Save result: the image with new revisions and the new annotation revision (with IDs of new objects).</summary>
public sealed record SavedImageAnnotationResponseDto(ImageResponseDto Image, ImageAnnotationResponseDto Annotation);
