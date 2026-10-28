export interface ImportImagesRequestDto {
    projectId: string;
    groupId: string;
    /** Токен из images.selectFiles, images.selectFolder или images.selectVideo; действует 30 минут и только на этой странице. */
    selectionToken: string;
    /** Создаётся UI на одно действие; повтор с тем же значением вернёт ту же задачу. */
    operationId: string;
    /** Для папки: «Включая вложенные папки». По умолчанию false. */
    recursive?: boolean;
    /** «Пропускать повторы в группе». По умолчанию true. */
    skipDuplicates?: boolean;
    /** Для видео (обязательно): отметка первого кадра, мс, ≥ 0. */
    startMs?: number;
    /** Для видео (обязательно): шаг между кадрами, мс, ≥ 1. */
    intervalMs?: number;
    /** Для видео (обязательно): число кадров ≥ 1; последняя отметка startMs + (count − 1) × intervalMs раньше конца видео. */
    count?: number;
}
