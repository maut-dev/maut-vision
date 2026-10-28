import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";

/**
 * Видео из images.selectVideo. selectionToken — null, если пользователь закрыл диалог (тогда остальные поля null); действует
 * 30 минут и только на этой странице. displayName — имя файла без пути, byteSize — размер файла, width/height — размер
 * кадров с учётом поворота, durationMs — длительность. Видео при выборе не хешируется.
 */
export interface VideoSelectionResponseDto {
    selectionToken: string | null;
    displayName: string | null;
    byteSize: number | null;
    width: number | null;
    height: number | null;
    durationMs: number | null;
}

/** Выбранное видео: все поля заполнены. */
export type SelectedVideoDto = { [Key in keyof VideoSelectionResponseDto]: NonNullable<VideoSelectionResponseDto[Key]> };

const isPositive = (value: unknown): value is number => isCount(value) && value > 0;

export function isVideoSelectionResponseDto(value: unknown): value is VideoSelectionResponseDto {
    if (!isRecord(value)) return false;
    if (value.selectionToken === null) {
        return value.displayName === null && value.byteSize === null && value.width === null && value.height === null && value.durationMs === null;
    }
    return typeof value.selectionToken === "string" && value.selectionToken.length > 0 && typeof value.displayName === "string"
        && isPositive(value.byteSize) && isPositive(value.width) && isPositive(value.height) && isPositive(value.durationMs);
}

/** true, если пользователь выбрал видео (а не закрыл диалог). */
export function isSelectedVideo(value: VideoSelectionResponseDto): value is SelectedVideoDto {
    return value.selectionToken !== null;
}
