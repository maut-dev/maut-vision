/** «Название набора фонов» (1–120 символов) и описание до 4000; размер набора — текущий вход проекта. */
export interface CreateBackgroundSetRequestDto {
    projectId: string;
    name: string;
    description: string;
}
