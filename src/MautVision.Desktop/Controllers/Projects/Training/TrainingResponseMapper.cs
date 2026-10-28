using System.Text.Json;
using MautVision.Core.Interfaces.Hardware;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Training;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Projects.Datasets;
using MautVision.Desktop.Controllers.Projects.Datasets.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Training;

internal static class TrainingResponseMapper
{
    internal static TrainingPresetResponseDto Map(TrainingPreset preset)
        => new(preset.Id, preset.Name, new Dictionary<string, JsonElement>(preset.Parameters), preset.CreatedAt, preset.UpdatedAt);

    internal static GpuInventoryResponseDto Map(GpuInventory inventory)
        => new(inventory.DriverAvailable, inventory.Gpus.Select(gpu => new GpuResponseDto(gpu.Index, gpu.Name, gpu.MemoryTotalMb, gpu.DriverVersion)).ToArray(),
            inventory.CheckedAt);

    internal static TrainingRunResponseDto Map(TrainingRun run, Func<string, string> messageFor) => new(
        run.Id,
        JobResponseMapper.Map(run.Job, messageFor),
        StateKey(run.State),
        Map(run.Dataset),
        run.Architecture,
        new InputSizeDto(run.Input.Width, run.Input.Height),
        run.ImageSize,
        new Dictionary<string, JsonElement>(run.Parameters),
        run.PresetName,
        run.EffectiveParams,
        new TrainingRunProgressDto(run.Progress.Stage, run.Progress.Epoch, run.Progress.Epochs, run.Progress.Batch, run.Progress.Batches,
            new Dictionary<string, double>(run.Progress.Metrics)),
        run.EpochCount,
        run.LogCount,
        run.Environment,
        run.Error is { } error ? new TrainingRunErrorDto(error.Code, TrainingErrorMessages.RunMessage(error.Code, error.Message, messageFor)) : null,
        run.ResultVersionId,
        run.Revision);

    internal static TrainingDatasetDto Map(TrainingDatasetReference dataset) => new(dataset.VersionId, dataset.Name, dataset.ManifestHash,
        dataset.Classes.Select(item => new DatasetClassEntryDto(item.Index, item.ClassId, item.Name)).ToArray(), DatasetsResponseMapper.Map(dataset.Counts));

    internal static TrainingEpochPageResponseDto Map(TrainingEpochPage page) => new(page.Items.Select(epoch => new TrainingEpochDto(epoch.Epoch,
        new Dictionary<string, double>(epoch.Losses), new Dictionary<string, double>(epoch.Metrics), epoch.Lr, epoch.Fitness)).ToArray(), page.Total, page.NextAfter);

    internal static TrainingLogPageResponseDto Map(TrainingLogPage page) => new(page.Items.Select(entry => new TrainingLogEntryDto(entry.Sequence, entry.At,
        entry.Kind switch
        {
            TrainingLogKind.Stage => "stage",
            TrainingLogKind.Warning => "warning",
            TrainingLogKind.System => "system",
            _ => "log",
        }, entry.Message)).ToArray(), page.Total, page.NextAfter);

    internal static ModelVersionResponseDto Map(string projectId, ModelVersion version, ResourceHandles resources) => new(
        version.Id,
        version.Number,
        version.RunId,
        Map(version.Dataset),
        version.Architecture,
        new InputSizeDto(version.Input.Width, version.Input.Height),
        version.ImageSize,
        new Dictionary<string, JsonElement>(version.Parameters),
        version.EffectiveParams,
        version.EpochsCompleted,
        version.StoppedEarly,
        version.BestEpoch,
        version.Classes.Select(item => new ModelClassDto(item.Index, item.ClassId, item.Name, item.SourceClassIds.ToArray())).ToArray(),
        new Dictionary<string, double>(version.Metrics),
        version.Files.Select(file => new ModelVersionFileDto(FileKindKey(file.Kind), file.Name, file.ByteSize, file.Sha256,
            file.Kind == ModelFileKind.Plot
                ? resources.Issue(projectId, new ResourceReference(ModelVersion.ResourceKind, file.ResourceId), ResourceVariant.Original)
                : null)).ToArray(),
        version.Notes.ToArray(),
        version.Environment,
        version.IsWorking,
        version.Revision,
        version.CreatedAt);

    internal static TrainingChangedEventDto Map(TrainingChangedEventArgs args) => new(args.ProjectId,
        args.Runs.Select(change => new TrainingEntityChangeDto(change.Id, change.Revision)).ToArray(),
        args.Versions.Select(change => new TrainingEntityChangeDto(change.Id, change.Revision)).ToArray());

    internal static string StateKey(TrainingRunState state) => state switch
    {
        TrainingRunState.Queued => "queued",
        TrainingRunState.Preparing => "preparing",
        TrainingRunState.Training => "training",
        TrainingRunState.Cancelling => "cancelling",
        TrainingRunState.Succeeded => "succeeded",
        TrainingRunState.Failed => "failed",
        TrainingRunState.Cancelled => "cancelled",
        _ => "interrupted",
    };

    private static string FileKindKey(ModelFileKind kind) => kind switch
    {
        ModelFileKind.WeightsPt => "weights_pt",
        ModelFileKind.Onnx => "onnx",
        ModelFileKind.Plot => "plot",
        _ => "log",
    };
}
