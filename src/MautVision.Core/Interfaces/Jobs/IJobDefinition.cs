namespace MautVision.Core.Interfaces.Jobs;

/// <summary>
/// Контракт вида задачи. Интерфейс-наследник помечается <see cref="JobKindAttribute"/>: ключ вида
/// сохраняется в project.db, поэтому переименование интерфейса не теряет сохранённые задачи.
/// </summary>
public interface IJobDefinition<TInput>
{
}
