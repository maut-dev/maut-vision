using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Desktop.Controllers.Projects.Images;

internal static class ImageErrorMessages
{
    private const string Changed = "Данные уже изменились. Обновите список и повторите действие.";

    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["images.project_not_open"] = "Проект не открыт. Откройте проект, чтобы работать с изображениями.",
        ["images.name_invalid"] = "Введите непустое название группы длиной до 120 символов.",
        ["images.description_invalid"] = "Описание группы должно быть не длиннее 4000 символов.",
        ["images.group_not_found"] = "Группа изображений уже удалена или недоступна. Обновите список.",
        ["images.image_not_found"] = "Изображение уже удалено или недоступно. Обновите список.",
        ["images.annotation_not_found"] = "Такой ревизии разметки нет. Обновите изображение.",
        ["images.revision_conflict"] = Changed,
        ["images.group_archived"] = "Группа в архиве. Восстановите её, чтобы изменять группу, изображения и разметку.",
        ["images.group_busy"] = "Генерация наполняет эту группу. Редактирование станет доступно после завершения.",
        ["images.group_importing"] = "В группу идёт импорт. Дождитесь его завершения или отмените задачу.",
        ["images.group_not_manual"] = "Импорт доступен только в группы исходных изображений.",
        ["images.origin_mismatch"] = "Перемещать можно только между группами одного происхождения: исходные к исходным, сгенерированные к сгенерированным.",
        ["images.selection_invalid"] = "Выберите от 1 до 400 разных изображений одной группы и другую группу для перемещения.",
        ["images.image_archived"] = "Изображение в архиве. Восстановите его, чтобы изменить разметку или переместить.",
        ["images.box_invalid"] = "Рамка выходит за кадр или не имеет размера. Нарисуйте её внутри изображения.",
        ["images.object_id_invalid"] = "Разметка изменилась в другом окне. Обновите изображение и повторите изменения.",
        ["images.reference_unavailable"] = "Класс или вариант объекта недоступен. Выберите активный класс и вариант этого класса.",
        ["images.annotation_invalid"] = "Разметка слишком большая: на изображении может быть не больше 1000 объектов.",
        ["images.deletion_revision_conflict"] = "Материалы изменились после открытия подтверждения. Повторите просмотр состава удаления.",
        ["images.deletion_blocked"] = "Удаление недоступно: в группу идёт импорт или генерация либо из неё собирается датасет. Проверьте зависимости снова.",
        ["images.file_invalid"] = "Недопустимое или повреждённое изображение. Загрузите целый файл PNG, JPEG, WebP или BMP.",
        ["images.file_unreadable"] = "Не удалось прочитать файл. Проверьте, что он доступен, и импортируйте его снова.",
        ["images.folder_unavailable"] = "Папка недоступна. Проверьте, что она существует и доступна для чтения.",
        ["images.import_sources_changed"] = "Состав папки или видео изменился до продолжения импорта. Добавленные кадры сохранены; запустите импорт снова — повторы будут пропущены.",
        ["images.selection_expired"] = "Выбор файлов, папки или видео устарел. Выберите их снова.",
    };

    /// <summary>A line of the blocker list in the deletion dialog.</summary>
    internal static string For(DeletionBlocker blocker) => blocker.Code switch
    {
        "images.import_active" => $"Выполняется импорт в эту группу: {blocker.Count}.",
        "images.generation_active" => $"Генерация группы «{blocker.Examples.FirstOrDefault()}».",
        "datasets.build_active" => $"Идёт сборка датасета из этой группы: {blocker.Count}.",
        _ => $"Материал используется: {blocker.Count}.",
    };
}
