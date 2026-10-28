namespace MautVision.Desktop.Controllers.System.ResponseDto;

/// <summary>ElapsedMilliseconds — время от старта процесса до первой отметки готовности; повторные отметки его не меняют.</summary>
public sealed record ReadyResponseDto(long ElapsedMilliseconds);
