using System.Globalization;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds;

internal static class BackgroundErrorMessages
{
    private const string Changed = "Данные уже изменились. Обновите список и повторите действие.";

    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["backgrounds.project_not_open"] = "Проект не открыт. Откройте проект, чтобы работать с фонами.",
        ["backgrounds.name_invalid"] = "Введите непустое название набора фонов длиной до 120 символов.",
        ["backgrounds.description_invalid"] = "Описание набора фонов должно быть не длиннее 4000 символов.",
        ["backgrounds.set_not_found"] = "Набор фонов уже удалён или недоступен. Обновите список.",
        ["backgrounds.item_not_found"] = "Фон уже удалён или недоступен. Обновите набор.",
        ["backgrounds.revision_conflict"] = Changed,
        ["backgrounds.set_archived"] = "Набор фонов в архиве. Восстановите его, чтобы изменять набор и добавлять фоны.",
        ["backgrounds.set_importing"] = "В набор идёт подготовка фонов. Дождитесь её завершения или отмените задачу.",
        ["backgrounds.selection_invalid"] = "Выберите от 1 до 400 разных фонов одного набора.",
        ["backgrounds.selection_expired"] = "Выбор файлов или видео устарел. Выберите их снова.",
        ["backgrounds.too_many_sources"] = "За один запуск можно нарезать не больше 500 файлов. Разделите выбор на части.",
        ["backgrounds.sources_empty"] = "Нечего импортировать: ни один файл не выбран.",
        ["backgrounds.blur_invalid"] = "Радиус размытия — от 0 до 1000 px, «от» не больше «до»; seed — целое число от 0 до 4 294 967 295 или пустое поле.",
        ["backgrounds.crop_invalid"] = "Рамка выходит за границы кадра. Поставьте её внутри изображения.",
        ["backgrounds.count_invalid"] = "Количество фонов — от 1 до числа доступных кадров. Уменьшите шаг или количество.",
        ["backgrounds.video_too_small"] = "Видео меньше входа модели: рамка не помещается.",
        ["backgrounds.region_invalid"] = "Рамка выходит за границы кадра видео. Передвиньте рамку.",
        ["backgrounds.deletion_revision_conflict"] = "Материалы изменились после открытия подтверждения. Повторите просмотр состава удаления.",
        ["backgrounds.deletion_blocked"] = "Удаление недоступно: в набор идёт подготовка фонов или на него ссылаются пресеты генерации. Проверьте зависимости снова.",
        ["backgrounds.import_sources_changed"] = "Файлы или видео изменились до продолжения подготовки. Добавленные фоны сохранены; запустите подготовку снова — повторы будут пропущены.",
        ["backgrounds.no_usable_media"] = "Ни один файл не подошёл. Причины пропуска — в истории подготовки набора.",
        ["backgrounds.wrong_size"] = "Размер кадра не совпадает с размером набора",
        ["backgrounds.crop_out_of_bounds"] = "Рамка выходит за границы кадра",
        ["backgrounds.image_unreadable"] = "Не удалось прочитать изображение",
        ["backgrounds.similar"] = "Почти не отличается от уже взятого кадра",
        ["backgrounds.duplicate"] = "Такой кадр уже есть в наборе",
    };

    /// <summary>
    /// Text of a skip reason: with the sizes «Размер a×b, нужен W×H» and «Рамка W×H не помещается
    /// в кадр a×b»; other reasons — by code (video.frame_unavailable comes from the video texts).
    /// </summary>
    internal static string Reason(string code, int? width, int? height, InputSize target, Func<string, string> messageFor)
    {
        if (width is { } actualWidth && height is { } actualHeight)
        {
            var actual = actualWidth.ToString(CultureInfo.InvariantCulture) + "×" + actualHeight.ToString(CultureInfo.InvariantCulture);
            var needed = target.Width.ToString(CultureInfo.InvariantCulture) + "×" + target.Height.ToString(CultureInfo.InvariantCulture);
            if (code == "backgrounds.wrong_size") return $"Размер {actual}, нужен {needed}";
            if (code == "backgrounds.crop_out_of_bounds") return $"Рамка {needed} не помещается в кадр {actual}";
        }
        return messageFor(code);
    }

    /// <summary>A line of the blocker list in the deletion dialog.</summary>
    internal static string For(DeletionBlocker blocker) => blocker.Code switch
    {
        "backgrounds.import_active" => $"Выполняется подготовка фонов в этот набор: {blocker.Count}.",
        "generation.preset_reference" => Generation.GenerationErrorMessages.PresetReference(blocker),
        _ => $"Набор используется: {blocker.Count}.",
    };
}
