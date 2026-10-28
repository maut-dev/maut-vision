using MautVision.Core.Interfaces.BaseModels;
using MautVision.Desktop.Controllers.BaseModels.ResponseDto;
using MautVision.Desktop.Transport;

namespace MautVision.Desktop.Controllers.BaseModels;

internal static class BaseModelResponseMapper
{
    internal static BaseModelResponseDto Map(IBaseModel model)
    {
        var code = model.ErrorCode;
        var error = code is null ? null : new BaseModelErrorDto(code, BridgeErrors.MessageFor(code, BaseModelErrorMessages.All));
        return new(model.Id, model.Family, model.Variant, model.ByteSize, model.Stride, Map(model.Status), model.Progress, error);
    }

    private static string Map(BaseModelStatus status) => status switch
    {
        BaseModelStatus.NotInstalled => "not_installed",
        BaseModelStatus.Installing => "installing",
        BaseModelStatus.Installed => "installed",
        BaseModelStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown base model status.")
    };
}
