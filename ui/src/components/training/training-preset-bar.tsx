import { useState } from "react";
import type { TrainingPresetResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { locale } from "@/lib/format";

/**
 * «Пресет параметров»: выбор заполняет форму; «Сохранить как пресет…» открывает поле имени (то же
 * имя без учёта регистра — «Перезаписать»); «Удалить пресет» спрашивает подтверждение. Пресеты общие для всех проектов.
 */
export function TrainingPresetBar({ presets, presetId, changed, busy, canSave, apply, save, remove }: {
    presets: readonly TrainingPresetResponseDto[];
    presetId: string;
    /** Форма изменена после выбора пресета: выбор показывается пустым, чтобы пресет можно было применить снова. */
    changed: boolean;
    busy: boolean;
    canSave: boolean;
    apply: (presetId: string) => void;
    save: (name: string) => Promise<boolean>;
    remove: (preset: TrainingPresetResponseDto) => Promise<boolean>;
}) {
    /** undefined — поле имени закрыто. */
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
                    <span className="block text-xs text-muted-foreground">Пресет параметров</span>
                    <Select value={changed ? "" : presetId} onValueChange={id => { if (!id) return; setConfirmDelete(false); apply(id); }} disabled={!presets.length || busy}>
                        <SelectTrigger aria-label="Пресет параметров" className="w-full sm:w-72" data-testid="training-preset">
                            <SelectValue placeholder={presets.length ? "Выберите пресет" : "Сохранённых пресетов нет"} />
                        </SelectTrigger>
                        <SelectContent>
                            {presets.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
                        </SelectContent>
                    </Select>
                </label>
                {name === undefined && (
                    <Button type="button" variant="outline" disabled={busy || !canSave} data-testid="training-preset-save-open" onClick={() => setName(selected?.name ?? "")}>
                        Сохранить как пресет…
                    </Button>
                )}
                {selected && !confirmDelete && (
                    <Button type="button" variant="ghost" disabled={busy} data-testid="training-preset-delete" onClick={() => setConfirmDelete(true)}>
                        Удалить пресет
                    </Button>
                )}
                {selected && confirmDelete && (
                    <span className="flex items-center gap-2 text-xs" data-testid="training-preset-delete-confirm-row">
                        Удалить «{selected.name}»?
                        <Button type="button" variant="destructive" size="sm" disabled={busy} data-testid="training-preset-delete-confirm"
                            onClick={() => void remove(selected).then(done => { if (done) setConfirmDelete(false); })}>
                            Удалить
                        </Button>
                        <Button type="button" variant="ghost" size="sm" onClick={() => setConfirmDelete(false)}>Отмена</Button>
                    </span>
                )}
            </div>
            {name !== undefined && (
                <div className="flex flex-wrap items-end gap-2">
                    <label className="space-y-1">
                        <span className="block text-xs text-muted-foreground">Имя пресета</span>
                        <Input
                            autoFocus
                            className="sm:w-72"
                            data-testid="training-preset-name"
                            value={name}
                            maxLength={120}
                            placeholder="Например, RTX 4090 · быстрый"
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
                        {overwritten ? "Перезаписать" : "Сохранить"}
                    </Button>
                    <Button type="button" variant="ghost" onClick={() => setName(undefined)}>Отмена</Button>
                </div>
            )}
            <p className="text-xs text-muted-foreground" data-testid="training-preset-hint">
                {name !== undefined
                    ? overwritten
                        ? `Пресет «${overwritten.name}» будет перезаписан текущими параметрами.`
                        : "Сохраняются основные и дополнительные настройки обучения. Пресеты общие для всех проектов."
                    : changed && selected
                        ? `Параметры изменены после выбора пресета «${selected.name}»: выберите его снова, чтобы вернуть его значения.`
                        : "Пресет заполняет форму; значения можно поправить перед запуском."}
            </p>
        </div>
    );
}
