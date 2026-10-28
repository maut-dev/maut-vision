import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Check, Download, Loader2, Search, Trash2, X } from "lucide-react";
import type { BaseModelResponseDto } from "@/api/base-models/response-dto/base-model-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
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
        return <span className="flex items-center gap-1.5 text-sm"><Check className="size-4 text-brand" aria-hidden />{t("baseModels.installed")}</span>;
    }
    if (model.status === "installing") {
        const percent = Math.min(100, Math.floor(model.progress));
        return (
            <div className="w-full space-y-1.5 sm:w-48" data-testid="base-model-progress">
                <div className="flex justify-between gap-3 text-xs tabular-nums text-muted-foreground">
                    <span>{t("baseModels.downloaded", { done: megabytes(model.byteSize * model.progress / 100), total: megabytes(model.byteSize) })}</span>
                    <span>{percent}%</span>
                </div>
                <div className="h-1.5 w-full overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={t("baseModels.installing", { model: model.id })} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
                    <div className="h-full rounded-full bg-brand transition-[width] duration-300" style={{ width: `${model.progress}%` }} />
                </div>
            </div>
        );
    }
    return <span className="text-sm text-muted-foreground">{model.status === "failed" ? t("baseModels.failed") : t("baseModels.notInstalled")}</span>;
}

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
                    <h1 id="catalog-heading" className="text-2xl font-semibold">{t("app.baseModels")}</h1>
                    <p className="mt-2 max-w-prose text-sm text-muted-foreground">
                        {t("baseModels.subtitle")}
                    </p>
                </div>
                <Button variant="outline" onClick={back}>{t("app.allProjects")}</Button>
            </div>
            <p className="text-sm text-muted-foreground">
                {t("baseModels.about")}
            </p>
            <div className="flex flex-wrap items-center gap-3">
                <div className="relative min-w-48 flex-1">
                    <Search className="absolute left-3 top-2.5 size-4 text-muted-foreground" aria-hidden />
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
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{catalog.error}</p>
                        <Button type="button" variant="outline" size="sm" className="mt-2" onClick={() => void catalog.refresh()}>{t("common.retryLoading")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            {catalog.loading && !catalog.models.length ? (
                <div className="flex items-center gap-2 py-8 text-muted-foreground" role="status">
                    <Loader2 className="size-4 animate-spin" aria-hidden />{t("baseModels.loading")}
                </div>
            ) : (
                <div className="divide-y rounded-lg border" data-testid="base-model-list">
                    {visible.map(model => {
                        const pending = catalog.pending.includes(model.id);
                        return (
                            <div key={model.id} className="space-y-3 p-4" data-model-id={model.id} data-status={model.status}>
                                <div className="flex flex-wrap items-center justify-between gap-4">
                                    <div className="min-w-32">
                                        <div className="flex items-center gap-2">
                                            <h2 className="font-medium">{model.id}</h2>
                                            {model.stride === 64 && <Badge variant="outline">P6</Badge>}
                                        </div>
                                        <p className="mt-1 text-xs tabular-nums text-muted-foreground">{model.family} · {megabytes(model.byteSize)}</p>
                                    </div>
                                    <div className="flex w-full flex-wrap items-center justify-between gap-3 sm:w-auto sm:justify-end">
                                        <InstallationStatus model={model} />
                                        {model.status === "installed" ? (
                                            <>
                                                <Button type="button" size="sm" variant="outline" onClick={() => createProject(model.id)}>{t("library.create")}</Button>
                                                <Button type="button" size="icon-sm" variant="ghost" disabled={pending} onClick={() => void catalog.remove(model.id)} aria-label={t("baseModels.deleteLabel", { model: model.id })} title={t("baseModels.deleteHint")}>
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
                                {model.error && <p role="status" className="max-w-prose text-sm text-destructive">{errorText(model.error.code)}</p>}
                            </div>
                        );
                    })}
                    {!visible.length && (
                        <div className="space-y-2 p-8 text-center">
                            <p className="font-medium">{installedOnly ? t("baseModels.noneInstalled") : t("baseModels.notFound")}</p>
                            <p className="text-sm text-muted-foreground">
                                {installedOnly ? t("baseModels.noneInstalledHint") : t("baseModels.notFoundHint")}
                            </p>
                        </div>
                    )}
                </div>
            )}
        </section>
    );
}
