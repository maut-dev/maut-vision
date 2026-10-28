namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>
/// Общий для проекта вариант отображения объекта. Название уникально без учёта регистра среди вариантов проекта,
/// включая архивные. Revision растёт с каждым сохранённым изменением.
/// </summary>
public sealed record ProjectVariant(
    Guid Id,
    string Name,
    string Description,
    long SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision);

/// <summary>
/// Класс — объект распознавания. SortOrder задаёт порядок классов: при сборке датасета выбранные классы получают
/// индексы от 0 без пропусков в этом порядке. Порядок меняется только перестановкой с ревизией порядка.
/// </summary>
public sealed record CatalogClass(
    Guid Id,
    string Name,
    string Description,
    long SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision);

/// <summary>
/// Вариант класса — привязка варианта проекта к классу; название и описание берутся у варианта проекта.
/// Color — сохранённый цвет <c>#rrggbb</c> или null (цвет по умолчанию, <see cref="DisplayColor"/>).
/// Available — ни привязка, ни её класс, ни вариант проекта не в архиве. Счётчики изображений хранятся в привязке.
/// </summary>
public sealed record ClassVariant(
    Guid Id,
    Guid ClassId,
    Guid VariantId,
    string? Color,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision,
    bool Available,
    long AssetCount,
    long OpaqueAssetCount,
    int MaxAssetWidth,
    int MaxAssetHeight)
{
    public string DisplayColor => Color ?? VariantColors.Default(Id);
}

public enum ClassAssetFormat
{
    Png,
    Jpeg,
    Webp,
    Bmp,
}

/// <summary>
/// Исходное изображение варианта класса. Байты хранятся без изменений в files/class-assets/&lt;id&gt;.&lt;ext&gt;.
/// Width и Height — размер с учётом EXIF-ориентации; HasAlpha — в файле есть альфа-канал.
/// </summary>
public sealed record ClassAsset(
    Guid Id,
    Guid ClassVariantId,
    ClassAssetFormat Format,
    int Width,
    int Height,
    bool HasAlpha,
    long ByteSize,
    string Sha256,
    DateTimeOffset CreatedAt,
    string OperationId)
{
    /// <summary>Вид материала в канале ресурсов: <c>new ResourceReference(ResourceKind, asset.Id)</c>.</summary>
    public const string ResourceKind = "class-assets";
}

/// <summary>
/// Лёгкий снимок каталога для экрана: варианты и классы в порядке SortOrder, привязки со счётчиками, без изображений.
/// CatalogRevision растёт при любом изменении каталога проекта; снимок с меньшим значением устарел.
/// </summary>
public sealed record CatalogWorkspace(
    IReadOnlyList<ProjectVariant> Variants,
    IReadOnlyList<CatalogClass> Classes,
    IReadOnlyList<ClassVariant> ClassVariants,
    long ClassOrderRevision,
    long CatalogRevision);

/// <summary>Страница изображений привязки по порядку добавления; NextCursor — null на последней странице.</summary>
public sealed record ClassAssetPage(IReadOnlyList<ClassAsset> Items, string? NextCursor);

public sealed record ClassOrder(IReadOnlyList<CatalogClass> Classes, long OrderRevision);

public enum CatalogTargetKind
{
    Variant,
    Class,
    ClassVariant,
    Asset,
}

/// <summary>
/// Причина, по которой удаление сейчас недоступно: Code — стабильный код (например, <c>jobs.active</c>),
/// Count — число мешающих объектов, Examples — до нескольких имён для показа.
/// </summary>
public sealed record DeletionBlocker(string Code, int Count, IReadOnlyList<string> Examples);

/// <summary>
/// Состав удаления. Revision описывает цель и все удаляемые вместе с ней строки; удаление сверяет её в транзакции.
/// TargetName — название варианта, класса или варианта проекта привязки; для изображения пусто.
/// </summary>
public sealed record DeletionReview(
    CatalogTargetKind TargetKind,
    Guid TargetId,
    string TargetName,
    long Classes,
    long ClassVariants,
    long Assets,
    IReadOnlyList<DeletionBlocker> Blockers,
    string Revision);

/// <summary>
/// Строки уже удалены. CleanupPending — файлы ещё удаляются в фоне (их много или часть занята); очистка продолжится
/// при следующем удалении и при открытии проекта.
/// </summary>
public sealed record DeletionResult(long Classes, long ClassVariants, long Assets, bool CleanupPending);
