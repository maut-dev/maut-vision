using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Desktop.Controllers.BaseModels.RequestDto;
using MautVision.Desktop.Controllers.BaseModels.ResponseDto;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Controllers.BaseModels;

public sealed class BaseModelsController(IBaseModelsManager baseModels, ILogger<BaseModelsController> logger)
{
    public async Task<BaseModelResponseDto[]> ListAsync(ListBaseModelsRequestDto request, CancellationToken cancellationToken)
        => (await baseModels.ListAsync(cancellationToken)).Select(BaseModelResponseMapper.Map).ToArray();

    /// <summary>Starts installation and returns at once; progress and the result arrive as baseModel.changed events.</summary>
    public async Task<BaseModelResponseDto> InstallAsync(InstallBaseModelRequestDto request, CancellationToken cancellationToken)
    {
        var model = await RequireAsync(request.ModelId, cancellationToken);
        // Installation belongs to the model and outlives this request; its failures are reported by the model status.
        _ = model.InstallAsync(CancellationToken.None).ContinueWith(
            task => logger.LogError(task.Exception, "Base model {ModelId} installation failed.", model.Id),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return BaseModelResponseMapper.Map(model);
    }

    public async Task<BaseModelResponseDto> CancelInstallationAsync(CancelBaseModelInstallationRequestDto request, CancellationToken cancellationToken)
    {
        var model = await RequireAsync(request.ModelId, cancellationToken);
        await model.StopInstallationAsync();
        return BaseModelResponseMapper.Map(model);
    }

    public async Task<BaseModelResponseDto> DeleteAsync(DeleteBaseModelRequestDto request, CancellationToken cancellationToken)
    {
        var model = await RequireAsync(request.ModelId, cancellationToken);
        await model.DeleteAsync(cancellationToken);
        return BaseModelResponseMapper.Map(model);
    }

    private async Task<IBaseModel> RequireAsync(string modelId, CancellationToken cancellationToken)
        => await baseModels.GetOrNullAsync(modelId, cancellationToken)
            ?? throw new CoreException("base_models.not_found", $"Base model {modelId} is not in the catalogue.");
}
