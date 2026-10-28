import { useEffect, useRef, useState } from "react";
import { AlertTriangle, Archive, ArchiveRestore, Loader2, Trash2, Upload } from "lucide-react";
import type { ClassAssetResponseDto } from "@/api/projects/catalog/response-dto/class-asset-response-dto";
import type { ClassVariantResponseDto } from "@/api/projects/catalog/response-dto/class-variant-response-dto";
import { parseImportClassAssetsResult } from "@/api/projects/catalog/response-dto/import-class-assets-result-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import { ResourceImage } from "@/components/resource-image";
import { ImageViewer } from "@/components/viewer/image-viewer";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { isJobActive, type AssetImport } from "@/lib/use-asset-imports";
import { useClassAssets } from "@/lib/use-class-assets";
import { VariantColorInput } from "./variant-color-input";

/** 1 изображение, 2 изображения, 5 изображений. */
export function imagesLabel(count: number): string {
    const tens = count % 100, units = count % 10;
    const word = tens >= 11 && tens <= 14 ? "изображений" : units === 1 ? "изображение" : units >= 2 && units <= 4 ? "изображения" : "изображений";
    return `${count.toLocaleString("ru-RU")} ${word}`;
}

export interface ClassVariantActions {
    setColor: (binding: ClassVariantResponseDto, color: string | null) => void;
    toggleArchive: (binding: ClassVariantResponseDto, label: string) => void;
    remove: (binding: ClassVariantResponseDto, label: string) => void;
    removeAsset: (asset: ClassAssetResponseDto, label: string) => void;
    startImport: (binding: ClassVariantResponseDto) => void;
    cancelImport: (binding: ClassVariantResponseDto) => void;
    dismissImport: (binding: ClassVariantResponseDto) => void;
}

/** Вариант класса: цвет, счётчики, импорт, предупреждения и сетка миниатюр со страничной загрузкой. */
export function ClassVariantSection({ projectId, binding, className, variantName, classArchived, variantArchived, inputSize, busy, importState, actions }: {
    projectId: string;
    binding: ClassVariantResponseDto;
    className: string;
    variantName: string;
    classArchived: boolean;
    variantArchived: boolean;
    inputSize: InputSizeDto;
    busy: boolean;
    importState?: AssetImport;
    actions: ClassVariantActions;
}) {
    const label = `${className} / ${variantName}`;
    const archived = !!binding.archivedAt;
    const importing = importState?.selecting || (importState?.job !== undefined && isJobActive(importState.job));
    const oversized = binding.assetCount > 0
        && (binding.maxAssetWidth >= inputSize.width || binding.maxAssetHeight >= inputSize.height);
    return (
        <section data-class-variant-id={binding.id} aria-label={`Вариант ${variantName}`} className="space-y-3 border-t pt-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="flex min-w-0 flex-wrap items-center gap-2">
                    <VariantColorInput
                        key={binding.id}
                        name={variantName}
                        color={binding.color}
                        displayColor={binding.displayColor}
                        disabled={!binding.available}
                        save={color => actions.setColor(binding, color)}
                    />
                    <h3 className="font-medium break-words">{variantName}</h3>
                    <Badge variant="outline" data-testid="asset-count">{imagesLabel(binding.assetCount)}</Badge>
                    {binding.opaqueAssetCount > 0 && (
                        <Badge variant="outline" className="border-amber-500/50 text-amber-300" title="Для генерации нужен альфа-канал: PNG с прозрачным фоном.">
                            Без прозрачности: {binding.opaqueAssetCount.toLocaleString("ru-RU")}
                        </Badge>
                    )}
                    {!binding.available && <Badge variant="outline">В архиве</Badge>}
                </div>
                <div className="flex flex-wrap gap-2">
                    <Button
                        variant="ghost"
                        size="sm"
                        className="text-destructive hover:text-destructive"
                        disabled={busy}
                        aria-label={`Удалить вариант класса: ${label}`}
                        onClick={() => actions.remove(binding, label)}
                    >
                        <Trash2 />Удалить
                    </Button>
                    <Button
                        variant="outline"
                        size="sm"
                        disabled={busy || !binding.available || importing}
                        aria-label={`Добавить изображения: ${label}`}
                        onClick={() => actions.startImport(binding)}
                    >
                        <Upload />Добавить изображения
                    </Button>
                    <Button
                        variant="ghost"
                        size="sm"
                        disabled={busy || classArchived || variantArchived}
                        aria-label={`${archived ? "Восстановить" : "В архив"}: ${label}`}
                        title={classArchived || variantArchived ? "Сначала восстановите класс и вариант проекта" : undefined}
                        onClick={() => actions.toggleArchive(binding, label)}
                    >
                        {archived ? <><ArchiveRestore />Восстановить</> : <><Archive />В архив</>}
                    </Button>
                </div>
            </div>
            {importState && <ImportStatus state={importState} cancel={() => actions.cancelImport(binding)} dismiss={() => actions.dismissImport(binding)} />}
            {oversized && (
                <Alert className="border-amber-500/40" role="status" data-testid="scale-warning">
                    <AlertTriangle className="text-amber-400" />
                    <AlertTitle>Потребуется изменить масштаб</AlertTitle>
                    <AlertDescription>
                        Некоторые изображения не меньше входа модели ({inputSize.width} × {inputSize.height} px). При генерации и разметке их нужно уменьшить. Импорт разрешён.
                    </AlertDescription>
                </Alert>
            )}
            <AssetGrid key={binding.id} projectId={projectId} classVariantId={binding.id} label={label} busy={busy} remove={actions.removeAsset} />
        </section>
    );
}

