namespace MautVision.Core.Interfaces.Projects.Resources;

/// <summary>
/// A project material whose file can be given to the UI: kind (for example, <c>images</c>) and ID. The file path from a reference
/// is built only by Core; an unknown kind — CoreException <c>resources.kind_unknown</c>.
/// </summary>
public readonly record struct ResourceReference(string Kind, Guid Id);

public enum ResourceVariant
{
    /// <summary>The source file of the material, unchanged.</summary>
    Original,

    /// <summary>JPEG no larger than 256 px on the longer side with EXIF orientation applied; cached on disk.</summary>
    Thumbnail,
}
