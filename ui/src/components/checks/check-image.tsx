import { useState } from "react";
import { useTranslation } from "react-i18next";
import { checksApi } from "@/api/projects/checks/checks-api";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { Notice } from "@/components/layout/notice";
import { Button } from "@/components/ui/button";
import { classColor, defaultCheckParameters, formatConfidence, parseCheckParameters, type CheckParameterValues } from "@/lib/checks/check-view";
import { errorMessage, formatNumber } from "@/lib/format";
import { useLiveCheck } from "@/lib/use-live-check";
import { CheckParameterFields } from "./check-parameter-fields";
import { CheckStatus, ClassSummary, DetectionOverlay } from "./check-results";

/** The image check: choosing the file and starting in one action, then the preview with boxes and the detections (N). */
export function CheckImage({ projectId, version, gpus }: { projectId: string; version: ModelVersionResponseDto; gpus: GpuInventoryResponseDto | null | undefined }) {
    const { t } = useTranslation("checks");
    const [values, setValues] = useState<CheckParameterValues>(defaultCheckParameters);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const session = useLiveCheck(projectId, "image", { keep: 1 });
    const parsed = parseCheckParameters(values);
    const problem = "field" in parsed ? parsed : undefined;
    const check = session.check;
    const frame = check?.lastFrame ?? session.journal.at(-1) ?? null;
    const detections = frame?.detections ?? [];

    async function pick() {
        if (!("parameters" in parsed)) return;
        setBusy(true);
        setError(undefined);
        try {
            const selection = await checksApi.selectImage({ projectId });
            if (!selection.token) return;
            session.attach(await checksApi.startImage({ projectId, versionId: version.id, selectionToken: selection.token, parameters: parsed.parameters,
                operationId: crypto.randomUUID() }));
        } catch (cause) {
            setError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    return (
        <section className="surface space-y-4 p-5" data-testid="check-image">
            <div>
                <h2 className="text-[15px] font-semibold tracking-[-0.015em]">{t("image.title")}</h2>
                <p className="mt-1 max-w-prose text-sm text-text-2">{t("image.text", { size: version.imageSize })}</p>
            </div>
            <div className="grid gap-3 sm:grid-cols-3">
                <CheckParameterFields value={values} onChange={setValues} gpus={gpus} disabled={busy || session.active} invalid={problem?.field} testPrefix="check-image" />
            </div>
            <p role={problem ? "alert" : undefined} className="min-h-4 text-xs text-danger">{problem?.message}</p>
            {error && <Notice variant="destructive" data-testid="check-image-error-notice">{error}</Notice>}
            <div className="flex flex-wrap gap-2">
                <Button data-testid="check-image-pick" disabled={!!problem || busy || session.active} onClick={() => void pick()}>
                    {t("image.pick")}
                </Button>
                {session.active && (
                    <Button variant="outline" disabled={check?.state === "cancelling"} data-testid="check-image-stop"
                        onClick={() => void session.stop().catch(cause => setError(errorMessage(cause)))}>{t("stop")}</Button>
                )}
            </div>
            {check && (
                <div className="space-y-4 border-t border-line pt-4" data-testid="check-image-result" data-check-id={check.id}>
                    <CheckStatus check={check} testPrefix="check-image" />
                    {check.preview && (
                        <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_18rem]">
                            <DetectionOverlay src={check.preview.url} width={check.preview.width} height={check.preview.height} detections={detections}
                                alt={check.source.name ?? t("image.alt")} testId="check-image-preview" />
                            <div className="space-y-2">
                                <h3 className="text-sm font-medium" data-testid="check-image-count">
                                    {t("image.detections", { number: formatNumber(frame?.detectionCount ?? detections.length) })}
                                </h3>
                                {detections.length ? (
                                    <ol className="space-y-1 text-sm" data-testid="check-image-detections">
                                        {detections.map((item, index) => (
                                            <li key={index} className="flex items-center justify-between gap-2 rounded-md border border-line px-2 py-1"
                                                title={t("image.box", { x: item.x.toFixed(3), y: item.y.toFixed(3), width: item.width.toFixed(3), height: item.height.toFixed(3) })}>
                                                <span className="inline-flex items-center gap-1.5">
                                                    <span className="inline-block size-2 rounded-full" style={{ background: classColor(item.classIndex) }} />
                                                    {item.name}
                                                </span>
                                                <span className="tabular-nums text-text-2">{formatConfidence(item.confidence)}</span>
                                            </li>
                                        ))}
                                    </ol>
                                ) : check.state === "succeeded" ? (
                                    <p className="text-sm text-text-2" data-testid="check-image-nothing">
                                        {t("image.nothing", { confidence: String(check.settings.confidence) })}
                                    </p>
                                ) : null}
                            </div>
                        </div>
                    )}
                    {check.state === "succeeded" && check.summary.classes.length > 1 && (
                        <div className="space-y-2">
                            <h3 className="text-sm font-medium">{t("classSummary")}</h3>
                            <ClassSummary check={check} testId="check-image-classes" />
                        </div>
                    )}
                </div>
            )}
        </section>
    );
}
