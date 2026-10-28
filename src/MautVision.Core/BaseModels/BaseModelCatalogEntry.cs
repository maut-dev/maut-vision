namespace MautVision.Core.BaseModels;

/// <summary>Запись yolo-catalog.json. Не выходит за пределы BaseModels.</summary>
internal sealed record BaseModelCatalogEntry
{
    public required string Id { get; init; }
    public required string Family { get; init; }
    public required string Variant { get; init; }
    public required string Task { get; init; }
    public required string FileName { get; init; }
    public required long ByteSize { get; init; }
    public required string Sha256 { get; init; }
    public required string Url { get; init; }
    public required int Stride { get; init; }
    public required string[] YamlNames { get; init; }
    public required string[] YamlFamilies { get; init; }
    public required string Scale { get; init; }
}
