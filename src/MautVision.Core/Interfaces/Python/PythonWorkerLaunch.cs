namespace MautVision.Core.Interfaces.Python;

/// <summary>
/// How to launch the Python worker; the host gives the description, Core does not search for Python in PATH. A packaged worker is an executable without
/// arguments (<see cref="Packaged"/>); in development — the interpreter of a pinned environment with <c>-m worker</c> and the source folder
/// in PYTHONPATH (<see cref="Interpreter"/>). Core adds the job folder as the last argument and makes it the process working folder.
/// </summary>
/// <param name="Executable">Absolute path to the exe: the packaged worker or python.exe.</param>
/// <param name="Arguments">Arguments before the job folder.</param>
/// <param name="SourceDirectory">Worker source folder (PYTHONPATH) for the interpreter; null for the packaged worker.</param>
public sealed record PythonWorkerLaunch(string Executable, IReadOnlyList<string> Arguments, string? SourceDirectory)
{
    /// <summary>Packaged onedir worker: <c>python/maut-training-worker/maut-training-worker.exe</c> in the application directory.</summary>
    public static PythonWorkerLaunch Packaged(string executable) => new(executable, [], null);

    /// <summary>Interpreter and worker sources: <c>python.exe -m worker &lt;job folder&gt;</c>, PYTHONPATH — sourceDirectory.</summary>
    public static PythonWorkerLaunch Interpreter(string python, string sourceDirectory) => new(python, ["-m", "worker"], sourceDirectory);

    /// <summary>Mode for display and the log: packaged or interpreter.</summary>
    public string Mode => SourceDirectory is null ? "packaged" : "interpreter";

    public bool Equals(PythonWorkerLaunch? other) => other is not null && Executable == other.Executable && SourceDirectory == other.SourceDirectory
        && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() => HashCode.Combine(Executable, SourceDirectory, Arguments.Count);
}
