namespace MautVision.Desktop.Controllers.System.RequestDto;

/// <summary>UI показала библиотеку после запуска; Projects — число полученных проектов.</summary>
public sealed record ReadyRequestDto(int Projects);
