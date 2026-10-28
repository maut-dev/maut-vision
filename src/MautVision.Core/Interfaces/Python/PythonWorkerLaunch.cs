namespace MautVision.Core.Interfaces.Python;

/// <summary>
/// Как запускать Python worker; описание даёт host, Core не ищет Python в PATH. Собранный worker — исполняемый файл без
/// аргументов (<see cref="Packaged"/>); в разработке — интерпретатор закреплённого окружения с <c>-m worker</c> и папкой исходников
/// в PYTHONPATH (<see cref="Interpreter"/>). Папку задачи Core добавляет последним аргументом и делает рабочей папкой процесса.
/// </summary>
/// <param name="Executable">Абсолютный путь к exe: собранный worker или python.exe.</param>
/// <param name="Arguments">Аргументы перед папкой задачи.</param>
/// <param name="SourceDirectory">Папка исходников worker (PYTHONPATH) для интерпретатора; null у собранного worker.</param>
public sealed record PythonWorkerLaunch(string Executable, IReadOnlyList<string> Arguments, string? SourceDirectory)
{
    /// <summary>Собранный onedir worker: <c>python/maut-training-worker/maut-training-worker.exe</c> в директории приложения.</summary>
    public static PythonWorkerLaunch Packaged(string executable) => new(executable, [], null);

    /// <summary>Интерпретатор и исходники worker: <c>python.exe -m worker &lt;папка задачи&gt;</c>, PYTHONPATH — sourceDirectory.</summary>
    public static PythonWorkerLaunch Interpreter(string python, string sourceDirectory) => new(python, ["-m", "worker"], sourceDirectory);

    /// <summary>Режим для показа и журнала: packaged или interpreter.</summary>
    public string Mode => SourceDirectory is null ? "packaged" : "interpreter";

    public bool Equals(PythonWorkerLaunch? other) => other is not null && Executable == other.Executable && SourceDirectory == other.SourceDirectory
        && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() => HashCode.Combine(Executable, SourceDirectory, Arguments.Count);
}
