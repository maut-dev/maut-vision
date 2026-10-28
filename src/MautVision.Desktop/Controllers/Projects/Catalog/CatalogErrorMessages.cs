using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Desktop.Controllers.Projects.Catalog;

internal static class CatalogErrorMessages
{
    private const string Missing = "Материал уже удалён или недоступен в этом проекте. Обновите список.";
    private const string NameInvalid = "Введите непустое название длиной до 120 символов.";
    private const string DescriptionInvalid = "Описание должно быть не длиннее 2000 символов.";
    private const string NameDuplicate = "Такое название уже существует, в том числе в архиве.";
    private const string DeletionConflict = "Материалы изменились после открытия подтверждения. Повторите просмотр состава удаления.";
    private const string DeletionBlocked = "Удаление недоступно: материалы используются или выполняются задачи. Проверьте зависимости снова.";

    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["variants.project_not_open"] = "Проект не открыт. Откройте проект, чтобы работать с вариантами.",
        ["variants.name_invalid"] = NameInvalid,
        ["variants.description_invalid"] = DescriptionInvalid,
        ["variants.name_duplicate"] = NameDuplicate,
        ["variants.not_found"] = Missing,
        ["variants.archived"] = "Вариант в архиве. Восстановите его, чтобы изменить название или описание.",
        ["variants.revision_conflict"] = "Вариант уже изменился. Обновите список и повторите изменение.",
        ["variants.deletion_revision_conflict"] = DeletionConflict,
        ["variants.deletion_blocked"] = DeletionBlocked,
        ["classes.project_not_open"] = "Проект не открыт. Откройте проект, чтобы работать с классами.",
        ["classes.name_invalid"] = NameInvalid,
        ["classes.description_invalid"] = DescriptionInvalid,
        ["classes.name_duplicate"] = NameDuplicate,
        ["classes.not_found"] = Missing,
        ["classes.archived"] = "Класс в архиве. Восстановите его, чтобы изменить название или описание.",
        ["classes.variant_archived"] = "Вариант класса в архиве. Восстановите его, чтобы изменить цвет.",
        ["classes.revision_conflict"] = "Данные уже изменились. Обновите список и повторите изменение.",
        ["classes.order_revision_conflict"] = "Порядок классов изменился. Обновите список и повторите перемещение.",
        ["classes.order_invalid"] = "Порядок должен содержать каждый класс проекта ровно один раз, включая архивные.",
        ["classes.class_unavailable"] = "Класс недоступен.",
        ["classes.variant_unavailable"] = "Вариант недоступен.",
        ["classes.project_variant_unavailable"] = "Вариант проекта недоступен. Выберите активный вариант.",
        ["classes.duplicate_variant_binding"] = "Этот вариант уже добавлен к классу. Восстановите его из архива, если он скрыт.",
        ["classes.color_invalid"] = "Цвет варианта: укажите значение в формате #rrggbb.",
        ["classes.deletion_revision_conflict"] = DeletionConflict,
        ["classes.deletion_blocked"] = DeletionBlocked,
        ["classes.asset_invalid"] = "Недопустимое или повреждённое изображение. Загрузите целый файл PNG, JPEG, WebP или BMP.",
        ["classes.asset_unreadable"] = "Не удалось прочитать файл. Проверьте, что он доступен, и добавьте его снова.",
        ["classes.selection_expired"] = "Выбор файлов устарел. Выберите файлы снова.",
    };

    /// <summary>A line of the blocker list in the deletion dialog.</summary>
    internal static string For(DeletionBlocker blocker) => blocker.Code switch
    {
        "jobs.active" => $"Выполняются задачи проекта: {blocker.Count}.",
        "generation.preset_reference" => Generation.GenerationErrorMessages.PresetReference(blocker),
        "images.annotations" => $"Разметка изображений ссылается на этот объект ({blocker.Count}): {string.Join(", ", blocker.Examples)}"
            + (blocker.Count > blocker.Examples.Count ? " и другие." : "."),
        _ => blocker.Examples.Count > 0
            ? $"Материал используется ({blocker.Count}): {string.Join(", ", blocker.Examples)}."
            : $"Материал используется: {blocker.Count}.",
    };
}
