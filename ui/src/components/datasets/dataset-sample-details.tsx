import { useTranslation } from "react-i18next";
import type { DatasetSampleResponseDto, DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { boxPixels, centerBox, classLabel, splitLabel, variantName } from "@/lib/datasets/dataset-view";
import { formatBytes } from "@/lib/format";

/**
 * Details of a sample in the viewer: the split, size and file weight, the source image and the annotation revision, objects and
 * negatives with the class, the variant (the name at build time, not the ID) and the box in pixels, ROI.
 */
export function DatasetSampleDetails({ sample, version }: { sample: DatasetSampleResponseDto; version: DatasetVersionResponseDto }) {
    const { t } = useTranslation("datasets");
    const { width, height } = sample;
    const group = version.composition.groups.find(item => item.groupId === sample.groupId);
    const variant = (id: string | null) => {
        const name = variantName(version, id);
        return name ? <span className="break-words"> · {t("sample.variant", { name })}</span> : null;
    };
    return (
        <div className="space-y-2" data-testid="dataset-sample-details">
            <p>
                {t("sample.summary", { name: version.name, split: splitLabel(sample.split), width, height, size: formatBytes(sample.byteSize), format: sample.format === "png" ? "PNG" : "JPEG" })}
            </p>
            <p className="break-all">
                {group
                    ? t("sample.sourceGroup", { id: sample.id, group: group.name, revision: sample.annotationRevision })
                    : t("sample.source", { id: sample.id, revision: sample.annotationRevision })}
            </p>
            <p>{t("sample.fixed")}</p>
            {!sample.boxes.length && (
                <p>{sample.negatives.length ? t("sample.noTargetsNegatives") : t("sample.noTargets")}</p>
            )}
            <ul aria-label={t("sample.annotation")} className="space-y-1">
                {sample.boxes.map((box, index) => (
                    <li key={`object-${index}`}>
                        {t("sample.object", { number: index + 1, label: classLabel(version, box.classId, box.index), box: boxPixels(centerBox(box), width, height) })}{variant(box.classVariantId)}
                    </li>
                ))}
                {sample.negatives.map((box, index) => (
                    <li key={`negative-${index}`}>
                        {t("sample.negative", { number: index + 1, label: classLabel(version, box.classId), box: boxPixels(centerBox(box), width, height) })}{variant(box.classVariantId)}
                        {` · ${t("sample.notInLabels")}`}
                    </li>
                ))}
                {sample.roi && <li>{t("sample.roi", { box: boxPixels(sample.roi, width, height) })}</li>}
            </ul>
        </div>
    );
}
