import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { parseImportImagesResult } from "@/api/projects/images/response-dto/import-images-result-dto";
import { count, JobRunRow } from "@/components/jobs/job-run";
import { Button } from "@/components/ui/button";
import type { ImageImportsState } from "@/lib/use-image-imports";
import { errorText } from "@/lib/errors";

/** История импорта этой группы: состояние, итог, ошибки по файлам, прогресс и отмена идущего импорта. */
export function ImageImportsPanel({ imports }: { imports: ImageImportsState }) {
    if (!imports.jobs.length && !imports.selecting && !imports.error) return null;
    return (
        <section className="max-h-44 shrink-0 overflow-auto rounded-md border bg-card p-3" aria-label="Импорт изображений" data-testid="image-imports">
            <h3 className="mb-2 text-sm font-medium">Импорт изображений</h3>
            {imports.selecting && <p role="status" className="text-xs text-muted-foreground">Выберите файлы или папку в окне выбора…</p>}
            {imports.error && (
                <p role="alert" className="flex items-center justify-between gap-2 text-xs text-destructive">
                    {imports.error}
                    <Button variant="ghost" size="xs" onClick={imports.dismissError}>Скрыть</Button>
                </p>
            )}
            <ul className="divide-y">
                {imports.jobs.map(job => <ImportRow key={job.id} job={job} cancel={() => void imports.cancel(job)} />)}
            </ul>
        </section>
    );
}

function ImportRow({ job, cancel }: { job: JobResponseDto; cancel: () => void }) {
    const result = parseImportImagesResult(job);
    return (
        <JobRunRow
            job={job}
            progressLabel="Прогресс импорта"
            cancel={cancel}
            summary={result ? (
                <span className="tabular-nums text-muted-foreground" data-testid="import-summary">
                    Добавлено {count(result.added)} / {count(result.total)} · пропущено {count(result.duplicates)} · ошибок {count(result.rejected)}
                </span>
            ) : undefined}
        >
            {!!result?.rejectedFiles.length && (
                <details>
                    <summary className="cursor-pointer text-muted-foreground">Ошибки по файлам ({count(result.rejected)})</summary>
                    <ul className="mt-1 max-h-24 list-disc space-y-0.5 overflow-y-auto pl-5">
                        {result.rejectedFiles.map((file, index) => <li key={`${file.name}-${index}`} className="break-words">{file.name} — {errorText(file.reason)}</li>)}
                        {result.rejected > result.rejectedFiles.length && <li>и ещё {count(result.rejected - result.rejectedFiles.length)}</li>}
                    </ul>
                </details>
            )}
        </JobRunRow>
    );
}
