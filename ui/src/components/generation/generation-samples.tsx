import { useState } from "react";
import { Loader2 } from "lucide-react";
import type { GenerationSampleResponseDto } from "@/api/projects/generation/response-dto/generation-sample-response-dto";
import type { AnnotationPalette } from "@/components/images/annotation-palette";
import { ResourceImage } from "@/components/resource-image";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { ImageViewer, type ViewerOverlay } from "@/components/viewer/image-viewer";
import type { GenerationSamplesState } from "@/lib/use-generation-samples";

/**
 * «Пример кадра»: до 4 кадров текущих настроек формы с рамками объектов (цвет — вариант класса), без сохранения. Кадр
 * открывается в общем просмотрщике; фокус после закрытия возвращается к плитке. stale — настройки или seed изменились.
 */
export function GenerationSamples({ samples, palette, stale }: {
    samples: GenerationSamplesState;
    palette: AnnotationPalette;
    stale: boolean;
}) {
    const [viewed, setViewed] = useState<number>();
    const items = samples.samples;
    const index = viewed === undefined ? -1 : items.findIndex(item => item.sampleIndex === viewed);
    const sample = index >= 0 ? items[index] : undefined;
    if (!items.length && !samples.loading && !samples.error) return null;

    const overlays = (item: GenerationSampleResponseDto): ViewerOverlay[] => item.objects.map((object, position) => ({
        id: `${item.sampleIndex}:${position}`,
        label: palette.labelOf(object.classId, object.classVariantId),
        color: palette.colorOf(object.classId, object.classVariantId),
        kind: "object",
        box: object.box,
    }));

    return (
        <section className="space-y-2" aria-label="Пример кадра" data-testid="generation-samples" aria-busy={samples.loading}>
            <div className="flex flex-wrap items-baseline justify-between gap-2">
                <h3 className="text-sm font-medium">Пример кадра{samples.seed !== undefined && <span className="ml-2 text-xs font-normal text-muted-foreground tabular-nums">seed {samples.seed}</span>}</h3>
                {samples.loading && <span role="status" className="flex items-center gap-1 text-xs text-muted-foreground"><Loader2 className="size-3.5 animate-spin" aria-hidden />Рисуем кадры…</span>}
            </div>
            {samples.error && <Alert variant="destructive" data-testid="generation-samples-error"><AlertDescription>{samples.error}</AlertDescription></Alert>}
            {items.length > 0 && (
                <>
                    <p className="text-xs text-muted-foreground" data-testid="generation-samples-note">
                        {stale
                            ? "Настройки или seed изменились после примера — нажмите «Пример кадра», чтобы обновить."
                            : `${items.length === 1 ? "Так будет выглядеть первый кадр" : `Так будут выглядеть первые ${items.length} кадра`} запуска с этим seed. Ничего не сохраняется.`}
                    </p>
                    <ul className={`grid grid-cols-2 gap-2 ${stale ? "opacity-60" : ""}`}>
                        {items.map(item => (
                            <li key={item.sampleIndex} data-testid="generation-sample" data-objects={item.objects.length}>
                                <button
                                    type="button"
                                    className="w-full rounded-md text-left outline-none transition-colors hover:bg-accent/40 focus-visible:ring-2 focus-visible:ring-ring"
                                    aria-label={`Открыть пример кадра ${item.sampleIndex + 1}`}
                                    data-testid="generation-sample-open"
                                    onClick={() => setViewed(item.sampleIndex)}
                                >
                                    <div className="relative">
                                        <ResourceImage src={item.imageUrl} alt={`Пример кадра ${item.sampleIndex + 1}`} width={item.width} height={item.height} fit="contain"
                                            className="w-full" onExpired={samples.renew} />
                                        <SampleOverlay sample={item} palette={palette} />
                                    </div>
                                    <span className="mt-1 block text-xs text-muted-foreground tabular-nums">
                                        Кадр {item.sampleIndex + 1} · {item.objects.length ? `объектов: ${item.objects.length}` : "без объектов"}
                                        {item.missingObjects > 0 && <span className="text-amber-700 dark:text-amber-400"> · не поместилось: {item.missingObjects}</span>}
                                    </span>
                                </button>
                            </li>
                        ))}
                    </ul>
                </>
            )}
            {sample && (
                <ImageViewer
                    source={{
                        id: `sample-${sample.sampleIndex}`,
                        name: `Пример кадра ${sample.sampleIndex + 1}`,
                        width: sample.width,
                        height: sample.height,
                        src: sample.imageUrl,
                        renew: samples.renew,
                        overlays: overlays(sample),
                        metadata: (
                            <p data-testid="generation-sample-details">
                                {sample.width} × {sample.height} · объектов: {sample.objects.length}{sample.missingObjects > 0 ? ` · не поместилось: ${sample.missingObjects}` : ""}
                            </p>
                        ),
                    }}
                    close={() => setViewed(undefined)}
                    onPrevious={index > 0 ? () => setViewed(items[index - 1].sampleIndex) : undefined}
                    onNext={index < items.length - 1 ? () => setViewed(items[index + 1].sampleIndex) : undefined}
                    position={`${index + 1} / ${items.length}`}
                />
            )}
        </section>
    );
}

/** Рамки поверх кадра: viewBox в пикселях кадра повторяет object-contain. */
function SampleOverlay({ sample, palette }: { sample: GenerationSampleResponseDto; palette: AnnotationPalette }) {
    if (!sample.objects.length) return null;
    const { width, height } = sample;
    return (
        <svg className="pointer-events-none absolute inset-0 size-full" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="xMidYMid meet" aria-hidden data-testid="sample-overlay">
            {sample.objects.map((item, position) => {
                const color = palette.colorOf(item.classId, item.classVariantId);
                return (
                    <rect key={position} x={item.box.x * width} y={item.box.y * height} width={item.box.width * width} height={item.box.height * height}
                        fill={color} fillOpacity={0.15} stroke={color} strokeWidth={2} vectorEffect="non-scaling-stroke" />
                );
            })}
        </svg>
    );
}
