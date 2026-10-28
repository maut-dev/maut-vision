import { useState, type FormEvent } from "react";
import type { ImageGroupResponseDto, ImageSplitHint } from "@/api/projects/images/response-dto/image-group-response-dto";
import { imageSplitHints } from "@/api/projects/images/response-dto/image-group-response-dto";
import { DialogError, GuardedDialog } from "@/components/catalog/catalog-dialogs";
import { Button } from "@/components/ui/button";
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { locale } from "@/lib/format";

export const splitHintLabels: Record<ImageSplitHint, string> = {
    unassigned: "Без назначения",
    train: "Обучение",
    validation: "Проверка",
    test: "Тест",
};

export interface GroupValues {
    name: string;
    description: string;
    splitHint: ImageSplitHint;
}

/** Изменение группы: название (1–120), описание (до 4000) и назначение. */
export function ImageGroupDialog({ group, busy, error, close, save }: {
    group: ImageGroupResponseDto;
    busy: boolean;
    error?: string;
    close: () => void;
    save: (values: GroupValues) => void;
}) {
    const [name, setName] = useState(group.name);
    const [description, setDescription] = useState(group.description);
    const [splitHint, setSplitHint] = useState<ImageSplitHint>(group.splitHint);

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && name.trim()) save({ name: name.trim(), description: description.trim(), splitHint });
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="image-group-dialog">
            <DialogHeader>
                <DialogTitle>Изменить группу</DialogTitle>
                <DialogDescription>Назначение подсказывает, в какую часть датасета пойдут кадры группы.</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                    <Label htmlFor="image-group-name">Название</Label>
                    <Input id="image-group-name" data-testid="image-group-name" autoFocus required maxLength={120} value={name} onChange={event => setName(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="image-group-description">Описание</Label>
                    <Textarea id="image-group-description" className="h-24 resize-none" maxLength={4000} value={description} onChange={event => setDescription(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="image-group-split">Назначение</Label>
                    <Select value={splitHint} onValueChange={value => setSplitHint(value as ImageSplitHint)} disabled={busy}>
                        <SelectTrigger id="image-group-split" data-testid="image-group-split" className="w-full"><SelectValue /></SelectTrigger>
                        <SelectContent>
                            {imageSplitHints.map(hint => <SelectItem key={hint} value={hint}>{splitHintLabels[hint]}</SelectItem>)}
                        </SelectContent>
                    </Select>
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>Отмена</Button>
                    <Button type="submit" data-testid="image-group-save" disabled={busy || !name.trim()}>{busy ? "Сохранение…" : "Сохранить"}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}

/** Перемещение выбранных изображений в другую активную группу того же происхождения; они встают в конец группы. */
export function MoveImagesDialog({ count, targets, busy, error, close, move }: {
    count: number;
    targets: readonly ImageGroupResponseDto[];
    busy: boolean;
    error?: string;
    close: () => void;
    move: (target: ImageGroupResponseDto) => void;
}) {
    const [targetId, setTargetId] = useState(targets.length === 1 ? targets[0].id : "");
    const target = targets.find(item => item.id === targetId);

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && target) move(target);
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="move-images-dialog">
            <DialogHeader>
                <DialogTitle>Переместить изображения</DialogTitle>
                <DialogDescription>Выбранные изображения ({count.toLocaleString(locale())}) переместятся в конец выбранной группы вместе с разметкой.</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                {targets.length ? (
                    <div className="space-y-2">
                        <Label htmlFor="move-target">Группа назначения</Label>
                        <Select value={targetId} onValueChange={setTargetId} disabled={busy}>
                            <SelectTrigger id="move-target" data-testid="move-target" className="w-full" autoFocus><SelectValue placeholder="Выберите группу" /></SelectTrigger>
                            <SelectContent>
                                {targets.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
                            </SelectContent>
                        </Select>
                    </div>
                ) : <p className="text-sm text-muted-foreground">Нет других активных групп того же происхождения.</p>}
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>Отмена</Button>
                    <Button type="submit" data-testid="move-confirm" disabled={busy || !target}>{busy ? "Перемещение…" : "Переместить"}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}
