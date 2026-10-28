using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Projects.Checks;

namespace MautVision.Core.Interfaces.Projects;

/// <summary>
/// Корневой объект проекта. Экземпляр единственный для проекта в менеджере; изменения
/// настроек и архивации меняют его и сообщаются событием IProjectsManager.ProjectChanged.
/// Материалы проекта — дочерние менеджеры; они работают после IProjectsManager.OpenAsync.
/// </summary>
public interface IProject
{
    string Id { get; }
    string Name { get; }
    string Description { get; }

    /// <summary>Базовая модель каталога, от которой начинается обучение проекта.</summary>
    string BaseModelId { get; }

    /// <summary>Размер входа модели; задаёт размер фонов, кадров генерации и датасетов.</summary>
    InputSize InputSize { get; }

    /// <summary>Увеличивается при каждом сохранённом изменении; используется для обнаружения конфликтов.</summary>
    long Revision { get; }

    DateTimeOffset CreatedAt { get; }
    DateTimeOffset UpdatedAt { get; }

    /// <summary>Время переноса в архив; null для активного проекта. Архивный проект нельзя изменить.</summary>
    DateTimeOffset? ArchivedAt { get; }

    /// <summary>Файлы материалов и их миниатюры для показа в UI.</summary>
    IResourcesManager Resources { get; }

    /// <summary>Общие варианты отображения объектов.</summary>
    IVariantsManager Variants { get; }

    /// <summary>Классы, их варианты и исходные изображения.</summary>
    IClassesManager Classes { get; }

    /// <summary>Группы изображений, изображения и их разметка.</summary>
    IImagesManager Images { get; }

    /// <summary>Наборы фонов и фоны размера входа модели.</summary>
    IBackgroundsManager Backgrounds { get; }

    /// <summary>Пресеты генерации и запуски: объекты классов на фонах с рамками.</summary>
    IGenerationManager Generation { get; }

    /// <summary>Неизменяемые версии готовых кадров и разметки (YOLO), их сборка, проверка и экспорт.</summary>
    IDatasetsManager Datasets { get; }

    /// <summary>Запуски обучения на версиях датасетов и версии моделей.</summary>
    ITrainingManager Training { get; }

    /// <summary>Оценки версий моделей на датасетах и живые проверки по изображению, видео и экрану.</summary>
    IChecksManager Checks { get; }

    /// <summary>
    /// Сохраняет настройки. expectedRevision — Revision, которую видел пользователь;
    /// при расхождении выбрасывается CoreException <c>projects.revision_conflict</c>.
    /// Новая базовая модель должна быть установлена; прежняя — не проверяется.
    /// </summary>
    Task UpdateAsync(ProjectSettings settings, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Переносит проект в архив или возвращает из него. Повторный вызов с тем же значением ничего не меняет.</summary>
    Task SetArchivedAsync(bool archived, CancellationToken cancellationToken = default);
}
