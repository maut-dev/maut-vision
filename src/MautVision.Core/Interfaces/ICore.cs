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

    /// <summary>Пресеты параметров обучения, общие для всех проектов.</summary>
    ITrainingPresetsManager TrainingPresets { get; }

    /// <summary>Видеокарты для выбора устройства обучения.</summary>
    IHardwareManager Hardware { get; }

    /// <summary>Состояние среды Python worker.</summary>
    IPythonRuntime Python { get; }

    /// <summary>Мониторы и снимок экрана для проверки с экрана.</summary>
    IScreenManager Screen { get; }
}
