namespace MautVision.Desktop.Controllers.BaseModels.ResponseDto;

/// <summary>Status: not_installed, installing, installed, failed. Progress: 0–100 during installation.</summary>
public sealed record BaseModelResponseDto(
    string Id,
    string Family,
    string Variant,
    long ByteSize,
    int Stride,
    string Status,
    double Progress,
    BaseModelErrorDto? Error);
