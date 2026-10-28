using System.Buffers.Text;
using System.Security.Cryptography;
using MautVision.Desktop.Controllers.System.RequestDto;
using MautVision.Desktop.Controllers.System.ResponseDto;

namespace MautVision.Desktop.Controllers.System;

/// <summary>
/// Closing the main window does not fire beforeunload in the page, so the host asks the UI first: it publishes
/// <c>app.closeRequested</c> and waits for <c>system.closeDecision</c>. With unsaved changes the UI answers allow: false and
/// shows its unsaved changes dialog; after «Уйти без сохранения» or a successful save it asks the host to close
/// (<c>system.close</c>). An answer that does not come within <see cref="AnswerTimeout"/> (renderer crashed or hung) allows
/// the close, so shutdown never waits on the page.
/// </summary>
/// <param name="publish">Publishes an event to the page.</param>
/// <param name="close">Closes the window without asking again.</param>
internal sealed class CloseGuard(Action<string, object> publish, Action close)
{
    internal static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(1);

    private readonly Lock gate = new();
    private (string RequestId, TaskCompletionSource<bool> Answer)? pending;

    /// <summary>true — the window may close: the page allowed it, or did not answer in time.</summary>
    internal async Task<bool> AskAsync(TimeSpan timeout)
    {
        var requestId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12));
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) pending = (requestId, answer);
        try
        {
            publish("app.closeRequested", new CloseRequestedEventDto(requestId));
            return await answer.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return true;
        }
        finally
        {
            lock (gate)
            {
                if (pending?.RequestId == requestId) pending = null;
            }
        }
    }

    /// <summary>The page's answer to the latest <c>app.closeRequested</c>; an answer to an older question is ignored.</summary>
    public Task<CloseResponseDto> DecideAsync(CloseDecisionRequestDto request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (pending is not { } current || current.RequestId != request.RequestId) return Task.FromResult(new CloseResponseDto(false));
            current.Answer.TrySetResult(request.Allow);
            return Task.FromResult(new CloseResponseDto(true));
        }
    }

    /// <summary>The page resolved its unsaved changes and asks the window to close.</summary>
    public Task<CloseResponseDto> CloseAsync(CloseRequestDto request, CancellationToken cancellationToken)
    {
        close();
        return Task.FromResult(new CloseResponseDto(true));
    }
}
