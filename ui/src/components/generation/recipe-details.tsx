import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { GenerationRecipeFormDto } from "@/api/projects/generation/request-dto/generation-recipe-form-dto";
import { backgroundsLabel, toPercent } from "@/lib/generation/recipe-form";

/**
 * Детали пресета или снимка запуска: «Размер кадра», «Масштаб объекта», «Объектов на кадр», «Пустые кадры», «Варианты и
 * классы», «Наборы фонов». Названия и доступность источников — по текущему каталогу и наборам (отметки удалённых и архивных).
 */
export function RecipeDetails({ recipe, workspace, sets }: {
    recipe: GenerationRecipeFormDto;
    workspace: CatalogWorkspaceResponseDto | undefined;
    sets: readonly BackgroundSetResponseDto[];
}) {
    const bindings = recipe.classVariantIds.map(id => ({ id, binding: workspace?.classVariants.find(item => item.id === id) }));
    const weighted = recipe.variantWeights.filter(item => item.percentage > 0);
    return (
        <div className="space-y-3 text-sm" data-testid="recipe-details">
            <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-xs">
                <div><dt className="text-muted-foreground">Размер кадра</dt><dd className="tabular-nums">{recipe.width} × {recipe.height} px</dd></div>
                <div><dt className="text-muted-foreground">Масштаб объекта</dt><dd className="tabular-nums">{recipe.scale.toLocaleString("ru-RU")}</dd></div>
                <div><dt className="text-muted-foreground">Объектов на кадр</dt><dd className="tabular-nums">{recipe.objectsPerImage}</dd></div>
                <div><dt className="text-muted-foreground">Пустые кадры</dt><dd className="tabular-nums">{toPercent(recipe.emptyProbability).toLocaleString("ru-RU")}%</dd></div>
            </dl>
            <p className="text-xs text-muted-foreground">Максимальная доля кадра; исходники не увеличиваются.</p>
            <div>
                <h4 className="mb-1 text-xs font-medium">Варианты и классы</h4>
                <ul className="space-y-1 text-xs" data-testid="recipe-variants">
                    {weighted.map(weight => {
                        const variant = workspace?.variants.find(item => item.id === weight.variantId);
                        const classes = bindings.filter(item => item.binding?.variantId === weight.variantId).map(item => {
                            const klass = workspace?.classes.find(entry => entry.id === item.binding?.classId);
                            return `${klass?.name ?? "Класс удалён"}${klass?.archivedAt || item.binding?.archivedAt ? " (архив)" : ""}`;
                        });
                        return (
                            <li className="break-words" key={weight.variantId}>
                                {variant?.name ?? "Вариант удалён"}{variant?.archivedAt ? " (архив)" : ""} — <span className="tabular-nums">{weight.percentage.toLocaleString("ru-RU")}%</span>
                                {classes.length > 0 && ` · ${Array.from(new Set(classes)).join(", ")}`}
                            </li>
                        );
                    })}
                </ul>
                {workspace && bindings.some(item => !item.binding) && (
                    <p className="mt-1 text-xs text-destructive">Часть вариантов класса удалена. Проверьте источники перед новым запуском.</p>
                )}
            </div>
            <div>
                <h4 className="mb-1 text-xs font-medium">Наборы фонов</h4>
                <ul className="space-y-1 text-xs" data-testid="recipe-sets">
                    {recipe.backgroundSetIds.map(id => {
                        const set = sets.find(item => item.id === id);
                        return (
                            <li className="break-words" key={id}>
                                {set ? `${set.name}${set.archivedAt ? " (архив)" : ""} · ${backgroundsLabel(set.itemCount)}` : "Набор удалён или недоступен"}
                            </li>
                        );
                    })}
                </ul>
            </div>
        </div>
    );
}
