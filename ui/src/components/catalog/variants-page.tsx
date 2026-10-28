import { useState } from "react";
import type { VariantResponseDto } from "@/api/projects/catalog/response-dto/variant-response-dto";
import { CatalogEmpty, CatalogRow, CatalogToolbar, filterEntries } from "./catalog-list";

export interface VariantActions {
    create: () => void;
    edit: (variant: VariantResponseDto) => void;
    toggleArchive: (variant: VariantResponseDto) => void;
    remove: (variant: VariantResponseDto) => void;
}

/** Раздел «Варианты»: общий справочник способов отображения объектов проекта. */
export function VariantsPage({ variants, busy, actions }: {
    variants: readonly VariantResponseDto[];
    busy: boolean;
    actions: VariantActions;
}) {
    const [search, setSearch] = useState("");
    const [showArchive, setShowArchive] = useState(false);
    const entries = filterEntries(variants, search, showArchive);
    return (
        <>
            <CatalogToolbar
                searchLabel="Поиск вариантов"
                search={search}
                setSearch={setSearch}
                showArchive={showArchive}
                setShowArchive={setShowArchive}
                addLabel="Добавить вариант"
                addDisabled={busy}
                add={actions.create}
            />
            <div
                className="min-h-0 flex-1 overflow-y-auto overscroll-contain rounded-lg border"
                data-testid="variant-list"
                tabIndex={0}
                role="region"
                aria-label="Список вариантов"
            >
                <div className="divide-y">
                    {entries.map(variant => (
                        <CatalogRow
                            key={variant.id}
                            data-variant-id={variant.id}
                            entry={variant}
                            kindLabel="вариант"
                            fallback="Общий вариант проекта"
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
                        title="Вариантов пока нет"
                        text="Добавьте варианты отображения объектов. Их доли можно будет выбирать при генерации."
                    />
                )}
            </div>
        </>
    );
}
