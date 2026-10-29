import { useState } from "react";
import { useTranslation } from "react-i18next";
import { classesApi } from "@/api/projects/catalog/classes-api";
import type { ClassResponseDto } from "@/api/projects/catalog/response-dto/class-response-dto";
import type { DeletionResultResponseDto } from "@/api/projects/catalog/response-dto/deletion-review-response-dto";
import type { VariantResponseDto } from "@/api/projects/catalog/response-dto/variant-response-dto";
import { variantsApi } from "@/api/projects/catalog/variants-api";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { errorMessage } from "@/lib/format";
import { useAssetImports } from "@/lib/use-asset-imports";
import { useCatalog } from "@/lib/use-catalog";
import { ArchiveConfirmDialog, AttachVariantDialog, CatalogEntryDialog, type EntryValues } from "./catalog-dialogs";
import { CatalogDeletionDialog, type DeletionTarget } from "./catalog-deletion-dialog";
import { ClassesPage } from "./classes-page";
import type { ClassVariantActions } from "./class-variant-section";
import { VariantsPage } from "./variants-page";
import "@/i18n/sections/catalog";

type Editor =
    | { kind: "variant"; entry?: VariantResponseDto }
    | { kind: "class"; entry?: ClassResponseDto }
    | { kind: "attach"; classId: string };

interface Notice {
    text: string;
    tone: "info" | "error";
}

interface ArchiveRequest {
    name: string;
    run: () => Promise<unknown>;
}

/** The state on screen is stale: the snapshot is reread, and the message tells the user. */
const conflictCodes = /\.(revision_conflict|order_revision_conflict)$/;
const staleCodes = /\.(not_found|archived|variant_archived|class_unavailable|variant_unavailable|project_variant_unavailable|duplicate_variant_binding)$/;

/**
 * The "Variants" and "Classes" sections of one project: a shared catalog snapshot, image imports and dialogs.
 * Switching between the sections does not reread the catalog; each section keeps its own search and archive toggle.
 */
