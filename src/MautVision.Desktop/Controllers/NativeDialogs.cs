namespace MautVision.Desktop.Controllers;

/// <summary>
/// Native dialogs owned by the main window. Each returns null when the user cancels; smoke runs never show them and
/// register fixture files in <see cref="FileSelections"/> instead (the video dialog returns the file the smoke set).
/// </summary>
/// <param name="PickImageFiles">Multi-select dialog for PNG, JPEG, WebP and BMP files with the given title.</param>
/// <param name="PickFolder">Folder dialog with the given title.</param>
/// <param name="PickVideo">Single-file dialog for MP4, MKV, AVI, MOV, WebM and M4V with the given title.</param>
/// <param name="PickSaveFile">"Save as" for a ZIP archive with the given title, file name and filter text ("ZIP archive"); overwriting is
/// confirmed in the dialog.</param>
/// <param name="PickImageFile">Single-file dialog for one PNG, JPEG, WebP or BMP image with the given title (a model check).</param>
/// <param name="PickArchive">Single-file dialog for a project archive (*.zip) with the given title.</param>
internal sealed record NativeDialogs(
    Func<string, CancellationToken, Task<IReadOnlyList<string>?>> PickImageFiles,
    Func<string, CancellationToken, Task<string?>> PickFolder,
    Func<string, CancellationToken, Task<string?>> PickVideo,
    Func<string, string, string, CancellationToken, Task<string?>> PickSaveFile,
    Func<string, CancellationToken, Task<string?>> PickImageFile,
    Func<string, CancellationToken, Task<string?>> PickArchive);
