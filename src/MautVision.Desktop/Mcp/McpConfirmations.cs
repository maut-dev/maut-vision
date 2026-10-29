using System.Collections.Concurrent;

namespace MautVision.Desktop.Mcp;

/// <summary>
/// A question to the user before a tool acts: Action — what happens, a key of the UI catalog mcp (confirm.actions.&lt;action&gt;); Values — its
/// placeholders (a project name, a number of jobs); Client — the name the AI client gave; ExpiresAt — when the call gives up.
/// </summary>
internal sealed record McpConfirmation(Guid Id, string Client, string Tool, string Action, IReadOnlyDictionary<string, string> Values, DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Confirmations in the application window: the tool waits for the user's "Allow" or "Deny" for at most two minutes. The page shows the
/// question (the window is brought to the front, also from the tray); denial is mcp.confirmation_denied, no answer — mcp.confirmation_timeout.
/// </summary>
internal sealed class McpConfirmations(Action requestAttention)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, (McpConfirmation Request, TaskCompletionSource<bool> Answer)> pending = new();

    /// <summary>Smoke seam: a shorter wait for the timeout check.</summary>
    internal TimeSpan? SmokeTimeout { get; set; }

    /// <summary>A question appeared; may come from any thread.</summary>
    internal event Action<McpConfirmation>? Requested;

    /// <summary>A question was answered, timed out or abandoned: the page closes its dialog.</summary>
    internal event Action<Guid>? Closed;

    internal IReadOnlyList<McpConfirmation> Pending => pending.Values.Select(item => item.Request).OrderBy(item => item.RequestedAt).ToArray();

    /// <summary>Waits for the answer; throws mcp.confirmation_denied or mcp.confirmation_timeout.</summary>
    internal async Task AskAsync(string client, string tool, string action, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var wait = SmokeTimeout ?? Timeout;
        var now = DateTimeOffset.UtcNow;
        var request = new McpConfirmation(Guid.NewGuid(), client, tool, action, values, now, now + wait);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[request.Id] = (request, answer);
        try
        {
            requestAttention();
            Requested?.Invoke(request);
            bool allowed;
            try
            {
                allowed = await answer.Task.WaitAsync(wait, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new McpToolException("mcp.confirmation_timeout", "The user did not answer within two minutes.");
            }
            if (!allowed) throw new McpToolException("mcp.confirmation_denied", "The user denied the action.");
        }
        finally
        {
            pending.TryRemove(request.Id, out _);
            Closed?.Invoke(request.Id);
        }
    }

    /// <summary>The user's answer; an unknown or finished question is ignored (false).</summary>
    internal bool Answer(Guid id, bool allow)
    {
        if (!pending.TryGetValue(id, out var item)) return false;
        return item.Answer.TrySetResult(allow);
    }

    /// <summary>The server stops: open questions are denied.</summary>
    internal void DenyAll()
    {
        foreach (var item in pending.Values) item.Answer.TrySetResult(false);
    }
}