function ImportStatus({ state, cancel, dismiss }: { state: AssetImport; cancel: () => void; dismiss: () => void }) {
    const job = state.job;
    if (state.selecting && !job) {
        return <p role="status" className="text-sm text-muted-foreground">Выберите файлы в окне выбора…</p>;
    }
    if (!job) {
        return state.error ? <ImportError message={state.error} dismiss={dismiss} /> : null;
    }
    if (isJobActive(job)) {
        const percent = job.total ? Math.round(((job.processed ?? 0) / job.total) * 100) : job.progress;
        return (
            <div role="status" data-testid="import-progress" className="space-y-2 rounded-lg border bg-card p-3 text-sm">
                <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className="flex items-center gap-2">
                        <Loader2 className="size-4 animate-spin" aria-hidden />
                        {job.status === "queued" ? "Импорт в очереди…" : "Импорт изображений"}
                        {job.total !== null && <span className="tabular-nums text-muted-foreground">{(job.processed ?? 0).toLocaleString("ru-RU")} из {job.total.toLocaleString("ru-RU")}</span>}
                    </span>
                    <Button variant="outline" size="sm" disabled={job.cancelRequested} onClick={cancel}>
                        {job.cancelRequested ? "Отмена…" : "Отменить"}
                    </Button>
                </div>
                <div
                    className="h-1.5 overflow-hidden rounded-full bg-secondary"
                    role="progressbar"
                    aria-label="Прогресс импорта"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={percent ?? undefined}
                >
                    <div className={percent === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"} style={percent === null ? undefined : { width: `${percent}%` }} />
                </div>
                {state.error && <p className="text-destructive">{state.error}</p>}
            </div>
        );
    }
    const result = parseImportClassAssetsResult(job);
    const title = job.status === "succeeded" ? "Импорт завершён" : job.status === "cancelled" ? "Импорт отменён" : "Импорт не завершён";
    return (
        <Alert variant={job.status === "succeeded" ? "default" : "destructive"} data-testid="import-result">
            <AlertTitle>{title}</AlertTitle>
            <AlertDescription>
                {result && (
                    <p className="tabular-nums">
                        Добавлено: {result.added.toLocaleString("ru-RU")} · Пропущено одинаковых: {result.duplicates.toLocaleString("ru-RU")} · Отклонено: {result.rejected.toLocaleString("ru-RU")}
                    </p>
                )}
                {job.status !== "succeeded" && job.status !== "cancelled" && job.error && <p>{job.error.message}</p>}
                {!!result?.rejectedFiles.length && (
                    <ul className="mt-1 max-h-32 list-disc space-y-0.5 overflow-y-auto pl-5 text-xs">
                        {result.rejectedFiles.map((file, index) => <li key={`${file.name}-${index}`} className="break-words">{file.name} — {file.message}</li>)}
                        {result.rejected > result.rejectedFiles.length && <li>и ещё {(result.rejected - result.rejectedFiles.length).toLocaleString("ru-RU")}</li>}
                    </ul>
                )}
                <Button variant="ghost" size="sm" className="mt-1" onClick={dismiss}>Скрыть</Button>
            </AlertDescription>
        </Alert>
    );
}

function ImportError({ message, dismiss }: { message: string; dismiss: () => void }) {
    return (
        <Alert variant="destructive">
            <AlertDescription>
                <p>{message}</p>
                <Button variant="ghost" size="sm" className="mt-1" onClick={dismiss}>Скрыть</Button>
            </AlertDescription>
        </Alert>
    );
}

function AssetGrid({ projectId, classVariantId, label, busy, remove }: {
    projectId: string;
    classVariantId: string;
    label: string;
    busy: boolean;
    remove: (asset: ClassAssetResponseDto, label: string) => void;
}) {
    const assets = useClassAssets(projectId, classVariantId);
    const sentinel = useRef<HTMLDivElement>(null);
    const { loadMore } = assets;
    const hasMore = assets.nextCursor !== null;
    const [viewed, setViewed] = useState<{ id: string; pendingNext?: boolean }>();
    const viewedIndex = viewed ? assets.items.findIndex(asset => asset.id === viewed.id) : -1;
    const viewedAsset = viewedIndex >= 0 ? assets.items[viewedIndex] : undefined;

    useEffect(() => {
        // «Далее» в конце загруженного списка подгружает страницу и переходит к её первому изображению.
        if (!viewed?.pendingNext || assets.loading) return;
        const following = assets.items[viewedIndex + 1];
        setViewed(following ? { id: following.id } : { id: viewed.id });
    }, [viewed, viewedIndex, assets.items, assets.loading]);

    function next() {
        const following = assets.items[viewedIndex + 1];
        if (following) setViewed({ id: following.id });
        else if (hasMore && viewedAsset) {
            setViewed({ id: viewedAsset.id, pendingNext: true });
            void loadMore();
        }
    }

    useEffect(() => {
        const element = sentinel.current;
        if (!element || !hasMore) return;
        const observer = new IntersectionObserver(entries => {
            if (entries.some(entry => entry.isIntersecting)) void loadMore();
        }, { rootMargin: "200px" });
        observer.observe(element);
        return () => observer.disconnect();
    }, [hasMore, loadMore]);

    if (!assets.items.length) {
        if (assets.loading) return <p role="status" className="py-6 text-sm text-muted-foreground">Загрузка изображений…</p>;
        if (assets.error) return <GridError message={assets.error} retry={() => void assets.reload()} />;
        return (
            <p className="py-6 text-sm text-muted-foreground">
                Добавьте PNG с прозрачным фоном для генерации. Для ручной разметки исходники не обязательны.
            </p>
        );
    }
    return (
        <div className="space-y-3">
            <div className="grid grid-cols-[repeat(auto-fill,minmax(8rem,1fr))] gap-3" data-testid="asset-grid">
                {assets.items.map(asset => {
                    const size = `${asset.width} × ${asset.height} px`;
                    return (
                        <figure key={asset.id} data-class-asset-id={asset.id} className="min-w-0">
                            <button
                                type="button"
                                className="relative block w-full rounded-md outline-none focus-visible:ring-2 focus-visible:ring-ring"
                                aria-label={`Просмотреть изображение: ${label} · ${size}`}
                                onClick={() => setViewed({ id: asset.id })}
                            >
                                <ResourceImage
                                    src={asset.thumbnailUrl}
                                    alt={`${label} · ${size}`}
                                    fit="contain"
                                    className="h-24 w-full border"
                                    onExpired={assets.renew}
                                />
                                {!asset.hasAlpha && (
                                    <span
                                        className="absolute top-1 left-1 rounded bg-black/70 px-1 py-0.5 text-[10px] leading-none text-amber-300"
                                        title="Нет альфа-канала: для генерации нужен PNG с прозрачным фоном."
                                    >
                                        Без прозрачности
                                    </span>
                                )}
                            </button>
                            <figcaption className="mt-1 text-center text-xs tabular-nums text-muted-foreground">{size}</figcaption>
                            <Button
                                variant="ghost"
                                size="sm"
                                className="w-full text-destructive hover:text-destructive"
                                disabled={busy}
                                aria-label={`Удалить изображение: ${label} · ${size}`}
                                onClick={() => remove(asset, `${label} · ${size}`)}
                            >
                                <Trash2 />Удалить
                            </Button>
                        </figure>
                    );
                })}
            </div>
            {assets.error && <GridError message={assets.error} retry={() => void assets.reload()} />}
            {viewedAsset && (
                <ImageViewer
                    source={{
                        id: viewedAsset.id,
                        name: `${label} · ${viewedAsset.width} × ${viewedAsset.height} px`,
                        width: viewedAsset.width,
                        height: viewedAsset.height,
                        src: viewedAsset.originalUrl,
                        renew: assets.renew,
                        metadata: (
                            <p>
                                {viewedAsset.format.toUpperCase()} · {Math.ceil(viewedAsset.byteSize / 1024).toLocaleString("ru-RU")} КБ · {viewedAsset.hasAlpha ? "с прозрачностью" : "без прозрачности"}
                            </p>
                        ),
                    }}
                    close={() => setViewed(undefined)}
                    onPrevious={viewedIndex > 0 ? () => setViewed({ id: assets.items[viewedIndex - 1].id }) : undefined}
                    onNext={viewedIndex < assets.items.length - 1 || hasMore ? next : undefined}
                    navigationBusy={!!viewed?.pendingNext}
                    position={`${viewedIndex + 1} / ${assets.items.length}${hasMore ? "+" : ""}`}
                />
            )}
            {hasMore && (
                <div ref={sentinel} className="flex justify-center">
                    <Button variant="outline" size="sm" disabled={assets.loading} onClick={() => void loadMore()}>
                        {assets.loading ? "Загрузка…" : "Показать ещё"}
                    </Button>
                </div>
            )}
        </div>
    );
}

function GridError({ message, retry }: { message: string; retry: () => void }) {
    return (
        <Alert variant="destructive">
            <AlertDescription>
                <p>{message}</p>
                <Button variant="ghost" size="sm" className="mt-1" onClick={retry}>Повторить</Button>
            </AlertDescription>
        </Alert>
    );
}
