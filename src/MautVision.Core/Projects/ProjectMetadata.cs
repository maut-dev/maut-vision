using MautVision.Core.Interfaces.Projects;

namespace MautVision.Core.Projects;

/// <summary>
/// Содержимое projects/&lt;projectId&gt;/project.json. Неизменяемый снимок: изменение
/// создаёт новую запись с увеличенной Revision.
/// </summary>
internal sealed record ProjectMetadata
{
    internal const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string BaseModelId { get; init; }
    public required InputSize InputSize { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
}
