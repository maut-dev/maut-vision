export interface UpdateBackgroundSetRequestDto {
    projectId: string;
    setId: string;
    name: string;
    description: string;
    /** The set revision the user saw. */
    revision: number;
}
