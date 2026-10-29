import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { TrainingPresetResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { locale } from "@/lib/format";

/**
 * "Parameter preset": a choice fills the form; "Save as preset…" opens the name field (the same name ignoring case — "Overwrite");
 * "Delete preset" asks for a confirmation. Presets are shared by all projects.
 */
export function TrainingPresetBar({ presets, presetId, changed, busy, canSave, apply, save, remove }: {
    presets: readonly TrainingPresetResponseDto[];
    presetId: string;
    /** The form changed after a preset was chosen: the choice shows empty so the preset can be applied again. */
    changed: boolean;
    busy: boolean;
    canSave: boolean;
    apply: (presetId: string) => void;
    save: (name: string) => Promise<boolean>;
    remove: (preset: TrainingPresetResponseDto) => Promise<boolean>;
}) {
    const { t } = useTranslation("training");
    /** undefined — the name field is closed. */
    const [name, setName] = useState<string>();
    const [confirmDelete, setConfirmDelete] = useState(false);
    const selected = presets.find(item => item.id === presetId);
    const trimmed = name?.trim() ?? "";
    const overwritten = presets.find(item => item.name.toLocaleLowerCase(locale()) === trimmed.toLocaleLowerCase(locale()));

    async function submit() {
        if (!trimmed || !canSave || busy) return;
        if (await save(trimmed)) setName(undefined);
    }

    return (
        <div className="space-y-2" data-testid="training-presets">
            <div className="flex flex-wrap items-end gap-2">
                <label className="space-y-1">
                    <span className="block text-xs text-muted-foreground">{t("presets.label")}</span>
                    <Select value={changed ? "" : presetId} onValueChange={id => { if (!id) return; setConfirmDelete(false); apply(id); }} disabled={!presets.length || busy}>
                        <SelectTrigger aria-label={t("presets.label")} className="w-full sm:w-72" data-testid="training-preset">
                            <SelectValue placeholder={presets.length ? t("presets.choose") : t("presets.none")} />
                        </SelectTrigger>
                        <SelectContent>
                            {presets.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
                        </SelectContent>
                    </Select>
                </label>
                {name === undefined && (
                    <Button type="button" variant="outline" disabled={busy || !canSave} data-testid="training-preset-save-open" onClick={() => setName(selected?.name ?? "")}>
                        {t("presets.saveAs")}
                    </Button>
                )}
                {selected && !confirmDelete && (
                    <Button type="button" variant="ghost" disabled={busy} data-testid="training-preset-delete" onClick={() => setConfirmDelete(true)}>
                        {t("presets.delete")}
                    </Button>
                )}
                {selected && confirmDelete && (
                    <span className="flex items-center gap-2 text-xs" data-testid="training-preset-delete-confirm-row">
                        {t("presets.deleteQuestion", { name: selected.name })}
                        <Button type="button" variant="destructive" size="sm" disabled={busy} data-testid="training-preset-delete-confirm"
                            onClick={() => void remove(selected).then(done => { if (done) setConfirmDelete(false); })}>
                            {t("presets.deleteConfirm")}
                        </Button>
                        <Button type="button" variant="ghost" size="sm" onClick={() => setConfirmDelete(false)}>{t("presets.cancel")}</Button>
                    </span>
                )}
            </div>
            {name !== undefined && (
                <div className="flex flex-wrap items-end gap-2">
                    <label className="space-y-1">
                        <span className="block text-xs text-muted-foreground">{t("presets.name")}</span>
                        <Input
                            autoFocus
                            className="sm:w-72"
                            data-testid="training-preset-name"
                            value={name}
                            maxLength={120}
                            placeholder={t("presets.namePlaceholder")}
                            onChange={event => setName(event.target.value)}
                            onKeyDown={event => {
                                if (event.key === "Enter") {
                                    event.preventDefault();
                                    void submit();
                                }
                                if (event.key === "Escape") setName(undefined);
                            }}
                        />
                    </label>
                    <Button type="button" disabled={busy || !canSave || !trimmed} data-testid="training-preset-save" onClick={() => void submit()}>
                        {overwritten ? t("presets.overwrite") : t("presets.save")}
                    </Button>
                    <Button type="button" variant="ghost" onClick={() => setName(undefined)}>{t("presets.cancel")}</Button>
                </div>
            )}
            <p className="text-xs text-muted-foreground" data-testid="training-preset-hint">
                {name !== undefined
                    ? overwritten
                        ? t("presets.overwriteHint", { name: overwritten.name })
                        : t("presets.saveHint")
                    : changed && selected
                        ? t("presets.changedHint", { name: selected.name })
                        : t("presets.hint")}
            </p>
        </div>
    );
}
