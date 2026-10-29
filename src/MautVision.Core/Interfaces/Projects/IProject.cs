using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Projects.Checks;

namespace MautVision.Core.Interfaces.Projects;

/// <summary>
/// Root object of a project. The manager holds a single instance per project; changes
/// of settings and archiving modify it and are reported by the event IProjectsManager.ProjectChanged.
/// Project materials are child managers; they work after IProjectsManager.OpenAsync.
/// </summary>
public interface IProject
{
    string Id { get; }
    string Name { get; }
    string Description { get; }

    /// <summary>Catalog base model from which the project training starts.</summary>
    string BaseModelId { get; }

    /// <summary>Model input size; sets the size of backgrounds, generation frames and datasets.</summary>
    InputSize InputSize { get; }

    /// <summary>Increases with every saved change; used to detect conflicts.</summary>
    long Revision { get; }

    DateTimeOffset CreatedAt { get; }
    DateTimeOffset UpdatedAt { get; }

    /// <summary>Time of archiving; null for an active project. An archived project cannot be changed.</summary>
    DateTimeOffset? ArchivedAt { get; }

    /// <summary>Material files and their thumbnails for display in the UI.</summary>
    IResourcesManager Resources { get; }

    /// <summary>Shared display variants of objects.</summary>
    IVariantsManager Variants { get; }

    /// <summary>Classes, their variants and source images.</summary>
    IClassesManager Classes { get; }

    /// <summary>Image groups, images and their annotation.</summary>
    IImagesManager Images { get; }

    /// <summary>Background sets and backgrounds of the model input size.</summary>
    IBackgroundsManager Backgrounds { get; }

    /// <summary>Generation presets and runs: class objects on backgrounds with boxes.</summary>
    IGenerationManager Generation { get; }

    /// <summary>Immutable versions of ready frames and annotation (YOLO), their build, check and export.</summary>
    IDatasetsManager Datasets { get; }

    /// <summary>Training runs on dataset versions and model versions.</summary>
    ITrainingManager Training { get; }

    /// <summary>Evaluations of model versions on datasets and live checks on an image, video and the screen.</summary>
    IChecksManager Checks { get; }

    /// <summary>
    /// Saves the settings. expectedRevision — the Revision the user saw;
    /// on a mismatch CoreException <c>projects.revision_conflict</c> is thrown.
    /// A new base model must be installed; the previous one is not checked.
    /// </summary>
    Task UpdateAsync(ProjectSettings settings, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Moves the project to the archive or back. A repeated call with the same value changes nothing.</summary>
    Task SetArchivedAsync(bool archived, CancellationToken cancellationToken = default);
}