export function CatalogSection({ project, page, openVariants }: {
    project: ProjectResponseDto;
    page: "variants" | "classes";
    openVariants: () => void;
}) {
    const { t } = useTranslation("catalog");
    const projectId = project.id;
    const catalog = useCatalog(projectId);
    const importer = useAssetImports(projectId);
    const [busy, setBusy] = useState(false);
    const [notice, setNotice] = useState<Notice>();
    const [editor, setEditor] = useState<Editor>();
    const [editorError, setEditorError] = useState<string>();
    const [archive, setArchive] = useState<ArchiveRequest>();
    const [archiveError, setArchiveError] = useState<string>();
    const [deletion, setDeletion] = useState<DeletionTarget>();
    const workspace = catalog.workspace;

    function describe(cause: unknown): string {
        const message = errorMessage(cause);
        if (cause instanceof BridgeError && conflictCodes.test(cause.code)) {
            void catalog.refresh();
            return t("section.listUpdated", { message });
        }
        if (cause instanceof BridgeError && staleCodes.test(cause.code)) void catalog.refresh();
        return message;
    }

    /** A synchronous catalog operation: buttons are disabled until the answer, an error goes where it will be seen. */
    async function mutate(action: () => Promise<void>, report: (message: string) => void): Promise<boolean> {
        setBusy(true);
        try {
            await action();
            return true;
        } catch (cause) {
            report(describe(cause));
            return false;
        } finally {
            setBusy(false);
        }
    }

    const showError = (text: string) => setNotice({ text, tone: "error" });

    function openEditor(next: Editor) {
        setEditorError(undefined);
        setNotice(undefined);
        setEditor(next);
    }

    function saveEntry(values: EntryValues) {
        if (!editor || editor.kind === "attach") return;
        void mutate(async () => {
            if (editor.kind === "variant") {
                catalog.applyVariant(editor.entry
                    ? await variantsApi.update({ projectId, variantId: editor.entry.id, revision: editor.entry.revision, ...values })
                    : await variantsApi.create({ projectId, ...values }));
            } else {
                catalog.applyClass(editor.entry
                    ? await classesApi.update({ projectId, classId: editor.entry.id, revision: editor.entry.revision, ...values })
                    : await classesApi.create({ projectId, ...values }));
            }
            setEditor(undefined);
        }, setEditorError);
    }

    function attach(variantId: string) {
        if (editor?.kind !== "attach") return;
        void mutate(async () => {
            catalog.applyClassVariant(await classesApi.addVariant({ projectId, classId: editor.classId, variantId }));
            setEditor(undefined);
        }, setEditorError);
    }

    /** Archiving after a confirmation, restoring at once. */
    function toggleArchive(name: string, archived: boolean, run: (archive: boolean) => Promise<void>) {
        setNotice(undefined);
        if (archived) {
            void mutate(() => run(false), showError);
            return;
        }
        setArchiveError(undefined);
        setArchive({ name, run: () => run(true) });
    }

    function confirmArchive() {
        const request = archive;
        if (!request) return;
        void mutate(async () => {
            await request.run();
            setArchive(undefined);
        }, setArchiveError);
    }

    function deleted(result: DeletionResultResponseDto) {
        setDeletion(undefined);
        setNotice(result.cleanupPending
            ? { tone: "info", text: t("section.cleanupPending") }
            : undefined);
        void catalog.refresh();
    }

    function remove(target: DeletionTarget) {
        setNotice(undefined);
        setDeletion(target);
    }

    const bindingActions: ClassVariantActions = {
        setColor: (binding, color) => {
            // The color is saved in the background without blocking the screen; an error is shown as a message.
            classesApi.setVariantColor({ projectId, classVariantId: binding.id, revision: binding.revision, color })
                .then(catalog.applyClassVariant, cause => showError(describe(cause)));
        },
        toggleArchive: (binding, label) => toggleArchive(label, !!binding.archivedAt, async archived => {
            catalog.applyClassVariant(await classesApi.setVariantArchived({ projectId, classVariantId: binding.id, revision: binding.revision, archived }));
        }),
        remove: (binding, label) => remove({ kind: "classVariant", id: binding.id, name: label }),
        removeAsset: (asset, label) => remove({ kind: "asset", id: asset.id, name: label }),
        startImport: binding => void importer.start(binding.id),
        cancelImport: binding => void importer.cancel(binding.id),
        dismissImport: binding => importer.dismiss(binding.id),
    };

    const editorTitle = editor?.kind === "variant"
        ? editor.entry ? t("section.editVariant") : t("section.newVariant")
        : editor?.kind === "class" && editor.entry ? t("section.editClass") : t("section.newClass");

    return (
        <div className="flex min-h-0 flex-1 flex-col gap-4" data-testid={`${page}-section`}>
            {notice && (
                <Alert variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="catalog-notice">
                    <AlertDescription>
                        <p>{notice.text}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => setNotice(undefined)}>{t("section.hide")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            {!workspace && catalog.loading && <p role="status" className="text-sm text-muted-foreground">{t("section.loading")}</p>}
            {!workspace && !catalog.loading && catalog.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{catalog.error}</p>
                        <Button variant="outline" size="sm" className="mt-2" onClick={() => void catalog.refresh()}>{t("section.retry")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            {workspace && page === "variants" && (
                <VariantsPage
                    variants={workspace.variants}
                    busy={busy}
                    actions={{
                        create: () => openEditor({ kind: "variant" }),
                        edit: entry => openEditor({ kind: "variant", entry }),
                        toggleArchive: entry => toggleArchive(entry.name, !!entry.archivedAt, async archived => {
                            catalog.applyVariant(await variantsApi.setArchived({ projectId, variantId: entry.id, revision: entry.revision, archived }));
                        }),
                        remove: entry => remove({ kind: "variant", id: entry.id, name: entry.name }),
                    }}
                />
            )}
            {workspace && page === "classes" && (
                <ClassesPage
                    projectId={projectId}
                    workspace={workspace}
                    inputSize={project.inputSize}
                    busy={busy}
                    imports={importer.imports}
                    actions={{
                        create: () => openEditor({ kind: "class" }),
                        edit: entry => openEditor({ kind: "class", entry }),
                        toggleArchive: entry => toggleArchive(entry.name, !!entry.archivedAt, async archived => {
                            catalog.applyClass(await classesApi.setArchived({ projectId, classId: entry.id, revision: entry.revision, archived }));
                        }),
                        remove: entry => remove({ kind: "class", id: entry.id, name: entry.name }),
                        reorder: classIds => {
                            setNotice(undefined);
                            return mutate(async () => {
                                catalog.applyOrder(await classesApi.reorder({ projectId, classIds, orderRevision: workspace.classOrderRevision }));
                            }, showError);
                        },
                        attach: entry => openEditor({ kind: "attach", classId: entry.id }),
                        openVariants,
                        binding: bindingActions,
                    }}
                />
            )}
            {editor && editor.kind !== "attach" && (
                <CatalogEntryDialog
                    title={editorTitle}
                    description={editor.kind === "class"
                        ? t("section.classDescription")
                        : t("section.variantDescription")}
                    initial={editor.entry}
                    busy={busy}
                    error={editorError}
                    close={() => setEditor(undefined)}
                    save={saveEntry}
                />
            )}
            {editor?.kind === "attach" && workspace && (
                <AttachVariantDialog
                    variants={workspace.variants.filter(variant => !variant.archivedAt
                        && !workspace.classVariants.some(item => item.classId === editor.classId && item.variantId === variant.id))}
                    busy={busy}
                    error={editorError}
                    close={() => setEditor(undefined)}
                    save={attach}
                />
            )}
            {archive && (
                <ArchiveConfirmDialog
                    name={archive.name}
                    busy={busy}
                    error={archiveError}
                    close={() => setArchive(undefined)}
                    confirm={confirmArchive}
                />
            )}
            {deletion && (
                <CatalogDeletionDialog projectId={projectId} target={deletion} close={() => setDeletion(undefined)} deleted={deleted} />
            )}
        </div>
    );
}
