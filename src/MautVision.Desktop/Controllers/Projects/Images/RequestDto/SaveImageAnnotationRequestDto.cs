namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Рамка в долях 0–1 ориентированного кадра от левого верхнего угла.</summary>
public sealed record ImageBoxRequestDto(double X, double Y, double Width, double Height);

/// <summary>Id — объект текущей ревизии или null для нового; ClassVariantId — вариант этого класса или null.</summary>
public sealed record AnnotationObjectRequestDto(Guid? Id, Guid ClassId, Guid? ClassVariantId, ImageBoxRequestDto Box);

/// <summary>AnnotationRevision — текущая ревизия разметки, которую видел пользователь. ReviewState: unreviewed, reviewed.</summary>
public sealed record SaveImageAnnotationRequestDto(
    string ProjectId,
    Guid ImageId,
    long AnnotationRevision,
    AnnotationObjectRequestDto[] Objects,
    ImageBoxRequestDto? Roi,
    string ReviewState);
