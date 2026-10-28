using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Datasets;

/// <summary>Вход сборки: план с его hash и копия спецификации; готовит <see cref="IDatasetsManager.PrepareBuildAsync"/>. ID версии — ID задачи.</summary>
public sealed record DatasetBuildInput(Guid PlanId, string PlanHash, DatasetSpecification Specification, int Width, int Height);

/// <summary>
/// Сборка версии (CPU, возобновляемая). Подготовка: источники читаются заново и план пересчитывается — другой hash даёт
/// <c>datasets.plan_stale</c>; строки версии (building) и всех образцов пишутся одной транзакцией. Файлы: окна образцов
/// параллельно — проверка SHA-256 исходного файла, жёсткая ссылка (JPEG и PNG без EXIF-поворота) или повёрнутый PNG, метка
/// YOLO; checkpoint после каждого окна, после перезапуска сборка продолжается с него. Публикация: проверка одинаковых пикселей
/// в разных частях (<c>datasets.split_collision</c>), manifest.json и data.yaml, версия ready и verified одной транзакцией.
/// Отмена и ошибка не оставляют ни строк, ни файлов. Итог — <see cref="DatasetBuildResult"/>.
/// </summary>
[JobKind("datasets.build")]
public interface IDatasetBuildJob : IJobDefinition<DatasetBuildInput>
{
}

/// <summary>Вход проверки целостности: версия и её название на момент постановки.</summary>
public sealed record DatasetVerifyInput(Guid VersionId, string VersionName);

/// <summary>
/// Проверка целостности (CPU, не возобновляемая): все файлы версии читаются параллельно и сверяются с SHA-256; все проблемы
/// перечисляются. Версия становится verified или corrupt. Итог — <see cref="DatasetVerifyResult"/>.
/// </summary>
[JobKind("datasets.verify")]
public interface IDatasetVerifyJob : IJobDefinition<DatasetVerifyInput>
{
}

/// <summary>Вход экспорта: версия, её название и абсолютный путь ZIP, выбранный в «Сохранить как».</summary>
public sealed record DatasetExportInput(Guid VersionId, string VersionName, string DestinationPath);

/// <summary>
/// Экспорт ZIP (CPU, не возобновляемый): data.yaml, manifest.json, dataset-metadata.json, images/ (без сжатия), labels/,
/// negatives/&lt;split&gt;/&lt;id&gt;.json — во временный файл рядом с целевым, затем замена целевого (перезапись разрешена).
/// Отмена и ошибка удаляют временный файл, целевой не меняется. Итог — <see cref="DatasetExportResult"/>.
/// </summary>
[JobKind("datasets.export")]
public interface IDatasetExportJob : IJobDefinition<DatasetExportInput>
{
}
