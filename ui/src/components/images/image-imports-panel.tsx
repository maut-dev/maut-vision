import { useTranslation } from "react-i18next";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { parseImportImagesResult } from "@/api/projects/images/response-dto/import-images-result-dto";
import { JobHistory, JobIssues } from "@/components/jobs/job-history";
import { count, JobRunRow } from "@/components/jobs/job-run";
import type { ImageImportsState } from "@/lib/use-image-imports";
import { errorText } from "@/lib/errors";
import "@/i18n/sections/images";

/**
 * The import history of this group: the state, the result, errors per file, the progress and cancelling a running import. No scroll of
 * its own: it sits in a Pane. The error of the last import action is shown by the section above its tabs.
 */
export function ImageImports({ imports }: { imports: ImageImportsState }) {
    const { t } = useTranslation("images");
    return (
        <JobHistory
            title={t("imports.title")}
            testId="image-imports"
            jobs={imports.jobs}
            empty={t("imports.empty")}
            renderRow={job => <ImportRow key={job.id} job={job} cancel={() => void imports.cancel(job)} />}
        />
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
                <span className="tabular-nums text-text-2" data-testid="import-summary">
                    {t("imports.summary", { added: count(result.added), total: count(result.total), duplicates: count(result.duplicates), rejected: count(result.rejected) })}
                </span>
            ) : undefined}
        >
            {!!result?.rejectedFiles.length && (
                <JobIssues
                    title={t("imports.fileErrors", { number: count(result.rejected) })}
                    testId="import-issues"
                    more={result.rejected > result.rejectedFiles.length ? t("imports.more", { number: count(result.rejected - result.rejectedFiles.length) }) : undefined}
                >
                    {result.rejectedFiles.map((file, index) => <li key={`${file.name}-${index}`}>{file.name} — {errorText(file.reason)}</li>)}
                </JobIssues>
            )}
        </JobRunRow>
    );
}
