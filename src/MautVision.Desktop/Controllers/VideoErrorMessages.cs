namespace MautVision.Desktop.Controllers;

/// <summary>Texts of the video codes; shared by every area that takes frames from a video (images, later backgrounds).</summary>
internal static class VideoErrorMessages
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["video.ffmpeg_missing"] = "Не найден компонент чтения видео (ffmpeg). Переустановите Maut Vision.",
        ["video.unsupported"] = "Формат не поддерживается. Выберите видео MP4, MKV, AVI, MOV, WebM или M4V.",
        ["video.unreadable"] = "Не удалось прочитать видео. Проверьте, что файл доступен, не повреждён и содержит видеодорожку.",
        ["video.frames_invalid"] = "Укажите начало не меньше 0 с, интервал не меньше 0,001 с и количество кадров не меньше 1.",
        ["video.range_invalid"] = "Последний кадр выходит за длительность видео. Уменьшите количество или интервал.",
        ["video.frame_unavailable"] = "Кадр на этой отметке не удалось прочитать: видеодорожка закончилась раньше.",
    };
}
