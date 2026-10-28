import { parameterLabel, parameterText } from "@/lib/training/run-view";

/** Ключи, которыми управляет приложение: в списке параметров не показываются. */
const managed = new Set(["data", "model", "project", "name", "save_dir", "cfg", "source", "teacher"]);

/** «Запрошенные» / «Фактические»: подпись реестра (или ключ worker) и значение; длинные значения сокращены. */
export function ParameterList({ title, parameters, empty }: { title: string; parameters: Record<string, unknown> | null; empty?: string }) {
    const entries = Object.entries(parameters ?? {}).filter(([key]) => !managed.has(key));
    return (
        <div data-testid="training-parameter-list" data-title={title}>
            <h4 className="mb-2 text-sm font-medium">{title}</h4>
            {entries.length ? (
                <dl className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-x-3 gap-y-1 text-xs">
                    {entries.map(([key, value]) => (
                        <div key={key} className="contents" data-parameter={key}>
                            <dt className="break-all text-muted-foreground" title={key}>{parameterLabel(key)}</dt>
                            <dd className="break-all tabular-nums">{parameterText(value)}</dd>
                        </div>
                    ))}
                </dl>
            ) : <p className="text-xs text-muted-foreground">{empty}</p>}
        </div>
    );
}
