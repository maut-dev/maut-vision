namespace MautVision.Core.Interfaces.Projects.Resources;

/// <summary>
/// Материал проекта, файл которого можно отдать UI: вид (например, <c>images</c>) и ID. Путь к файлу по ссылке
/// строит только Core; неизвестный вид — CoreException <c>resources.kind_unknown</c>.
/// </summary>
public readonly record struct ResourceReference(string Kind, Guid Id);

public enum ResourceVariant
{
    /// <summary>Исходный файл материала без изменений.</summary>
    Original,

    /// <summary>JPEG не больше 256 px по большей стороне с применённой EXIF-ориентацией; кешируется на диске.</summary>
    Thumbnail,
}
