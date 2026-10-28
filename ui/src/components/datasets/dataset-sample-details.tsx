import type { DatasetSampleResponseDto, DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { boxPixels, centerBox, classLabel, splitLabel, variantName } from "@/lib/datasets/dataset-view";
import { locale } from "@/lib/format";

/**
 * Сведения образца в просмотрщике: часть, размер и вес файла, исходное изображение и версия
 * разметки, объекты и негативы с классом, вариантом (название на момент сборки, а не ID) и рамкой в пикселях, ROI.
 */
export function DatasetSampleDetails({ sample, version }: { sample: DatasetSampleResponseDto; version: DatasetVersionResponseDto }) {
    const { width, height } = sample;
    const group = version.composition.groups.find(item => item.groupId === sample.groupId);
    const variant = (id: string | null) => {
        const name = variantName(version, id);
        return name ? <span className="break-words"> · Вариант: {name}</span> : null;
    };
    return (
        <div className="space-y-2" data-testid="dataset-sample-details">
            <p>
                Датасет: {version.name} · {splitLabel(sample.split)} · {width} × {height} · {(sample.byteSize / 1024).toLocaleString(locale(), { maximumFractionDigits: 1 })} КБ
                · {sample.format === "png" ? "PNG" : "JPEG"}
            </p>
            <p className="break-all">
                Исходное изображение: {sample.id}{group && ` · группа «${group.name}»`} · Версия разметки: {sample.annotationRevision}
            </p>
            <p>Копия кадра и разметка зафиксированы при сборке. ROI не обрезает изображение.</p>
            {!sample.boxes.length && (
                <p>Без целевых объектов: пустая YOLO-разметка.{sample.negatives.length ? " Негативные объекты сохранены отдельно." : ""}</p>
            )}
            <ul aria-label="Подробности разметки датасета" className="space-y-1">
                {sample.boxes.map((box, index) => (
                    <li key={`object-${index}`}>
                        Объект {index + 1}: {classLabel(version, box.classId, box.index)} · {boxPixels(centerBox(box), width, height)}{variant(box.classVariantId)}
                    </li>
                ))}
                {sample.negatives.map((box, index) => (
                    <li key={`negative-${index}`}>
                        Негатив {index + 1}: {classLabel(version, box.classId)} · {boxPixels(centerBox(box), width, height)}{variant(box.classVariantId)} · Не входит в YOLO labels
                    </li>
                ))}
                {sample.roi && <li>Область ROI: {boxPixels(sample.roi, width, height)}</li>}
            </ul>
        </div>
    );
}
