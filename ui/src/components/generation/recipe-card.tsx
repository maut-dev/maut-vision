import { Archive, ArchiveRestore, Pencil, Trash2, WandSparkles } from "lucide-react";
import { useTranslation } from "react-i18next";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { GenerationRecipeResponseDto } from "@/api/projects/generation/response-dto/generation-recipe-response-dto";
import { Button } from "@/components/ui/button";
import { RecipeDetails } from "./recipe-details";
import { formatNumber } from "@/lib/format";

/**
 * A preset card: the name with the mark "Archived", the description, details, "Generate N images", "Edit", "Archive" / "Restore",
 * "Delete". An archived preset is read-only and does not run.
 */
export function RecipeCard({ recipe, workspace, sets, count, canStart, editing, busy, start, edit, toggleArchive, remove }: {
    recipe: GenerationRecipeResponseDto;
    workspace: CatalogWorkspaceResponseDto | undefined;
    sets: readonly BackgroundSetResponseDto[];
    count: number;
    canStart: boolean;
    editing: boolean;
    busy: boolean;
    start: () => void;
    edit: () => void;
    toggleArchive: () => void;
    remove: () => void;
}) {
    const { t } = useTranslation("generation");
    const archived = !!recipe.archivedAt;
    return (
        <article className={`space-y-3 rounded-xl border bg-card p-5 ${editing ? "border-primary/60" : ""}`} data-generation-recipe-id={recipe.id}
            data-revision={recipe.revision} data-archived={archived || undefined} aria-label={t("recipe.label", { name: recipe.name })}>
            <div className="flex items-start justify-between gap-2">
                <h3 className="min-w-0 font-medium break-words" data-testid="recipe-name-title">
                    {recipe.name}
                    {archived && <span className="ml-2 text-xs text-muted-foreground" data-testid="recipe-archived">{t("recipe.archived")}</span>}
                </h3>
                <span className="shrink-0 text-xs text-muted-foreground tabular-nums">{t("recipe.revision", { revision: recipe.revision })}</span>
            </div>
            {recipe.description && <p className="text-sm break-words text-muted-foreground">{recipe.description}</p>}
            <RecipeDetails recipe={recipe} workspace={workspace} sets={sets} />
            <div className="flex flex-wrap items-center gap-2">
                <Button className="h-auto min-h-9 max-w-full min-w-0 whitespace-normal" disabled={busy || !canStart || archived} data-testid="recipe-start" onClick={start}>
                    <WandSparkles />{t("recipe.start", { count, value: formatNumber(count) })}
                </Button>
                <Button size="sm" variant="ghost" disabled={busy || archived || editing} aria-label={t("recipe.editLabel", { name: recipe.name })} data-testid="recipe-edit" onClick={edit}>
                    <Pencil />{t("recipe.edit")}
                </Button>
                <Button size="sm" variant="ghost" disabled={busy} aria-label={t(archived ? "recipe.restoreLabel" : "recipe.archiveLabel", { name: recipe.name })} data-testid="recipe-archive" onClick={toggleArchive}>
                    {archived ? <><ArchiveRestore />{t("recipe.restore")}</> : <><Archive />{t("recipe.archive")}</>}
                </Button>
                <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" disabled={busy} aria-label={t("recipe.deleteLabel", { name: recipe.name })}
                    data-testid="recipe-delete" onClick={remove}>
                    <Trash2 />{t("recipe.delete")}
                </Button>
            </div>
        </article>
    );
}
