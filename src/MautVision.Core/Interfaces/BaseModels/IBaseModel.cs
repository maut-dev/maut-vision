namespace MautVision.Core.Interfaces.BaseModels;

/// <summary>
/// A catalog model. The manager holds a single instance per model; it changes
/// state on install, check and removal; changes are reported by IBaseModelsManager.ModelChanged.
/// </summary>
public interface IBaseModel
{
    string Id { get; }
    string Family { get; }
    string Variant { get; }
    long ByteSize { get; }

    /// <summary>Model input stride: the project input width and height are multiples of it.</summary>
    int Stride { get; }

    BaseModelStatus Status { get; }

    /// <summary>Installation progress from 0 to 100 percent; always 0 when installation is not running.</summary>
    double Progress { get; }

    /// <summary>
    /// Reason code for Status = Failed, for example <c>base_models.download_failed</c>;
    /// null in other states.
    /// </summary>
    string? ErrorCode { get; }

    /// <summary>
    /// Starts the installation and completes when it finishes, is cancelled or fails.
    /// During installation Status = Installing and Progress goes from 0 to 100.
    /// On success Status = Installed, on cancellation NotInstalled, on error Failed
    /// with ErrorCode. After completion Progress is always 0. A repeated call does not start
    /// a parallel installation of the same model but awaits the current one; for an installed model
    /// it completes immediately. A download error is not thrown: Status reflects the result.
    /// Cancelling cancellationToken stops the installation and throws OperationCanceledException.
    /// </summary>
    Task InstallAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the installation to stop and waits until its cleanup completes.
    /// After stopping Status = NotInstalled, Progress = 0.
    /// Changes nothing if no installation is running.
    /// </summary>
    Task StopInstallationAsync();

    /// <summary>
    /// Deletes the installed file; after a successful deletion Status = NotInstalled,
    /// Progress = 0. A missing file counts as already deleted.
    /// During installation throws CoreException <c>base_models.installation_active</c>.
    /// </summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the file size and SHA-256 against the internal metadata.
    /// Returns false for a missing or corrupted file.
    /// Updates Status: Installed for a valid file, NotInstalled for
    /// a missing one, Failed for a corrupted one. Progress stays 0.
    /// During installation throws CoreException <c>base_models.installation_active</c>;
    /// read errors and cancellation are reported as exceptions, not as a false result.
    /// </summary>
    Task<bool> VerifyIntegrityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the installed model into a writable stream of the consumer.
    /// Does not reveal the source path and does not close the destination stream.
    /// For a model that is not installed throws CoreException <c>base_models.not_installed</c>.
    /// </summary>
    Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default);
}
