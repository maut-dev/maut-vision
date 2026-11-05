import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ArrowDown, ArrowUp, Plus, Tags } from "lucide-react";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { ClassResponseDto } from "@/api/projects/catalog/response-dto/class-response-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import { EmptyState } from "@/components/layout/empty-state";
import { MasterDetail } from "@/components/layout/master-detail";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type { AssetImportsState } from "@/lib/use-asset-imports";
import { CatalogEmpty, CatalogRow, CatalogToolbar, filterEntries } from "./catalog-list";
import { ClassVariantSection, type ClassVariantActions } from "./class-variant-section";

export interface ClassActions {
    create: () => void;
    edit: (item: ClassResponseDto) => void;
    toggleArchive: (item: ClassResponseDto) => void;
    remove: (item: ClassResponseDto) => void;
    /** A new full order: active classes in the new order, then archived ones. */
    reorder: (classIds: string[]) => Promise<boolean>;
    attach: (item: ClassResponseDto) => void;
    openVariants: () => void;
    binding: ClassVariantActions;
}

/**
 * The "Classes" section: the toolbar and a short order hint above two columns: the ordered list and the chosen class (a compact header
 * with its name and "Attach variant", then its variants with their images in one scroll).
 */
export function ClassesPage({ projectId, workspace, inputSize, busy, imports, actions }: {
    projectId: string;
    workspace: CatalogWorkspaceResponseDto;
    inputSize: InputSizeDto;
    busy: boolean;
    imports: AssetImportsState["imports"];
    actions: ClassActions;
}) {
    const { t } = useTranslation("catalog");
    const [search, setSearch] = useState("");
    const [showArchive, setShowArchive] = useState(false);
    const [classId, setClassId] = useState("");
    const list = useRef<HTMLDivElement>(null);
    const refocus = useRef<{ id: string; direction: "up" | "down" }>(undefined);

    const classes = workspace.classes;
    const active = classes.filter(item => !item.archivedAt);
    const indices = new Map(active.map((item, index) => [item.id, index]));
    const activeVariants = workspace.variants.filter(item => !item.archivedAt);
    const entries = filterEntries(classes, search, showArchive);
    const searching = !!search.trim();
    const selected = entries.find(item => item.id === classId) ?? entries[0];
    const bindings = selected
        ? workspace.classVariants.filter(item => item.classId === selected.id && (showArchive || !item.archivedAt))
        : [];
    const attachable = selected
        ? activeVariants.filter(variant => !workspace.classVariants.some(item => item.classId === selected.id && item.variantId === variant.id))
        : [];

    useEffect(() => {
        // After a move the row changes its place; keyboard focus stays on the same arrow, or the other one at the edge.
        const target = refocus.current;
        if (!target || busy) return;
        refocus.current = undefined;
        const row = list.current?.querySelector(`[data-class-id="${target.id}"]`);
        const button = row?.querySelector<HTMLButtonElement>(`[data-move="${target.direction}"]:not(:disabled)`)
            ?? row?.querySelector<HTMLButtonElement>("[data-move]:not(:disabled)");
        button?.focus();
    }, [classes, busy]);

    async function move(item: ClassResponseDto, offset: -1 | 1) {
        const ids = active.map(entry => entry.id);
        const index = ids.indexOf(item.id);
        const destination = index + offset;
        if (index < 0 || destination < 0 || destination >= ids.length) return;
        [ids[index], ids[destination]] = [ids[destination], ids[index]];
        const archived = classes.filter(entry => entry.archivedAt).map(entry => entry.id);
        refocus.current = { id: item.id, direction: offset < 0 ? "up" : "down" };
        if (!await actions.reorder([...ids, ...archived])) refocus.current = undefined;
    }

    const orderHint = searching ? t("classes.orderLocked") : undefined;
    return (
        <>
            <CatalogToolbar
                searchLabel={t("classes.search")}
                search={search}
                setSearch={setSearch}
                showArchive={showArchive}
                setShowArchive={setShowArchive}
                addLabel={t("classes.add")}
                addDisabled={busy || !activeVariants.length}
                add={actions.create}
            />
            {!activeVariants.length && (
                <Notice className="shrink-0" title={t("classes.registerVariants")} action={{ label: t("classes.openVariants"), onClick: actions.openVariants }}>
                    {t("classes.registerVariantsText")}
                </Notice>
            )}
            <p className="shrink-0 text-xs text-text-2">{t("classes.orderHelp")}</p>
            <MasterDetail className="grid-cols-[minmax(0,13rem)_minmax(0,1fr)] lg:grid-cols-[minmax(16rem,30%)_minmax(0,1fr)]">
                <Pane
                    ref={list}
                    surface
                    className="p-2 outline-none focus-visible:ring-[3px] focus-visible:ring-focus/30"
                    data-testid="class-list"
                    tabIndex={0}
                    role="region"
                    aria-label={t("classes.list")}
                >
                    <div className="space-y-1">
                        {entries.map(item => {
                            const index = indices.get(item.id);
                            return (
                                <CatalogRow
                                    key={item.id}
                                    data-class-id={item.id}
                                    entry={item}
                                    deleteLabel={t("classes.deleteLabel", { name: item.name })}
                                    selected={item.id === selected?.id}
                                    prefix={index !== undefined && (
                                        <span className="mr-2 text-xs tabular-nums text-text-3" data-class-index={index} title={t("classes.indexHint")}>{index}</span>
                                    )}
                                    fallback={t("classes.fallback")}
                                    busy={busy}
                                    open={() => setClassId(item.id)}
                                    edit={() => actions.edit(item)}
                                    toggleArchive={() => actions.toggleArchive(item)}
                                    remove={() => actions.remove(item)}
                                    extra={index !== undefined && (
                                        <div className="flex shrink-0 flex-col">
                                            <Button
                                                size="icon-xs"
                                                variant="ghost"
                                                data-move="up"
                                                aria-label={t("classes.upLabel", { name: item.name })}
                                                title={orderHint ?? t("classes.up")}
                                                disabled={busy || searching || index === 0}
                                                onClick={() => void move(item, -1)}
                                            >
                                                <ArrowUp />
                                            </Button>
                                            <Button
                                                size="icon-xs"
                                                variant="ghost"
                                                data-move="down"
                                                aria-label={t("classes.downLabel", { name: item.name })}
                                                title={orderHint ?? t("classes.down")}
                                                disabled={busy || searching || index === active.length - 1}
                                                onClick={() => void move(item, 1)}
                                            >
                                                <ArrowDown />
                                            </Button>
                                        </div>
                                    )}
                                />
                            );
                        })}
                    </div>
                    {!entries.length && (
                        <CatalogEmpty
                            searching={searching || classes.length > 0}
                            title={t("classes.emptyTitle")}
                            text={t("classes.emptyText")}
                        />
                    )}
                </Pane>
                <section
                    className="flex min-h-0 min-w-0 flex-col"
                    data-testid="class-content"
                    aria-label={selected ? t("classes.contentLabel", { name: selected.name }) : t("classes.content")}
                >
                    {selected ? (
                        <div className="flex min-h-0 flex-1 flex-col gap-3" data-selected-class-id={selected.id}>
                            <div className="flex shrink-0 flex-wrap items-center justify-between gap-x-3 gap-y-2">
                                <div className="min-w-0">
                                    <h2 className="flex flex-wrap items-center gap-2 font-medium break-words">
                                        {selected.name}
                                        {selected.archivedAt && <Badge variant="outline">{t("list.archived")}</Badge>}
                                    </h2>
                                    <p className="mt-0.5 text-xs break-words text-text-2">
                                        {selected.description || t("classes.descriptionFallback")}
                                    </p>
                                </div>
                                <Button size="sm" variant="outline" data-testid="attach-variant-open" disabled={busy || !!selected.archivedAt || !attachable.length}
                                    onClick={() => actions.attach(selected)}>
                                    <Plus />{t("classes.attach")}
                                </Button>
                            </div>
                            <Pane surface tabIndex={0} role="region" aria-label={t("classes.variants")}
                                className="flex-1 space-y-4 p-4 outline-none focus-visible:ring-[3px] focus-visible:ring-focus/30">
                                {bindings.map(binding => {
                                    const variant = workspace.variants.find(item => item.id === binding.variantId);
                                    return (
                                        <ClassVariantSection
                                            key={binding.id}
                                            projectId={projectId}
                                            binding={binding}
                                            className={selected.name}
                                            variantName={variant?.name ?? t("classes.variantFallback")}
                                            classArchived={!!selected.archivedAt}
                                            variantArchived={!!variant?.archivedAt}
                                            inputSize={inputSize}
                                            busy={busy}
                                            importState={imports[binding.id]}
                                            actions={actions.binding}
                                        />
                                    );
                                })}
                                {!bindings.length && <EmptyState title={t("classes.noBindings")} />}
                            </Pane>
                        </div>
                    ) : (
                        <EmptyState icon={Tags} title={t("classes.chooseTitle")} description={t("classes.chooseText")}
                            className="flex-1 rounded-lg border border-dashed border-line" />
                    )}
                </section>
            </MasterDetail>
        </>
    );
}
