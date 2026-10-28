using MautVision.Core.Interfaces.BaseModels;
using MautVision.Core.Interfaces.Hardware;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Python;
using MautVision.Core.Interfaces.Screen;
using MautVision.Core.Interfaces.Training;

namespace MautVision.Core.Interfaces;

public interface ICore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    IProjectsManager Projects { get; }
    IBaseModelsManager BaseModels { get; }
    IJobsManager Jobs { get; }
    IMediaManager Media { get; }

    /// <summary>Training parameter presets shared by all projects.</summary>
    ITrainingPresetsManager TrainingPresets { get; }

    /// <summary>GPUs for choosing the training device.</summary>
    IHardwareManager Hardware { get; }

    /// <summary>State of the Python worker runtime.</summary>
    IPythonRuntime Python { get; }

    /// <summary>Monitors and a screenshot for the screen check.</summary>
    IScreenManager Screen { get; }
}
