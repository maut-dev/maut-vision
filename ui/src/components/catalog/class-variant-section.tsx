import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { AlertTriangle, Archive, ArchiveRestore, Loader2, Trash2, Upload } from "lucide-react";
import type { ClassAssetResponseDto } from "@/api/projects/catalog/response-dto/class-asset-response-dto";
import type { ClassVariantResponseDto } from "@/api/projects/catalog/response-dto/class-variant-response-dto";
import { parseImportClassAssetsResult } from "@/api/projects/catalog/response-dto/import-class-assets-result-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import { Collapsible } from "@/components/layout/collapsible";
import { Notice } from "@/components/layout/notice";
import { Progress } from "@/components/layout/progress";
import { ResourceImage } from "@/components/resource-image";
import { ImageViewer } from "@/components/viewer/image-viewer";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { isJobActive, type AssetImport } from "@/lib/use-asset-imports";
import { useClassAssets } from "@/lib/use-class-assets";
import { VariantColorInput } from "./variant-color-input";
import { errorText, jobErrorText } from "@/lib/errors";
import { i18n } from "@/i18n";
import { formatNumber } from "@/lib/format";
import "@/i18n/sections/catalog";

/** "1 image", "2 images" with the plural forms of the language. */
export function imagesLabel(count: number): string {
    return i18n.t("catalog:binding.images", { count });
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

/** A class variant: the color, counters, import, warnings and a grid of thumbnails loaded by pages. */
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
    const { t } = useTranslation("catalog");
    const label = `${className} / ${variantName}`;
    const archived = !!binding.archivedAt;
    const importing = importState?.selecting || (importState?.job !== undefined && isJobActive(importState.job));
    const oversized = binding.assetCount > 0
        && (binding.maxAssetWidth >= inputSize.width || binding.maxAssetHeight >= inputSize.height);
    return (
        <section data-class-variant-id={binding.id} aria-label={t("binding.label", { name: variantName })} className="space-y-3 border-t border-line pt-4 first:border-t-0 first:pt-0">
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
                        <Badge variant="warn" title={t("binding.noAlphaHint")}>
                            {t("binding.noAlphaCount", { number: formatNumber(binding.opaqueAssetCount) })}
                        </Badge>
                    )}
                    {!binding.available && <Badge variant="outline">{t("list.archived")}</Badge>}
                </div>
                <div className="flex flex-wrap gap-2">
                    <Button
                        variant="ghost"
                        size="sm"
                        className="text-destructive hover:text-destructive"
                        disabled={busy}
                        aria-label={t("binding.deleteLabel", { name: label })}
                        onClick={() => actions.remove(binding, label)}
                    >
                        <Trash2 />{t("list.delete")}
                    </Button>
                    <Button
                        variant="outline"
                        size="sm"
                        disabled={busy || !binding.available || importing}
                        aria-label={t("binding.addLabel", { name: label })}
                        onClick={() => actions.startImport(binding)}
                    >
                        <Upload />{t("binding.add")}
                    </Button>
                    <Button
                        variant="ghost"
                        size="sm"
                        disabled={busy || classArchived || variantArchived}
                        aria-label={t(archived ? "list.restoreLabel" : "list.archiveLabel", { name: label })}
                        title={classArchived || variantArchived ? t("binding.restoreFirst") : undefined}
                        onClick={() => actions.toggleArchive(binding, label)}
                    >
                        {archived ? <><ArchiveRestore />{t("list.restore")}</> : <><Archive />{t("list.archive")}</>}
                    </Button>
                </div>
            </div>
            {importState && <ImportStatus state={importState} cancel={() => actions.cancelImport(binding)} dismiss={() => actions.dismissImport(binding)} />}
            {oversized && (
                <Alert variant="warn" role="status" data-testid="scale-warning">
                    <AlertTriangle />
                    <AlertTitle>{t("binding.scaleTitle")}</AlertTitle>
                    <AlertDescription>
                        {t("binding.scaleText", { width: inputSize.width, height: inputSize.height })}
                    </AlertDescription>
                </Alert>
            )}
            <AssetGrid key={binding.id} projectId={projectId} classVariantId={binding.id} label={label} busy={busy} remove={actions.removeAsset} />
        </section>
    );
}

