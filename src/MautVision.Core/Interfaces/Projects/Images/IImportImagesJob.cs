using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;

namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>
/// Вид источника импорта. Новый вид (кадры видео) добавляется значением и необязательными полями источника,
/// без изменения остального входа задачи.
/// </summary>
public enum ImageImportSourceKind
{
    /// <summary>Один файл PNG, JPEG, WebP или BMP.</summary>
    File,

    /// <summary>Папка: файлы с этими расширениями, по относительному пути в естественном порядке; ссылки не читаются.</summary>
    Folder,

    /// <summary>
    /// Кадры видео (MP4, MKV, AVI, MOV, WebM, M4V) на отметках <see cref="ImageImportSource.Frames"/>: PNG исходного размера
    /// с именем «&lt;имя видео&gt;-frame-&lt;отметка, мс&gt;.png» и происхождением video.
    /// </summary>
    Video,
}

/// <summary>
/// Источник импорта. Path — абсолютный путь; хранится во входе задачи в project.db и наружу не выходит.
/// Recursive — для папки: включая вложенные папки. Frames — для видео: отметки кадров (обязательны).
/// </summary>
public sealed record ImageImportSource(ImageImportSourceKind Kind, string Path, bool Recursive = false, VideoFrameRange? Frames = null);

/// <summary>
/// Вход задачи импорта в ручную группу. Порядок кадров в группе — порядок источников, внутри папки — по относительному
/// пути. SkipDuplicates — пропускать кадры, пиксели которых (PixelHash) уже есть в группе.
/// </summary>
public sealed record ImportImagesInput(Guid GroupId, IReadOnlyList<ImageImportSource> Sources, bool SkipDuplicates = true);

/// <summary>
/// Итог импорта: Total — файлов (кадров) после раскрытия папок и видео, Added, Duplicates (пропущенные повторы), Rejected;
/// RejectedFiles — первые <see cref="MaxRejectedFiles"/> отклонённых (имя или относительный путь в папке, код причины
/// <c>images.file_invalid</c>, <c>images.file_unreadable</c> или <c>video.frame_unavailable</c> — видео кончилось раньше отметки).
/// </summary>
public sealed record ImportImagesResult(int Total, int Added, int Duplicates, int Rejected, IReadOnlyList<ImportRejectedFile> RejectedFiles)
{
    public const int MaxRejectedFiles = 50;
}

/// <summary>
/// Импорт изображений в группу (CPU, возобновляемая). Каждый файл декодируется один раз полностью: размер с учётом
/// EXIF-ориентации, проверка пикселей, PixelHash; файл копируется без изменений. Ревизия группы не требуется. Группа не
/// ручная — <c>images.group_not_manual</c>, в архиве или удалена — <c>images.group_archived</c>, <c>images.group_not_found</c>;
/// папка недоступна — <c>images.folder_unavailable</c>; состав папок или видео изменился до продолжения после перезапуска —
/// <c>images.import_sources_changed</c>. Видео: нет ffmpeg — <c>video.ffmpeg_missing</c>, файл не читается —
/// <c>video.unreadable</c> или <c>video.unsupported</c>, отметки неверны — <c>video.frames_invalid</c>, последняя не раньше
/// конца — <c>video.range_invalid</c>. Кадры одного видео декодирует один процесс ffmpeg; хеш семьи (SHA-256 файла)
/// считается в задаче. Отмена и сбой оставляют добавленное; итог — в <see cref="JobInfo.Result"/>.
/// </summary>
[JobKind("images.import")]
public interface IImportImagesJob : IJobDefinition<ImportImagesInput>
{
}
