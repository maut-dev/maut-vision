import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ArrowDown, ArrowUp, Plus, Tags } from "lucide-react";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { ClassResponseDto } from "@/api/projects/catalog/response-dto/class-response-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
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

/** The "Classes" section: the ordered list and the contents of the chosen class by variants. */
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
                <Alert className="shrink-0">
                    <Tags />
                    <AlertTitle>{t("classes.registerVariants")}</AlertTitle>
                    <AlertDescription>
                        <p>{t("classes.registerVariantsText")}</p>
                        <Button className="mt-3" variant="outline" onClick={actions.openVariants}>{t("classes.openVariants")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            <p className="shrink-0 text-sm text-muted-foreground">
                {t("classes.orderHelp")}
            </p>
            <div className="grid min-h-0 flex-1 grid-cols-[minmax(220px,30%)_minmax(0,1fr)] gap-5">
                <div
                    ref={list}
                    className="min-h-0 overflow-y-auto overscroll-contain rounded-lg border"
                    data-testid="class-list"
                    tabIndex={0}
                    role="region"
                    aria-label={t("classes.list")}
                >
                    <div className="divide-y">
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
                                        <span className="mr-2 text-xs tabular-nums text-muted-foreground" data-class-index={index} title={t("classes.indexHint")}>{index}</span>
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
                </div>
                <section
                    className="min-h-0 min-w-0 overflow-y-auto overscroll-contain rounded-lg border p-5"
                    data-testid="class-content"
                    tabIndex={0}
                    aria-label={selected ? t("classes.contentLabel", { name: selected.name }) : t("classes.content")}
                >
                    {selected ? (
                        <div className="space-y-5" data-selected-class-id={selected.id}>
                            <div className="flex flex-wrap items-center justify-between gap-3">
                                <div className="min-w-0">
                                    <h2 className="flex flex-wrap items-center gap-2 text-lg font-semibold break-words">
                                        {selected.name}
                                        {selected.archivedAt && <Badge variant="outline">{t("list.archived")}</Badge>}
                                    </h2>
                                    <p className="mt-1 text-sm break-words text-muted-foreground">
                                        {selected.description || t("classes.descriptionFallback")}
                                    </p>
                                </div>
                                <Button variant="outline" data-testid="attach-variant-open" disabled={busy || !!selected.archivedAt || !attachable.length} onClick={() => actions.attach(selected)}>
                                    <Plus />{t("classes.attach")}
                                </Button>
                            </div>
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
                            {!bindings.length && (
                                <p className="py-8 text-sm text-muted-foreground">{t("classes.noBindings")}</p>
                            )}
                        </div>
                    ) : (
                        <div className="flex min-h-80 flex-col items-center justify-center gap-2 text-center">
                            <Tags className="size-6 text-muted-foreground" aria-hidden />
                            <h2 className="font-medium">{t("classes.chooseTitle")}</h2>
                            <p className="max-w-sm text-sm text-muted-foreground">{t("classes.chooseText")}</p>
                        </div>
                    )}
                </section>
            </div>
        </>
    );
}
