import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { Checkbox } from "@/components/ui/checkbox";
import { seriesColor } from "@/lib/checks/check-view";
import { useModelEvaluations } from "@/lib/use-model-evaluations";
import { ModelEvaluations } from "./model-evaluations";

/** Versions compared by default: the chosen one and the next two. */
const defaultCompared = 3;

function defaults(versions: readonly ModelVersionResponseDto[], selectedId: string): string[] {
    const index = Math.max(0, versions.findIndex(item => item.id === selectedId));
    return [selectedId, ...versions.slice(index + 1).concat(versions.slice(0, index)).filter(item => item.id !== selectedId).slice(0, defaultCompared - 1).map(item => item.id)];
}

/** The Dataset mode of checks: versions to compare and the shared evaluations component; the version chosen above is always compared. */
export function CheckDataset({ projectId, versions, version, datasets, gpus }: {
    projectId: string;
    versions: readonly ModelVersionResponseDto[];
    version: ModelVersionResponseDto;
    datasets: readonly DatasetVersionResponseDto[];
    gpus: GpuInventoryResponseDto | null | undefined;
}) {
    const { t } = useTranslation("checks");
    const evaluations = useModelEvaluations(projectId);
    const [comparedIds, setComparedIds] = useState(() => defaults(versions, version.id));

    useEffect(() => {
        setComparedIds(current => current.includes(version.id) ? current : [version.id, ...current]);
    }, [version.id]);

    const compared = versions.filter(item => comparedIds.includes(item.id));
    return (
        <div className="space-y-4">
            {versions.length > 1 && (
                <section className="space-y-2 rounded-lg border bg-card p-4" data-testid="evaluation-compared">
                    <h2 className="text-sm font-medium">{t("dataset.compared")}</h2>
                    <div className="flex flex-wrap gap-x-5 gap-y-2">
                        {versions.map(item => (
                            <label key={item.id} className="flex items-center gap-2 text-sm">
                                <Checkbox
                                    checked={comparedIds.includes(item.id)}
                                    disabled={item.id === version.id}
                                    data-testid="evaluation-compared-version"
                                    data-version-id={item.id}
                                    onCheckedChange={checked => setComparedIds(current => checked === true ? [...new Set([...current, item.id])] : current.filter(id => id !== item.id))}
                                />
                                <span className="inline-block size-2 rounded-full" style={{ background: seriesColor(item.number) }} />
                                {t("version.number", { number: item.number })}
                                {item.isWorking && <span className="text-xs text-muted-foreground">{t("version.working")}</span>}
                            </label>
                        ))}
                    </div>
                </section>
            )}
            <ModelEvaluations evaluations={evaluations} versions={versions} compared={compared} versionId={version.id} datasets={datasets} gpus={gpus} />
        </div>
    );
}
