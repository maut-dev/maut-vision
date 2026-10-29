import { useTranslation } from "react-i18next";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { GenerationRecipeFormDto } from "@/api/projects/generation/request-dto/generation-recipe-form-dto";
import { backgroundsLabel, toPercent } from "@/lib/generation/recipe-form";
import { formatNumber } from "@/lib/format";

/**
 * Details of a preset or a run snapshot: "Frame size", "Object scale", "Objects per frame", "Empty frames", "Variants and classes",
 * "Background sets". Names and availability of sources follow the current catalog and sets (marks of deleted and archived ones).
 */
export function RecipeDetails({ recipe, workspace, sets }: {
    recipe: GenerationRecipeFormDto;
    workspace: CatalogWorkspaceResponseDto | undefined;
    sets: readonly BackgroundSetResponseDto[];
}) {
    const { t } = useTranslation("generation");
    const archivedName = (name: string, archived: boolean) => archived ? t("details.archived", { name }) : name;
    const bindings = recipe.classVariantIds.map(id => ({ id, binding: workspace?.classVariants.find(item => item.id === id) }));
    const weighted = recipe.variantWeights.filter(item => item.percentage > 0);
    return (
        <div className="space-y-3 text-sm" data-testid="recipe-details">
            <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-xs">
                <div><dt className="text-muted-foreground">{t("details.frameSize")}</dt><dd className="tabular-nums">{recipe.width} × {recipe.height} px</dd></div>
                <div><dt className="text-muted-foreground">{t("form.scale")}</dt><dd className="tabular-nums">{formatNumber(recipe.scale)}</dd></div>
                <div><dt className="text-muted-foreground">{t("form.objects")}</dt><dd className="tabular-nums">{recipe.objectsPerImage}</dd></div>
                <div><dt className="text-muted-foreground">{t("details.emptyFrames")}</dt><dd className="tabular-nums">{formatNumber(toPercent(recipe.emptyProbability))}%</dd></div>
            </dl>
            <p className="text-xs text-muted-foreground">{t("details.scaleNote")}</p>
            <div>
                <h4 className="mb-1 text-xs font-medium">{t("details.variants")}</h4>
                <ul className="space-y-1 text-xs" data-testid="recipe-variants">
                    {weighted.map(weight => {
                        const variant = workspace?.variants.find(item => item.id === weight.variantId);
                        const classes = bindings.filter(item => item.binding?.variantId === weight.variantId).map(item => {
                            const klass = workspace?.classes.find(entry => entry.id === item.binding?.classId);
                            return archivedName(klass?.name ?? t("details.classDeleted"), !!(klass?.archivedAt || item.binding?.archivedAt));
                        });
                        return (
                            <li className="break-words" key={weight.variantId}>
                                {archivedName(variant?.name ?? t("details.variantDeleted"), !!variant?.archivedAt)} — <span className="tabular-nums">{formatNumber(weight.percentage)}%</span>
                                {classes.length > 0 && ` · ${Array.from(new Set(classes)).join(", ")}`}
                            </li>
                        );
                    })}
                </ul>
                {workspace && bindings.some(item => !item.binding) && (
                    <p className="mt-1 text-xs text-destructive">{t("details.bindingsDeleted")}</p>
                )}
            </div>
            <div>
                <h4 className="mb-1 text-xs font-medium">{t("form.sets")}</h4>
                <ul className="space-y-1 text-xs" data-testid="recipe-sets">
                    {recipe.backgroundSetIds.map(id => {
                        const set = sets.find(item => item.id === id);
                        return (
                            <li className="break-words" key={id}>
                                {set ? `${archivedName(set.name, !!set.archivedAt)} · ${backgroundsLabel(set.itemCount)}` : t("form.setMissing")}
                            </li>
                        );
                    })}
                </ul>
            </div>
        </div>
    );
}
