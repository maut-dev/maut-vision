import { useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import "@/i18n/sections/checks";
import { versionOptionLabel } from "@/lib/checks/check-view";
import { useDatasetVersions } from "@/lib/use-dataset-versions";
import { useGpuInventory } from "@/lib/use-gpu-inventory";
import { useModelVersions } from "@/lib/use-model-versions";
import { CheckDataset } from "./check-dataset";
import { CheckImage } from "./check-image";
import { CheckScreen } from "./check-screen";
import { CheckVideo } from "./check-video";

type Mode = "image" | "dataset" | "video" | "screen";

const modes: readonly { id: Mode }[] = [{ id: "image" }, { id: "dataset" }, { id: "video" }, { id: "screen" }];

const tabId = (mode: Mode) => `checks-tab-${mode}`;
const panelId = (mode: Mode) => `checks-panel-${mode}`;

/**
 * The Checks section: the version (the working one by default) and the modes Image, Dataset, Video, Screen as a tablist; initialMode is
 * the mode of a navigation (an evaluation from the overview opens Dataset).
 * Opened modes stay mounted, so switching tabs does not break a check; leaving the section stops a screen check. No polling:
 * training.changed, datasets.changed, checks.changed, job.changed.
 */
export function ChecksSection({ project, initialMode, openModels }: { project: ProjectResponseDto; initialMode?: Mode; openModels: () => void }) {
    const { t } = useTranslation("checks");
    const projectId = project.id;
    const models = useModelVersions(projectId);
    const datasets = useDatasetVersions(projectId);
    const gpus = useGpuInventory();
    const [selectedId, setSelectedId] = useState<string>();
    const [mode, setMode] = useState<Mode>(initialMode ?? "image");
    const [opened, setOpened] = useState<ReadonlySet<Mode>>(() => new Set([initialMode ?? "image"]));
    const versions = [...models.versions].sort((left, right) => right.number - left.number);
    const version = versions.find(item => item.id === selectedId) ?? versions.find(item => item.isWorking) ?? versions[0];

    function select(next: Mode) {
        setMode(next);
        setOpened(current => current.has(next) ? current : new Set([...current, next]));
    }

    function onKeyDown(event: KeyboardEvent, index: number) {
        const last = modes.length - 1;
        const next = event.key === "ArrowRight" ? (index + 1) % modes.length
            : event.key === "ArrowLeft" ? (index + last) % modes.length
                : event.key === "Home" ? 0 : event.key === "End" ? last : null;
        if (next === null) return;
        event.preventDefault();
        select(modes[next].id);
        document.getElementById(tabId(modes[next].id))?.focus();
    }

    if (models.loaded && !versions.length) {
        return (
            <section className="space-y-3 rounded-lg border border-dashed p-6" data-testid="checks-empty">
                <h2 className="font-medium">{t("empty.title")}</h2>
                <p className="max-w-prose text-sm text-muted-foreground">{t("empty.text")}</p>
                <Button variant="outline" data-testid="checks-open-models" onClick={openModels}>{t("empty.openModels")}</Button>
            </section>
        );
    }

    return (
        <div className="space-y-5" data-testid="checks-section">
            {models.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{models.error}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => void models.refresh()}>{t("retry")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            <section className="space-y-4 rounded-lg border bg-card p-5">
                <div className="flex flex-wrap items-end justify-between gap-4">
                    <div className="space-y-1">
                        <span className="block text-xs text-muted-foreground">{t("version.label")}</span>
                        <Select value={version?.id ?? ""} disabled={!versions.length} onValueChange={next => { if (next) setSelectedId(next); }}>
                            <SelectTrigger aria-label={t("version.aria")} className="w-80" data-testid="checks-version">
                                <SelectValue placeholder={models.loaded ? t("version.none") : t("loading")} />
                            </SelectTrigger>
                            <SelectContent>
                                {versions.map(item => <SelectItem key={item.id} value={item.id}>{versionOptionLabel(item)}</SelectItem>)}
                            </SelectContent>
                        </Select>
                    </div>
                    <div role="tablist" aria-label={t("modeLabel")} className="flex flex-wrap gap-1 rounded-md border p-1">
                        {modes.map((item, index) => (
                            <Button
                                key={item.id}
                                id={tabId(item.id)}
                                role="tab"
                                size="sm"
                                data-testid={`checks-tab-${item.id}`}
                                tabIndex={mode === item.id ? 0 : -1}
                                aria-selected={mode === item.id}
                                aria-controls={panelId(item.id)}
                                variant={mode === item.id ? "secondary" : "ghost"}
                                onClick={() => select(item.id)}
                                onKeyDown={event => onKeyDown(event, index)}
                            >
                                {t(`modes.${item.id}`)}
                            </Button>
                        ))}
                    </div>
                </div>
                {version && (
                    <p className="text-xs text-muted-foreground" data-testid="checks-caption">
                        {t("caption", { number: version.number, width: version.input.width, height: version.input.height, classes: version.classes.length })}{" "}
                        <Button variant="link" size="sm" className="h-auto p-0 text-xs" data-testid="checks-models-link" onClick={openModels}>{t("allVersions")}</Button>
                    </p>
                )}
            </section>
            {version && modes.map(item => opened.has(item.id) && (
                <div key={item.id} id={panelId(item.id)} role="tabpanel" aria-labelledby={tabId(item.id)} hidden={mode !== item.id}>
                    {item.id === "image" && <CheckImage projectId={projectId} version={version} gpus={gpus} />}
                    {item.id === "dataset" && <CheckDataset projectId={projectId} versions={versions} version={version} datasets={datasets.versions} gpus={gpus} />}
                    {item.id === "video" && <CheckVideo projectId={projectId} version={version} gpus={gpus} />}
                    {item.id === "screen" && <CheckScreen projectId={projectId} version={version} gpus={gpus} />}
                </div>
            ))}
        </div>
    );
}
