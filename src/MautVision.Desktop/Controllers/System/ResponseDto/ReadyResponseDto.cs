namespace MautVision.Desktop.Controllers.System.ResponseDto;

/// <summary>ElapsedMilliseconds — time from the process start to the first readiness mark; repeated marks do not change it.</summary>
public sealed record ReadyResponseDto(long ElapsedMilliseconds);
