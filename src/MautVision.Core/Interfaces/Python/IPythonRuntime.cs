namespace MautVision.Core.Interfaces.Python;

/// <summary>
/// State of the Python worker runtime: Configured — the host passed a launch description; Available — its files are in place; Mode — packaged |
/// interpreter (null without configuration); Code — <c>runtime_not_configured</c> or <c>runtime_missing</c> while the runtime is unavailable.
/// </summary>
public sealed record PythonRuntimeStatus(bool Configured, bool Available, string? Mode, string? Code);

/// <summary>Python worker runtime. The check only shows the state: a training run checks the runtime itself.</summary>
public interface IPythonRuntime
{
    PythonRuntimeStatus GetStatus();

    /// <summary>IDs of running worker processes with their child processes (DataLoader); empty if no worker is running.</summary>
    IReadOnlyList<int> GetWorkerProcessIds();
}
