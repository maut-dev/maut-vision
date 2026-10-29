import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import type { ImageGroupResponseDto, ImageSplitHint } from "@/api/projects/images/response-dto/image-group-response-dto";
import { imageSplitHints } from "@/api/projects/images/response-dto/image-group-response-dto";
import { DialogError, GuardedDialog } from "@/components/catalog/catalog-dialogs";
import { Button } from "@/components/ui/button";
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { formatNumber } from "@/lib/format";
import { splitHintLabels } from "@/lib/images/split-hints";
import "@/i18n/sections/images";

export interface GroupValues {
    name: string;
    description: string;
    splitHint: ImageSplitHint;
}

/** Editing a group: the name (1–120), the description (up to 4000) and the purpose. */
export function ImageGroupDialog({ group, busy, error, close, save }: {
    group: ImageGroupResponseDto;
    busy: boolean;
    error?: string;
    close: () => void;
    save: (values: GroupValues) => void;
}) {
    const { t } = useTranslation("images");
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
                <DialogTitle>{t("groupDialog.title")}</DialogTitle>
                <DialogDescription>{t("groupDialog.description")}</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                    <Label htmlFor="image-group-name">{t("groupDialog.name")}</Label>
                    <Input id="image-group-name" data-testid="image-group-name" autoFocus required maxLength={120} value={name} onChange={event => setName(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="image-group-description">{t("groupDialog.descriptionLabel")}</Label>
                    <Textarea id="image-group-description" className="h-24 resize-none" maxLength={4000} value={description} onChange={event => setDescription(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="image-group-split">{t("groupDialog.split")}</Label>
                    <Select value={splitHint} onValueChange={value => setSplitHint(value as ImageSplitHint)} disabled={busy}>
                        <SelectTrigger id="image-group-split" data-testid="image-group-split" className="w-full"><SelectValue /></SelectTrigger>
                        <SelectContent>
                            {imageSplitHints.map(hint => <SelectItem key={hint} value={hint}>{splitHintLabels[hint]}</SelectItem>)}
                        </SelectContent>
                    </Select>
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>{t("dialog.cancel")}</Button>
                    <Button type="submit" data-testid="image-group-save" disabled={busy || !name.trim()}>{busy ? t("dialog.saving") : t("dialog.save")}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}

/** Moving the selected images to another active group of the same origin; they go to the end of that group. */
export function MoveImagesDialog({ count, targets, busy, error, close, move }: {
    count: number;
    targets: readonly ImageGroupResponseDto[];
    busy: boolean;
    error?: string;
    close: () => void;
    move: (target: ImageGroupResponseDto) => void;
}) {
    const { t } = useTranslation("images");
    const [targetId, setTargetId] = useState(targets.length === 1 ? targets[0].id : "");
    const target = targets.find(item => item.id === targetId);

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && target) move(target);
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="move-images-dialog">
            <DialogHeader>
                <DialogTitle>{t("moveDialog.title")}</DialogTitle>
                <DialogDescription>{t("moveDialog.description", { number: formatNumber(count) })}</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                {targets.length ? (
                    <div className="space-y-2">
                        <Label htmlFor="move-target">{t("moveDialog.target")}</Label>
                        <Select value={targetId} onValueChange={setTargetId} disabled={busy}>
                            <SelectTrigger id="move-target" data-testid="move-target" className="w-full" autoFocus><SelectValue placeholder={t("moveDialog.chooseGroup")} /></SelectTrigger>
                            <SelectContent>
                                {targets.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
                            </SelectContent>
                        </Select>
                    </div>
                ) : <p className="text-sm text-muted-foreground">{t("moveDialog.noTargets")}</p>}
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>{t("dialog.cancel")}</Button>
                    <Button type="submit" data-testid="move-confirm" disabled={busy || !target}>{busy ? t("moveDialog.moving") : t("moveDialog.move")}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}
