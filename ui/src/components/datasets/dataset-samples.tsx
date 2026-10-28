import { useEffect, useRef, useState, type CSSProperties } from "react";
import type { DatasetSplit } from "@/api/projects/datasets/request-dto/dataset-request-dto";
import type { DatasetSampleResponseDto, DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { ResourceImage } from "@/components/resource-image";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ImageViewer } from "@/components/viewer/image-viewer";
import { VirtualGrid } from "@/components/virtual-grid";
import { count, datasetSplits, sampleOverlays, splitLabel, type SampleOverlayKind } from "@/lib/datasets/dataset-view";
import { useDatasetSamples } from "@/lib/use-dataset-samples";
import { DatasetSampleDetails } from "./dataset-sample-details";

const tileImageHeight = 150;

/**
 * Примеры версии по частям: виртуализированная сетка миниатюр с рамками и подписью «W × H · n объектов · m негативов». Кадр
 * открывается в общем просмотрщике с переключателями «Объекты», «Негативы», «ROI»; «Назад»/«Далее» проходят все образцы части,
 * дочитывая страницы по требованию, позиция — «i / N» по составу версии. Фокус после закрытия возвращается к плитке.
 */
export function DatasetSamples({ projectId, version }: { projectId: string; version: DatasetVersionResponseDto }) {
    const firstSplit = datasetSplits.find(split => version.counts[split] > 0) ?? "train";
    const [split, setSplit] = useState<DatasetSplit>(firstSplit);
    const samples = useDatasetSamples(projectId, version.id, split);
    const [viewedId, setViewedId] = useState<string>();
    const [navigating, setNavigating] = useState(false);
    const [visible, setVisible] = useState<Record<SampleOverlayKind, boolean>>({ object: true, negative: true, roi: true });
    const items = samples.items;
    const index = viewedId ? items.findIndex(item => item.id === viewedId) : -1;
    const viewed = index >= 0 ? items[index] : undefined;
    const total = version.counts[split];
    const navigation = useRef(0);
    const [pendingIndex, setPendingIndex] = useState<number>();

    useEffect(() => { setVisible({ object: true, negative: true, roi: true }); }, [viewedId]);

    async function next() {
        if (index < 0) return;
        if (index < items.length - 1) {
            setViewedId(items[index + 1].id);
            return;
        }
        if (!samples.hasMore) return;
        const request = ++navigation.current;
        setNavigating(true);
        try {
            const before = items.length;
            if (await samples.loadMore() && request === navigation.current) setPendingIndex(before);
        } finally {
            if (request === navigation.current) setNavigating(false);
        }
    }

    // The next page arrives with a render; the viewer moves to its first sample afterwards.
    useEffect(() => {
        if (pendingIndex !== undefined && items[pendingIndex]) {
            setViewedId(items[pendingIndex].id);
            setPendingIndex(undefined);
        }
    }, [pendingIndex, items]);

    function close() {
        navigation.current++;
        setNavigating(false);
        setViewedId(undefined);
    }

    const overlays = viewed ? sampleOverlays(viewed, version).filter(overlay => visible[overlay.kind]) : [];
    return (
        <section className="flex min-h-[28rem] flex-col gap-3" aria-label="Примеры датасета" data-testid="dataset-samples">
            <div className="flex flex-wrap items-center gap-3">
                <h3 className="text-sm font-medium">Примеры</h3>
                <Select value={split} onValueChange={value => { setViewedId(undefined); setSplit(value as DatasetSplit); }}>
                    <SelectTrigger aria-label="Часть датасета" className="w-48" data-testid="dataset-sample-split"><SelectValue /></SelectTrigger>
                    <SelectContent>
                        {datasetSplits.map(item => <SelectItem key={item} value={item}>{splitLabel(item)} · {count(version.counts[item])}</SelectItem>)}
                    </SelectContent>
                </Select>
                <span className="text-xs text-muted-foreground tabular-nums" data-testid="dataset-sample-count">
                    {count(items.length)} из {count(total)} · Снимки при сборке
                </span>
            </div>
            {samples.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{samples.error}</p>
                        <Button variant="outline" size="sm" className="mt-1" disabled={samples.loading} onClick={() => void samples.loadMore()}>Повторить загрузку примеров</Button>
                    </AlertDescription>
                </Alert>
            )}
            {!samples.loading && !samples.error && !samples.hasMore && !items.length ? (
                <p className="text-sm text-muted-foreground" data-testid="dataset-samples-empty">В этой части нет изображений. Выберите другую часть датасета.</p>
            ) : (
                <VirtualGrid
                    items={items}
                    keyOf={item => item.id}
                    minTileWidth={170}
                    tileHeight={tileImageHeight + 60}
                    hasMore={samples.hasMore}
                    loading={samples.loading}
                    loadMore={() => void samples.loadMore()}
                    label={`Примеры части ${splitLabel(split)}`}
                    testId="dataset-sample-grid"
                    loadingText="Загрузка примеров…"
                    className="min-h-[24rem]"
                    renderTile={(sample, position, style) => (
                        <SampleTile key={sample.id} sample={sample} version={version} position={position} style={style}
                            open={() => setViewedId(sample.id)} renew={() => void samples.renew(sample.id)} />
                    )}
                />
            )}
            {viewed && (
                <ImageViewer
                    source={{
                        id: `${version.id}:${viewed.id}`,
                        name: `${version.name} · ${splitLabel(viewed.split)} · пример ${index + 1}`,
                        width: viewed.width,
                        height: viewed.height,
                        src: viewed.imageUrl,
                        renew: () => void samples.renew(viewed.id),
                        overlays: overlays.map(overlay => ({ id: overlay.id, label: overlay.label, color: overlay.color, kind: overlay.kind, box: overlay.box })),
                        metadata: (
                            <div className="space-y-2">
                                <div className="flex flex-wrap gap-x-4 gap-y-2">
                                    <label className="flex items-center gap-2">
                                        <Checkbox aria-label="Рамки объектов датасета" checked={visible.object} onCheckedChange={value => setVisible(current => ({ ...current, object: value === true }))} />Объекты
                                    </label>
                                    <label className="flex items-center gap-2">
                                        <Checkbox aria-label="Негативные объекты датасета" checked={visible.negative} onCheckedChange={value => setVisible(current => ({ ...current, negative: value === true }))} />Негативы
                                    </label>
                                    <label className="flex items-center gap-2">
                                        <Checkbox aria-label="Область ROI датасета" checked={visible.roi} onCheckedChange={value => setVisible(current => ({ ...current, roi: value === true }))} />ROI
                                    </label>
                                </div>
                                <DatasetSampleDetails sample={viewed} version={version} />
                            </div>
                        ),
                    }}
                    close={close}
                    onPrevious={index > 0 ? () => setViewedId(items[index - 1].id) : undefined}
                    onNext={index < items.length - 1 || samples.hasMore ? () => void next() : undefined}
                    navigationBusy={navigating}
                    position={`${index + 1} / ${count(total)}`}
                />
            )}
        </section>
    );
}

