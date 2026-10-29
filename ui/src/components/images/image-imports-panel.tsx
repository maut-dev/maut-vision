import { useTranslation } from "react-i18next";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { parseImportImagesResult } from "@/api/projects/images/response-dto/import-images-result-dto";
import { count, JobRunRow } from "@/components/jobs/job-run";
import { Button } from "@/components/ui/button";
import type { ImageImportsState } from "@/lib/use-image-imports";
import { errorText } from "@/lib/errors";
import "@/i18n/sections/images";

/** The import history of this group: the state, the result, errors per file, the progress and cancelling a running import. */
export function ImageImportsPanel({ imports }: { imports: ImageImportsState }) {
    const { t } = useTranslation("images");
    if (!imports.jobs.length && !imports.selecting && !imports.error) return null;
    return (
        <section className="max-h-44 shrink-0 overflow-auto rounded-md border bg-card p-3" aria-label={t("imports.title")} data-testid="image-imports">
            <h3 className="mb-2 text-sm font-medium">{t("imports.title")}</h3>
            {imports.selecting && <p role="status" className="text-xs text-muted-foreground">{t("imports.selecting")}</p>}
            {imports.error && (
                <p role="alert" className="flex items-center justify-between gap-2 text-xs text-destructive">
                    {imports.error}
                    <Button variant="ghost" size="xs" onClick={imports.dismissError}>{t("section.hide")}</Button>
                </p>
            )}
            <ul className="divide-y">
                {imports.jobs.map(job => <ImportRow key={job.id} job={job} cancel={() => void imports.cancel(job)} />)}
            </ul>
        </section>
    );
}

function ImportRow({ job, cancel }: { job: JobResponseDto; cancel: () => void }) {
    const { t } = useTranslation("images");
    const result = parseImportImagesResult(job);
    return (
        <JobRunRow
            job={job}
            progressLabel={t("imports.progress")}
            cancel={cancel}
            summary={result ? (
                <span className="tabular-nums text-muted-foreground" data-testid="import-summary">
                    {t("imports.summary", { added: count(result.added), total: count(result.total), duplicates: count(result.duplicates), rejected: count(result.rejected) })}
                </span>
            ) : undefined}
        >
            {!!result?.rejectedFiles.length && (
                <details>
                    <summary className="cursor-pointer text-muted-foreground">{t("imports.fileErrors", { number: count(result.rejected) })}</summary>
                    <ul className="mt-1 max-h-24 list-disc space-y-0.5 overflow-y-auto pl-5">
                        {result.rejectedFiles.map((file, index) => <li key={`${file.name}-${index}`} className="break-words">{file.name} — {errorText(file.reason)}</li>)}
                        {result.rejected > result.rejectedFiles.length && <li>{t("imports.more", { number: count(result.rejected - result.rejectedFiles.length) })}</li>}
                    </ul>
                </details>
            )}
        </JobRunRow>
    );
}
