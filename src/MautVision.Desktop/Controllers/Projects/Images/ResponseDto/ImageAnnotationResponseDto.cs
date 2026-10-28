namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>Рамка в долях 0–1 ориентированного кадра от левого верхнего угла.</summary>
public sealed record ImageBoxResponseDto(double X, double Y, double Width, double Height);

/// <summary>Объект разметки: ClassVariantId — вариант класса (привязка) или null («Без варианта»).</summary>
public sealed record AnnotationObjectResponseDto(Guid Id, Guid ClassId, Guid? ClassVariantId, ImageBoxResponseDto Box);

/// <summary>Ревизия разметки (неизменяемая). Roi — рамка области или null. ReviewState: unreviewed, reviewed.</summary>
public sealed record ImageAnnotationResponseDto(
    Guid ImageId,
    long Revision,
    AnnotationObjectResponseDto[] Objects,
    ImageBoxResponseDto? Roi,
    string ReviewState,
    DateTimeOffset CreatedAt);

/// <summary>Результат сохранения: изображение с новыми ревизиями и новая ревизия разметки (с ID новых объектов).</summary>
public sealed record SavedImageAnnotationResponseDto(ImageResponseDto Image, ImageAnnotationResponseDto Annotation);