function ImportStatus({ state, cancel, dismiss }: { state: AssetImport; cancel: () => void; dismiss: () => void }) {
    const { t } = useTranslation("catalog");
    const job = state.job;
    if (state.selecting && !job) {
        return <p role="status" className="text-sm text-text-2">{t("import.selecting")}</p>;
    }
    if (!job) {
        return state.error ? <ImportError message={state.error} dismiss={dismiss} /> : null;
    }
    if (isJobActive(job)) {
        const percent = job.total ? Math.round(((job.processed ?? 0) / job.total) * 100) : job.progress;
        return (
            <div role="status" data-testid="import-progress" className="space-y-2 rounded-lg border border-line bg-ink-2 p-3 text-sm">
                <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className="flex items-center gap-2">
                        <Loader2 className="size-4 animate-spin" aria-hidden />
                        {job.status === "queued" ? t("import.queued") : t("import.running")}
                        {job.total !== null && <span className="tabular-nums text-text-2">{t("import.progress", { processed: formatNumber(job.processed ?? 0), total: formatNumber(job.total) })}</span>}
                    </span>
                    <Button variant="outline" size="sm" disabled={job.cancelRequested} onClick={cancel}>
                        {job.cancelRequested ? t("import.cancelling") : t("import.cancel")}
                    </Button>
                </div>
                <Progress value={percent} live={job.status === "running"} aria-label={t("import.progressLabel")} />
                {state.error && <p className="text-destructive">{state.error}</p>}
            </div>
        );
    }
    const result = parseImportClassAssetsResult(job);
    const title = job.status === "succeeded" ? t("import.succeeded") : job.status === "cancelled" ? t("import.cancelled") : t("import.failed");
    // Rejected files unfold inline in the result, without a scroll of their own.
    return (
        <Notice variant={job.status === "succeeded" ? "default" : "destructive"} title={title} data-testid="import-result" onDismiss={dismiss}>
            {result && (
                <p className="tabular-nums">
                    {t("import.summary", { added: formatNumber(result.added), duplicates: formatNumber(result.duplicates), rejected: formatNumber(result.rejected) })}
                </p>
            )}
            {job.status !== "succeeded" && job.status !== "cancelled" && job.error && <p>{jobErrorText(job.error)}</p>}
            {!!result?.rejectedFiles.length && (
                <Collapsible title={<span className="text-xs font-normal">{t("import.rejectedFiles", { number: formatNumber(result.rejected) })}</span>} className="-mx-3 mt-1"
                    data-testid="import-rejected">
                    <ul className="list-disc space-y-0.5 pr-3 pb-1 pl-12 text-xs">
                        {result.rejectedFiles.map((file, index) => <li key={`${file.name}-${index}`} className="break-words">{file.name} — {errorText(file.reason)}</li>)}
                        {result.rejected > result.rejectedFiles.length && <li>{t("import.more", { number: formatNumber(result.rejected - result.rejectedFiles.length) })}</li>}
                    </ul>
                </Collapsible>
            )}
        </Notice>
    );
}

function ImportError({ message, dismiss }: { message: string; dismiss: () => void }) {
    return <Notice variant="destructive" onDismiss={dismiss}>{message}</Notice>;
}

function AssetGrid({ projectId, classVariantId, label, busy, remove }: {
    projectId: string;
    classVariantId: string;
    label: string;
    busy: boolean;
    remove: (asset: ClassAssetResponseDto, label: string) => void;
}) {
    const { t } = useTranslation("catalog");
    const assets = useClassAssets(projectId, classVariantId);
    const sentinel = useRef<HTMLDivElement>(null);
    const { loadMore } = assets;
    const hasMore = assets.nextCursor !== null;
    const [viewed, setViewed] = useState<{ id: string; pendingNext?: boolean }>();
    const viewedIndex = viewed ? assets.items.findIndex(asset => asset.id === viewed.id) : -1;
    const viewedAsset = viewedIndex >= 0 ? assets.items[viewedIndex] : undefined;

    useEffect(() => {
        // "Next" at the end of the loaded list loads a page and moves to its first image.
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
        if (assets.loading) return <p role="status" className="py-6 text-sm text-text-2">{t("assets.loading")}</p>;
        if (assets.error) return <GridError message={assets.error} retry={() => void assets.reload()} />;
        return (
            <p className="py-6 text-sm text-text-2">
                {t("assets.empty")}
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
                                className="relative block w-full rounded-md outline-none focus-visible:ring-[3px] focus-visible:ring-focus/30"
                                aria-label={t("assets.viewLabel", { name: `${label} · ${size}` })}
                                onClick={() => setViewed({ id: asset.id })}
                            >
                                <ResourceImage
                                    src={asset.thumbnailUrl}
                                    alt={`${label} · ${size}`}
                                    fit="contain"
                                    className="h-24 w-full rounded-md border border-line"
                                    onExpired={assets.renew}
                                />
                                {!asset.hasAlpha && (
                                    <span
                                        className="absolute top-1 left-1 rounded bg-black/70 px-1 py-0.5 text-[10px] leading-none text-warn"
                                        title={t("assets.noAlphaHint")}
                                    >
                                        {t("assets.noAlpha")}
                                    </span>
                                )}
                            </button>
                            <figcaption className="mt-1 text-center text-xs tabular-nums text-text-2">{size}</figcaption>
                            <Button
                                variant="ghost"
                                size="sm"
                                className="w-full text-destructive hover:text-destructive"
                                disabled={busy}
                                aria-label={t("assets.deleteLabel", { name: `${label} · ${size}` })}
                                onClick={() => remove(asset, `${label} · ${size}`)}
                            >
                                <Trash2 />{t("list.delete")}
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
                                {t("assets.meta", {
                                    format: viewedAsset.format.toUpperCase(),
                                    kilobytes: formatNumber(Math.ceil(viewedAsset.byteSize / 1024)),
                                    alpha: viewedAsset.hasAlpha ? t("assets.withAlpha") : t("assets.withoutAlpha"),
                                })}
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
                        {assets.loading ? t("assets.loadingMore") : t("assets.more")}
                    </Button>
                </div>
            )}
        </div>
    );
}

function GridError({ message, retry }: { message: string; retry: () => void }) {
    const { t } = useTranslation("catalog");
    return <Notice variant="destructive" action={{ label: t("section.retry"), onClick: retry }}>{message}</Notice>;
}
