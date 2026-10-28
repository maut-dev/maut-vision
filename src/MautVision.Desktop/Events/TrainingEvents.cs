using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Training;
using MautVision.Desktop.Controllers.Projects.Training;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;

namespace MautVision.Desktop.Events;

/// <summary>
/// Forwards training changes as training.changed (runs and versions with new revisions; Core coalesces live progress) and preset
/// changes as trainingPresets.changed. State and progress of a run also come as job.changed.
/// </summary>
internal sealed class TrainingEvents : IDisposable
{
    private readonly IProjectsManager projects;
    private readonly ITrainingPresetsManager presets;
    private readonly Action<string, object> publish;
    private int disposed;

    internal TrainingEvents(IProjectsManager projects, ITrainingPresetsManager presets, Action<string, object> publish)
    {
        this.projects = projects;
        this.presets = presets;
        this.publish = publish;
        projects.TrainingChanged += OnTrainingChanged;
        presets.PresetsChanged += OnPresetsChanged;
    }

    private void OnTrainingChanged(object? sender, TrainingChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("training.changed", TrainingResponseMapper.Map(args));
    }

    private void OnPresetsChanged(object? sender, TrainingPresetsChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("trainingPresets.changed", new TrainingPresetsChangedEventDto(args.PresetIds.ToArray()));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        projects.TrainingChanged -= OnTrainingChanged;
        presets.PresetsChanged -= OnPresetsChanged;
    }
}