function SampleTile({ sample, version, position, style, open, renew }: {
    sample: DatasetSampleResponseDto;
    version: DatasetVersionResponseDto;
    position: number;
    style: CSSProperties;
    open: () => void;
    renew: () => void;
}) {
    const overlays = sampleOverlays(sample, version);
    return (
        <div style={style} className="min-w-0" data-dataset-sample-id={sample.id} data-testid="dataset-sample-tile" data-objects={sample.boxes.length}>
            <button
                type="button"
                className="block w-full rounded-md border p-1.5 text-left outline-none hover:bg-accent/40 focus-visible:ring-2 focus-visible:ring-ring"
                aria-label={`Открыть изображение датасета: пример ${position + 1}`}
                data-testid="dataset-sample-open"
                onClick={open}
            >
                <div className="relative" style={{ height: tileImageHeight }}>
                    <ResourceImage src={sample.thumbnailUrl} alt={`Пример ${position + 1} датасета ${version.name}`} fit="contain" className="size-full" onExpired={renew} />
                    {overlays.length > 0 && (
                        <svg className="pointer-events-none absolute inset-0 size-full" viewBox={`0 0 ${sample.width} ${sample.height}`} preserveAspectRatio="xMidYMid meet"
                            aria-hidden data-testid="dataset-sample-overlay">
                            {overlays.map(overlay => (
                                <rect key={overlay.id} data-kind={overlay.kind} x={overlay.box.x * sample.width} y={overlay.box.y * sample.height}
                                    width={overlay.box.width * sample.width} height={overlay.box.height * sample.height} fill="none" stroke={overlay.color} strokeWidth={2}
                                    vectorEffect="non-scaling-stroke" strokeDasharray={overlay.kind === "object" ? undefined : "4 3"} />
                            ))}
                        </svg>
                    )}
                </div>
                <span className="mt-1 line-clamp-2 block text-xs text-muted-foreground tabular-nums" data-testid="dataset-sample-caption">
                    {sample.width} × {sample.height} · {sample.boxes.length} объектов · {sample.negatives.length} негативов
                </span>
            </button>
        </div>
    );
}
