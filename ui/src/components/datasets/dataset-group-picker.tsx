import { useTranslation } from "react-i18next";
import type { ImageGroupResponseDto } from "@/api/projects/images/response-dto/image-group-response-dto";
import { Checkbox } from "@/components/ui/checkbox";
import { count } from "@/lib/datasets/dataset-view";
import type { ReviewCount } from "@/lib/use-group-review-counts";

/**
 * "Image groups" of the build form: each group shows the number of active frames, the origin, the purpose and the reviewed share
 * (counted for groups up to 2000 frames; for larger ones it "becomes known in the plan"). A chosen group that was archived or deleted
 * stays as an "unavailable" row so it can be cleared.
 */
export function DatasetGroupPicker({ groups, selected, toggle, reviewCount, invalid }: {
    groups: readonly ImageGroupResponseDto[];
    selected: readonly string[];
    toggle: (groupId: string) => void;
    reviewCount: (groupId: string) => ReviewCount | undefined;
    invalid: boolean;
}) {
    const { t } = useTranslation("datasets");
    const active = groups.filter(group => !group.archivedAt);
    const missing = selected.filter(id => !active.some(group => group.id === id));
    return (
        <fieldset className="max-h-64 min-h-32 space-y-2 overflow-y-auto rounded-md border p-3" aria-invalid={invalid || undefined} data-testid="dataset-groups">
            <legend className="mb-2 text-xs text-muted-foreground">{t("groups.title")}</legend>
            {active.map(group => {
                const images = group.imageCount - group.archivedImageCount;
                const review = reviewCount(group.id);
                return (
                    <label key={group.id} className="flex items-start gap-2 text-sm" data-dataset-group-id={group.id}>
                        <Checkbox className="mt-0.5" checked={selected.includes(group.id)} aria-label={t("groups.label", { name: group.name })} onCheckedChange={() => toggle(group.id)} />
                        <span className="min-w-0">
                            <span className="block break-words">{group.name}</span>
                            <span className="block text-xs text-muted-foreground tabular-nums" data-testid="dataset-group-meta">
                                {[t("groups.frames", { count: images, value: count(images) }), group.origin === "generated" ? t("groups.generated") : t("groups.manual"),
                                    t(`groups.splitHints.${group.splitHint}`)].join(" · ")}
                                {" · "}<ReviewShare review={review} images={images} />
                                {group.generationState === "producing" && ` · ${t("groups.producing")}`}
                                {group.generationState === "partial" && ` · ${t("groups.partial")}`}
                            </span>
                        </span>
                    </label>
                );
            })}
            {missing.map(id => (
                <label key={id} className="flex items-center gap-2 text-sm text-muted-foreground" data-dataset-group-id={id}>
                    <Checkbox checked aria-label={t("groups.unavailableLabel")} onCheckedChange={() => toggle(id)} />
                    {t("groups.unavailable")}
                </label>
            ))}
            {!active.length && !missing.length && <p className="text-sm text-muted-foreground">{t("groups.empty")}</p>}
        </fieldset>
    );
}

function ReviewShare({ review, images }: { review: ReviewCount | undefined; images: number }) {
    const { t } = useTranslation("datasets");
    if (images === 0) return <span data-testid="dataset-group-reviewed">{t("groups.reviewedNone")}</span>;
    if (review === undefined) return <span data-testid="dataset-group-reviewed">{t("groups.reviewedCounting")}</span>;
    if (review === null) return <span data-testid="dataset-group-reviewed" title={t("groups.reviewedLargeHint")}>{t("groups.reviewedInPlan")}</span>;
    const percent = Math.round(100 * review.reviewed / Math.max(1, review.active));
    return <span data-testid="dataset-group-reviewed" data-reviewed={review.reviewed}>{t("groups.reviewed", { percent, number: count(review.reviewed) })}</span>;
}
