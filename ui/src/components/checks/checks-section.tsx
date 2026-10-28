import { useState, type KeyboardEvent } from "react";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { versionOptionLabel } from "@/lib/checks/check-view";
import { useDatasetVersions } from "@/lib/use-dataset-versions";
import { useGpuInventory } from "@/lib/use-gpu-inventory";
import { useModelVersions } from "@/lib/use-model-versions";
import { CheckDataset } from "./check-dataset";
import { CheckImage } from "./check-image";
import { CheckScreen } from "./check-screen";
import { CheckVideo } from "./check-video";

type Mode = "image" | "dataset" | "video" | "screen";

const modes: readonly { id: Mode; label: string }[] = [
    { id: "image", label: "Изображение" },
    { id: "dataset", label: "Датасет" },
    { id: "video", label: "Видео" },
    { id: "screen", label: "Экран" },
];

const tabId = (mode: Mode) => `checks-tab-${mode}`;
const panelId = (mode: Mode) => `checks-panel-${mode}`;

/**
 * Раздел «Проверки»: выбор версии (по умолчанию рабочая) и режимы «Изображение», «Датасет», «Видео», «Экран» по шаблону tablist;
 * initialMode — режим при переходе (оценка из обзора открывает «Датасет»).
 * Открытые режимы остаются смонтированными, поэтому смена вкладки не обрывает проверку; уход из раздела останавливает проверку с
 * экрана. Без опроса: training.changed, datasets.changed, checks.changed, job.changed.
 */
export function ChecksSection({ project, initialMode, openModels }: { project: ProjectResponseDto; initialMode?: Mode; openModels: () => void }) {
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
                <h2 className="font-medium">Проверять пока нечего</h2>
                <p className="max-w-prose text-sm text-muted-foreground">
                    Проверки прогоняют версию модели по картинке, датасету, видео или экрану. Версия появляется после успешного обучения.
                </p>
                <Button variant="outline" data-testid="checks-open-models" onClick={openModels}>Открыть модели</Button>
            </section>
        );
    }

    return (
        <div className="space-y-5" data-testid="checks-section">
            {models.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{models.error}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => void models.refresh()}>Повторить</Button>
                    </AlertDescription>
                </Alert>
            )}
            <section className="space-y-4 rounded-lg border bg-card p-5">
                <div className="flex flex-wrap items-end justify-between gap-4">
                    <div className="space-y-1">
                        <span className="block text-xs text-muted-foreground">Версия модели</span>
                        <Select value={version?.id ?? ""} disabled={!versions.length} onValueChange={next => { if (next) setSelectedId(next); }}>
                            <SelectTrigger aria-label="Версия модели для проверки" className="w-80" data-testid="checks-version">
                                <SelectValue placeholder={models.loaded ? "Нет версий" : "Загрузка…"} />
                            </SelectTrigger>
                            <SelectContent>
                                {versions.map(item => <SelectItem key={item.id} value={item.id}>{versionOptionLabel(item)}</SelectItem>)}
                            </SelectContent>
                        </Select>
                    </div>
                    <div role="tablist" aria-label="Режим проверки" className="flex flex-wrap gap-1 rounded-md border p-1">
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
                                {item.label}
                            </Button>
                        ))}
                    </div>
                </div>
                {version && (
                    <p className="text-xs text-muted-foreground" data-testid="checks-caption">
                        Веса best.pt версии {version.number} · вход {version.input.width} × {version.input.height} px · классов {version.classes.length}. Проверка занимает
                        видеокарту так же, как обучение: пока идёт обучение, проверка не запустится, а обучение, запущенное во время проверки, ждёт в очереди.{" "}
                        <Button variant="link" size="sm" className="h-auto p-0 text-xs" data-testid="checks-models-link" onClick={openModels}>Все версии — в «Моделях»</Button>
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
