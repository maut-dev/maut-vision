import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { EmptyState } from "@/components/layout/empty-state";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { SectionLayout } from "@/components/layout/section-layout";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/layout/tabs";
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
 * training.changed, datasets.changed, checks.changed, job.changed. The section fits the window: the version stays above the tabs, and each
 * mode is one scroll with its form, results and journal.
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

    if (models.loaded && !versions.length) {
        return (
            <EmptyState title={t("empty.title")} description={t("empty.text")} className="min-h-40 rounded-lg border border-dashed border-line" data-testid="checks-empty"
                action={<Button variant="outline" data-testid="checks-open-models" onClick={openModels}>{t("empty.openModels")}</Button>} />
        );
    }

    return (
        <SectionLayout data-testid="checks-section">
            {models.error && (
                <Notice variant="destructive" className="shrink-0" action={{ label: t("retry"), onClick: () => void models.refresh() }}>{models.error}</Notice>
            )}
            <Tabs value={mode} onValueChange={next => select(next as Mode)} className="flex-1">
                <div className="flex shrink-0 flex-wrap items-end justify-between gap-x-4 gap-y-2">
                    <div className="min-w-0 space-y-1">
                        <span className="block text-xs text-text-2">{t("version.label")}</span>
                        <Select value={version?.id ?? ""} disabled={!versions.length} onValueChange={next => { if (next) setSelectedId(next); }}>
                            <SelectTrigger aria-label={t("version.aria")} className="w-80 max-w-full" data-testid="checks-version">
                                <SelectValue placeholder={models.loaded ? t("version.none") : t("loading")} />
                            </SelectTrigger>
                            <SelectContent>
                                {versions.map(item => <SelectItem key={item.id} value={item.id}>{versionOptionLabel(item)}</SelectItem>)}
                            </SelectContent>
                        </Select>
                    </div>
                    <TabsList aria-label={t("modeLabel")}>
                        {modes.map(item => (
                            <TabsTrigger key={item.id} value={item.id} id={tabId(item.id)} aria-controls={panelId(item.id)} data-testid={`checks-tab-${item.id}`}>
                                {t(`modes.${item.id}`)}
                            </TabsTrigger>
                        ))}
                    </TabsList>
                </div>
                {version && (
                    <p className="shrink-0 text-xs text-text-2" data-testid="checks-caption">
                        {t("caption", { number: version.number, width: version.input.width, height: version.input.height, classes: version.classes.length })}{" "}
                        <Button variant="link" size="sm" className="h-auto p-0 text-xs" data-testid="checks-models-link" onClick={openModels}>{t("allVersions")}</Button>
                    </p>
                )}
                {version && modes.map(item => opened.has(item.id) && (
                    <TabsContent key={item.id} value={item.id} id={panelId(item.id)} aria-labelledby={tabId(item.id)} forceMount hidden={mode !== item.id} className="flex flex-col">
                        <Pane className="flex-1">
                            {item.id === "image" && <CheckImage projectId={projectId} version={version} gpus={gpus} />}
                            {item.id === "dataset" && <CheckDataset projectId={projectId} versions={versions} version={version} datasets={datasets.versions} gpus={gpus} />}
                            {item.id === "video" && <CheckVideo projectId={projectId} version={version} gpus={gpus} />}
                            {item.id === "screen" && <CheckScreen projectId={projectId} version={version} gpus={gpus} />}
                        </Pane>
                    </TabsContent>
                ))}
            </Tabs>
        </SectionLayout>
    );
}
