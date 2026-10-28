import { useState, type FormEvent } from "react";
import { Boxes } from "lucide-react";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import type { BaseModelsState } from "@/lib/use-base-models";

export interface ProjectEditorValues {
    name: string;
    description: string;
    baseModelId: string;
    inputSize: { width: number; height: number };
}

export function ProjectEditor({ project, initialModelId, catalog, busy, error, close, save, openCatalog }: {
    project?: ProjectResponseDto;
    initialModelId?: string;
    catalog: BaseModelsState;
    busy: boolean;
    error?: string;
    close: () => void;
    save: (values: ProjectEditorValues) => void;
    openCatalog: () => void;
}) {
    const installed = catalog.models.filter(model => model.status === "installed");
    const [baseModelId, setBaseModelId] = useState(project?.baseModelId ?? initialModelId ?? installed[0]?.id ?? "");
    const [name, setName] = useState(project?.name ?? "");
    const [description, setDescription] = useState(project?.description ?? "");
    const [width, setWidth] = useState(String(project?.inputSize.width ?? 640));
    const [height, setHeight] = useState(String(project?.inputSize.height ?? 640));
    const selected = catalog.models.find(model => model.id === baseModelId);
    const stride = selected?.stride ?? 32;
    // A project keeps its current model even if its weights were removed; a new choice must be installed.
    const modelAvailable = (!!project && project.baseModelId === baseModelId) || selected?.status === "installed";
    const sizeValid = [width, height].every(value => {
        const number = Number(value);
        return Number.isSafeInteger(number) && number >= stride && number % stride === 0;
    });

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!modelAvailable || !sizeValid || busy) return;
        save({ name: name.trim(), description: description.trim(), baseModelId, inputSize: { width: Number(width), height: Number(height) } });
    }

    return (
        <Dialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <DialogContent className="max-h-[calc(100dvh-3rem)] overflow-y-auto" onInteractOutside={event => { if (busy) event.preventDefault(); }}>
                <DialogHeader>
                    <DialogTitle>{project ? "Настройки проекта" : "Новый проект"}</DialogTitle>
                    <DialogDescription>Размер входа задаёт размер фонов, кадров генерации и датасетов.</DialogDescription>
                </DialogHeader>
                <form onSubmit={submit} className="space-y-4">
                    <div className="space-y-2">
                        <Label htmlFor="project-name">Название</Label>
                        <Input id="project-name" data-testid="project-name" autoFocus required maxLength={120} value={name} onChange={event => setName(event.target.value)} />
                    </div>
                    <div className="space-y-2">
                        <Label htmlFor="project-description">Описание</Label>
                        <Textarea id="project-description" className="h-24 resize-none" maxLength={2000} value={description} onChange={event => setDescription(event.target.value)} />
                    </div>
                    <div className="space-y-2">
                        <Label htmlFor="project-model">Модель для обучения</Label>
                        <Select value={baseModelId} onValueChange={setBaseModelId} disabled={busy || catalog.loading}>
                            <SelectTrigger id="project-model" data-testid="project-model" className="w-full"><SelectValue placeholder="Выберите установленную модель" /></SelectTrigger>
                            <SelectContent>
                                {project && !installed.some(model => model.id === project.baseModelId) && (
                                    <SelectItem value={project.baseModelId}>{project.baseModelId} · модель проекта</SelectItem>
                                )}
                                {installed.map(model => <SelectItem key={model.id} value={model.id}>{model.id}</SelectItem>)}
                            </SelectContent>
                        </Select>
                        {!installed.length && !project && (
                            <p className="text-sm text-muted-foreground">Установите модель в каталоге, чтобы создать проект.</p>
                        )}
                        <Button type="button" variant="ghost" size="sm" disabled={busy} onClick={openCatalog}><Boxes />Открыть каталог моделей</Button>
                    </div>
                    <div className="grid grid-cols-2 gap-4">
                        <div className="space-y-2">
                            <Label htmlFor="project-width">Ширина входа, px</Label>
                            <Input id="project-width" type="number" required min={stride} step={stride} value={width} onChange={event => setWidth(event.target.value)} />
                        </div>
                        <div className="space-y-2">
                            <Label htmlFor="project-height">Высота входа, px</Label>
                            <Input id="project-height" type="number" required min={stride} step={stride} value={height} onChange={event => setHeight(event.target.value)} />
                        </div>
                    </div>
                    <p className={sizeValid ? "text-xs text-muted-foreground" : "text-xs text-destructive"}>
                        Размеры кратны {stride}. Существующие датасеты сохранят свой размер.
                    </p>
                    <div className="min-h-12" aria-live="polite">
                        {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
                    </div>
                    <DialogFooter>
                        <Button type="button" variant="outline" disabled={busy} onClick={close}>Отмена</Button>
                        <Button type="submit" data-testid="project-save" disabled={busy || !name.trim() || !modelAvailable || !sizeValid}>
                            {busy ? "Сохранение…" : project ? "Сохранить" : "Создать"}
                        </Button>
                    </DialogFooter>
                </form>
            </DialogContent>
        </Dialog>
    );
}
