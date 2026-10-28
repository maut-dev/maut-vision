namespace MautVision.Core.Interfaces.Jobs;

/// <summary>
/// Файлы, выбранные пользователем, для одного варианта класса. Files — абсолютные пути; они хранятся во входных данных
/// задачи в project.db и наружу (в снимок задачи и UI) не выходят.
/// </summary>
public sealed record ImportClassAssetsInput(Guid ClassVariantId, IReadOnlyList<string> Files);

/// <summary>
/// Импорт исходных изображений варианта класса (CPU). Принимаются PNG, JPEG, WebP и BMP; байты копируются без изменений.
/// Одинаковый файл (SHA-256) в той же привязке пропускается. Итог — <see cref="ImportClassAssetsResult"/> в
/// <see cref="JobInfo.Result"/>, в том числе после отмены. Привязка в архиве или удалена — задача завершается
/// с <c>classes.variant_unavailable</c>.
/// </summary>
[JobKind("classes.import_assets")]
public interface IImportClassAssetsJob : IJobDefinition<ImportClassAssetsInput>
{
}

/// <summary>Отклонённый файл: только имя без папки и код причины (<c>classes.asset_invalid</c>, <c>classes.asset_unreadable</c>).</summary>
public sealed record ImportRejectedFile(string Name, string Reason);

/// <summary>
/// Итог импорта. Rejected — число отклонённых; RejectedFiles — первые <see cref="MaxRejectedFiles"/> из них.
/// </summary>
public sealed record ImportClassAssetsResult(int Total, int Added, int Duplicates, int Rejected, IReadOnlyList<ImportRejectedFile> RejectedFiles)
{
    public const int MaxRejectedFiles = 50;
}
