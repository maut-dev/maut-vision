namespace MautVision.Core.Interfaces.Projects;

/// <summary>Model input size in pixels; both sides are multiples of the stride of the chosen base model.</summary>
public sealed record InputSize(int Width, int Height);

/// <summary>
/// User-editable project settings. BaseModelId — the base model for training;
/// InputSize sets the size of backgrounds, generation frames and datasets.
/// </summary>
public sealed record ProjectSettings(string Name, string Description, string BaseModelId, InputSize InputSize);
