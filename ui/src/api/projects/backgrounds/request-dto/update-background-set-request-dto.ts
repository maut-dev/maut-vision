export interface UpdateBackgroundSetRequestDto {
    projectId: string;
    setId: string;
    name: string;
    description: string;
    /** Ревизия набора, которую видел пользователь. */
    revision: number;
}
