import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Check, Download, Loader2, Search, Trash2, X } from "lucide-react";
import type { BaseModelResponseDto } from "@/api/base-models/response-dto/base-model-response-dto";
import { EmptyState } from "@/components/layout/empty-state";
import { Notice } from "@/components/layout/notice";
import { Progress } from "@/components/layout/progress";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { errorText } from "@/lib/errors";
import { locale, megabytes } from "@/lib/format";
import type { BaseModelsState } from "@/lib/use-base-models";

function InstallationStatus({ model }: { model: BaseModelResponseDto }) {
    const { t } = useTranslation();
    if (model.status === "installed") {
        return <span className="flex items-center gap-1.5 text-sm text-ok"><Check className="size-4" aria-hidden />{t("baseModels.installed")}</span>;
    }
    if (model.status === "installing") {
        const percent = Math.min(100, Math.floor(model.progress));
        return (
            <div className="w-full space-y-1.5 sm:w-48" data-testid="base-model-progress">
                <div className="flex justify-between gap-3 text-xs tabular-nums text-text-2">
                    <span>{t("baseModels.downloaded", { done: megabytes(model.byteSize * model.progress / 100), total: megabytes(model.byteSize) })}</span>
                    <span>{percent}%</span>
                </div>
                <Progress value={model.progress} live aria-label={t("baseModels.installing", { model: model.id })} />
            </div>
        );
    }
    return <span className={`text-sm ${model.status === "failed" ? "text-danger" : "text-text-2"}`}>{model.status === "failed" ? t("baseModels.failed") : t("baseModels.notInstalled")}</span>;
}

/**
 * The catalogue of base models: search, the family and "Installed" filters, a row per model with its size, installation progress and
 * actions. It scrolls with the page; no row action is primary, so the screen has no brand button.
 */
export function BaseModelCatalog({ catalog, back, createProject }: {
    catalog: BaseModelsState;
    back: () => void;
    createProject: (modelId: string) => void;
}) {
    const { t } = useTranslation();
    const [search, setSearch] = useState("");
    const [family, setFamily] = useState("all");
    const [installedOnly, setInstalledOnly] = useState(false);
    const families = [...new Set(catalog.models.map(model => model.family))];
    const installedCount = catalog.models.filter(model => model.status === "installed").length;
    const query = search.trim().toLocaleLowerCase(locale());
    const visible = catalog.models.filter(model =>
        (family === "all" || model.family === family)
        && (!installedOnly || model.status === "installed")
        && model.id.toLocaleLowerCase(locale()).includes(query));
    return (
        <section className="space-y-5" aria-labelledby="catalog-heading">
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                    <h1 id="catalog-heading" className="text-2xl font-semibold tracking-[-0.025em]">{t("app.baseModels")}</h1>
                    <p className="mt-2 max-w-prose text-sm text-text-2">
                        {t("baseModels.subtitle")}
                    </p>
                </div>
                <Button variant="outline" onClick={back}>{t("app.allProjects")}</Button>
            </div>
            <p className="text-sm text-text-2">
                {t("baseModels.about")}
            </p>
            <div className="flex flex-wrap items-center gap-3">
                <div className="relative min-w-48 flex-1">
                    <Search className="absolute top-2.5 left-3 size-4 text-text-3" aria-hidden />
                    <Input aria-label={t("baseModels.searchLabel")} placeholder={t("baseModels.searchPlaceholder")} value={search} onChange={event => setSearch(event.target.value)} className="pl-9" />
                </div>
                <Select value={family} onValueChange={setFamily}>
                    <SelectTrigger aria-label={t("baseModels.family")} className="w-40"><SelectValue /></SelectTrigger>
                    <SelectContent>
                        <SelectItem value="all">{t("baseModels.allFamilies")}</SelectItem>
                        {families.map(item => <SelectItem key={item} value={item}>{item}</SelectItem>)}
                    </SelectContent>
                </Select>
                <Button type="button" variant={installedOnly ? "secondary" : "outline"} aria-pressed={installedOnly} onClick={() => setInstalledOnly(value => !value)}>
                    {t("baseModels.installedFilter", { count: installedCount })}
                </Button>
            </div>
            {catalog.error && (
                <Notice variant="destructive" action={{ label: t("common.retryLoading"), onClick: () => void catalog.refresh() }}>{catalog.error}</Notice>
            )}
            {catalog.loading && !catalog.models.length ? (
                <div className="flex items-center gap-2 py-8 text-text-2" role="status">
                    <Loader2 className="size-4 animate-spin" aria-hidden />{t("baseModels.loading")}
                </div>
            ) : (
                <div className="surface divide-y divide-line" data-testid="base-model-list">
                    {visible.map(model => {
                        const pending = catalog.pending.includes(model.id);
                        return (
                            <div key={model.id} className="space-y-3 px-4 py-3" data-model-id={model.id} data-status={model.status}>
                                <div className="flex flex-wrap items-center justify-between gap-4">
                                    <div className="min-w-32">
                                        <div className="flex items-center gap-2">
                                            <h2 className="text-[15px] font-semibold tracking-[-0.015em]">{model.id}</h2>
                                            {model.stride === 64 && <Badge variant="plain">P6</Badge>}
                                        </div>
                                        <p className="mt-1 text-xs tabular-nums text-text-2">{model.family} · {megabytes(model.byteSize)}</p>
                                    </div>
                                    <div className="flex w-full flex-wrap items-center justify-between gap-3 sm:w-auto sm:justify-end">
                                        <InstallationStatus model={model} />
                                        {model.status === "installed" ? (
                                            <>
                                                <Button type="button" size="sm" variant="outline" onClick={() => createProject(model.id)}>{t("library.create")}</Button>
                                                <Button type="button" size="icon-sm" variant="ghost" className="text-danger hover:text-danger" disabled={pending} onClick={() => void catalog.remove(model.id)} aria-label={t("baseModels.deleteLabel", { model: model.id })} title={t("baseModels.deleteHint")}>
                                                    <Trash2 />
                                                </Button>
                                            </>
                                        ) : model.status === "installing" ? (
                                            <Button type="button" size="sm" variant="ghost" disabled={pending} onClick={() => void catalog.cancel(model.id)} aria-label={t("baseModels.cancelLabel", { model: model.id })}>
                                                <X />{t("common.cancelAction")}
                                            </Button>
                                        ) : (
                                            <Button type="button" size="sm" variant="outline" disabled={pending} onClick={() => void catalog.install(model.id)} aria-label={t("baseModels.installLabel", { model: model.id })}>
                                                {pending ? <Loader2 className="animate-spin" /> : <Download />}
                                                {model.status === "failed" ? t("baseModels.retry") : t("baseModels.install")}
                                            </Button>
                                        )}
                                    </div>
                                </div>
                                {model.error && <p role="status" className="max-w-prose text-sm text-danger">{errorText(model.error.code)}</p>}
                            </div>
                        );
                    })}
                    {!visible.length && (
                        <EmptyState title={installedOnly ? t("baseModels.noneInstalled") : t("baseModels.notFound")}
                            description={installedOnly ? t("baseModels.noneInstalledHint") : t("baseModels.notFoundHint")} />
                    )}
                </div>
            )}
        </section>
    );
}
