namespace MautVision.Core.Interfaces.BaseModels;

/// <summary>
/// Модель каталога. Экземпляр единственный для каждой модели в менеджере и меняет
/// состояние при установке, проверке и удалении; изменения сообщает IBaseModelsManager.ModelChanged.
/// </summary>
public interface IBaseModel
{
    string Id { get; }
    string Family { get; }
    string Variant { get; }
    long ByteSize { get; }

    /// <summary>Шаг входа модели: ширина и высота входа проекта кратны ему.</summary>
    int Stride { get; }

    BaseModelStatus Status { get; }

    /// <summary>Installation progress from 0 to 100 percent; always 0 when installation is not running.</summary>
    double Progress { get; }

    /// <summary>
    /// Код причины для Status = Failed, например <c>base_models.download_failed</c>;
    /// в остальных состояниях null.
    /// </summary>
    string? ErrorCode { get; }

    /// <summary>
    /// Запускает установку и завершается после её окончания, отмены или ошибки.
    /// Во время установки Status = Installing, Progress обновляется от 0 до 100.
    /// При успехе Status = Installed, при отмене — NotInstalled, при ошибке — Failed
    /// с ErrorCode. После завершения Progress всегда равен 0. Повторный вызов не запускает
    /// параллельную установку той же модели, а ожидает текущую; для установленной модели
    /// завершается сразу. Ошибка загрузки не выбрасывается: результат отражает Status.
    /// Отмена cancellationToken останавливает установку и выбрасывает OperationCanceledException.
    /// </summary>
    Task InstallAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Запрашивает остановку установки и дожидается завершения её очистки.
    /// После остановки Status = NotInstalled, Progress = 0.
    /// Если установка не выполняется, ничего не меняет.
    /// </summary>
    Task StopInstallationAsync();

    /// <summary>
    /// Удаляет установленный файл; после успешного удаления Status = NotInstalled,
    /// Progress = 0. Отсутствующий файл считается уже удалённым.
    /// Во время установки выбрасывает CoreException <c>base_models.installation_active</c>.
    /// </summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет размер и SHA-256 файла по внутренним метаданным.
    /// Возвращает false для отсутствующего или повреждённого файла.
    /// Обновляет Status: Installed для корректного файла, NotInstalled для
    /// отсутствующего, Failed для повреждённого. Progress остаётся 0.
    /// Во время установки выбрасывает CoreException <c>base_models.installation_active</c>;
    /// ошибки чтения и отмена передаются исключениями, а не результатом false.
    /// </summary>
    Task<bool> VerifyIntegrityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Копирует установленную модель в доступный для записи поток потребителя.
    /// Не раскрывает исходный путь и не закрывает поток назначения.
    /// Для неустановленной модели выбрасывает CoreException <c>base_models.not_installed</c>.
    /// </summary>
    Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default);
}
