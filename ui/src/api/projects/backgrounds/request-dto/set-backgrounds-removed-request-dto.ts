/** Фон с ревизией, которую видел пользователь. */
export interface BackgroundRevisionRequestDto {
    id: string;
    revision: number;
}

export interface SetBackgroundsRemovedRequestDto {
    projectId: string;
    /** До 400 фонов; всё или ничего. Ревизия набора не нужна: идущая подготовка не мешает. */
    items: BackgroundRevisionRequestDto[];
    /** true — «Убрать», false — «Восстановить». */
    removed: boolean;
}
