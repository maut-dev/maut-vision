import type { ImageGroupResponseDto } from "@/api/projects/images/response-dto/image-group-response-dto";
import { splitHintLabels } from "@/components/images/image-group-dialogs";
import { Checkbox } from "@/components/ui/checkbox";
import { count } from "@/lib/datasets/dataset-view";
import type { ReviewCount } from "@/lib/use-group-review-counts";

/**
 * «Группы изображений» формы сборки: у каждой группы — число активных кадров, происхождение, назначение и доля проверенных
 * (считается для групп до 2000 кадров; у крупных — «станет известна в плане»). Выбранная, но архивная или удалённая группа
 * остаётся строкой «недоступна», чтобы её можно было снять.
 */
export function DatasetGroupPicker({ groups, selected, toggle, reviewCount, invalid }: {
    groups: readonly ImageGroupResponseDto[];
    selected: readonly string[];
    toggle: (groupId: string) => void;
    reviewCount: (groupId: string) => ReviewCount | undefined;
    invalid: boolean;
}) {
    const active = groups.filter(group => !group.archivedAt);
    const missing = selected.filter(id => !active.some(group => group.id === id));
    return (
        <fieldset className="max-h-64 min-h-32 space-y-2 overflow-y-auto rounded-md border p-3" aria-invalid={invalid || undefined} data-testid="dataset-groups">
            <legend className="mb-2 text-xs text-muted-foreground">Группы изображений</legend>
            {active.map(group => {
                const images = group.imageCount - group.archivedImageCount;
                const review = reviewCount(group.id);
                return (
                    <label key={group.id} className="flex items-start gap-2 text-sm" data-dataset-group-id={group.id}>
                        <Checkbox className="mt-0.5" checked={selected.includes(group.id)} aria-label={`Группа: ${group.name}`} onCheckedChange={() => toggle(group.id)} />
                        <span className="min-w-0">
                            <span className="block break-words">{group.name}</span>
                            <span className="block text-xs text-muted-foreground tabular-nums" data-testid="dataset-group-meta">
                                {count(images)} кадров · {group.origin === "generated" ? "Генерация" : "Исходные изображения"} · {splitHintLabels[group.splitHint]}
                                {" · "}<ReviewShare review={review} images={images} />
                                {group.generationState === "producing" && " · генерация идёт"}
                                {group.generationState === "partial" && " · генерация не завершена"}
                            </span>
                        </span>
                    </label>
                );
            })}
            {missing.map(id => (
                <label key={id} className="flex items-center gap-2 text-sm text-muted-foreground" data-dataset-group-id={id}>
                    <Checkbox checked aria-label="Недоступная группа" onCheckedChange={() => toggle(id)} />
                    Группа в архиве или удалена — снимите выбор
                </label>
            ))}
            {!active.length && !missing.length && <p className="text-sm text-muted-foreground">Сначала импортируйте или сгенерируйте изображения.</p>}
        </fieldset>
    );
}

function ReviewShare({ review, images }: { review: ReviewCount | undefined; images: number }) {
    if (images === 0) return <span data-testid="dataset-group-reviewed">проверено: —</span>;
    if (review === undefined) return <span data-testid="dataset-group-reviewed">проверено: считаем…</span>;
    if (review === null) return <span data-testid="dataset-group-reviewed" title="Группа крупнее 2000 кадров: число проверенных покажет «Проверить перед сборкой».">проверено: в плане</span>;
    const percent = Math.round(100 * review.reviewed / Math.max(1, review.active));
    return <span data-testid="dataset-group-reviewed" data-reviewed={review.reviewed}>проверено: {percent}% ({count(review.reviewed)})</span>;
}
