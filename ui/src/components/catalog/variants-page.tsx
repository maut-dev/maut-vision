import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { VariantResponseDto } from "@/api/projects/catalog/response-dto/variant-response-dto";
import { CatalogEmpty, CatalogRow, CatalogToolbar, filterEntries } from "./catalog-list";

export interface VariantActions {
    create: () => void;
    edit: (variant: VariantResponseDto) => void;
    toggleArchive: (variant: VariantResponseDto) => void;
    remove: (variant: VariantResponseDto) => void;
}

/** The "Variants" section: the shared list of the ways objects of the project look. */
export function VariantsPage({ variants, busy, actions }: {
    variants: readonly VariantResponseDto[];
    busy: boolean;
    actions: VariantActions;
}) {
    const { t } = useTranslation("catalog");
    const [search, setSearch] = useState("");
    const [showArchive, setShowArchive] = useState(false);
    const entries = filterEntries(variants, search, showArchive);
    return (
        <>
            <CatalogToolbar
                searchLabel={t("variants.search")}
                search={search}
                setSearch={setSearch}
                showArchive={showArchive}
                setShowArchive={setShowArchive}
                addLabel={t("variants.add")}
                addDisabled={busy}
                add={actions.create}
            />
            <div
                className="min-h-0 flex-1 overflow-y-auto overscroll-contain rounded-lg border"
                data-testid="variant-list"
                tabIndex={0}
                role="region"
                aria-label={t("variants.list")}
            >
                <div className="divide-y">
                    {entries.map(variant => (
                        <CatalogRow
                            key={variant.id}
                            data-variant-id={variant.id}
                            entry={variant}
                            deleteLabel={t("variants.deleteLabel", { name: variant.name })}
                            fallback={t("variants.fallback")}
                            busy={busy}
                            open={() => actions.edit(variant)}
                            openDisabled={!!variant.archivedAt}
                            edit={() => actions.edit(variant)}
                            toggleArchive={() => actions.toggleArchive(variant)}
                            remove={() => actions.remove(variant)}
                        />
                    ))}
                </div>
                {!entries.length && (
                    <CatalogEmpty
                        searching={!!search.trim() || variants.length > 0}
                        title={t("variants.emptyTitle")}
                        text={t("variants.emptyText")}
                    />
                )}
            </div>
        </>
    );
}
