export interface SetBackgroundSetArchivedRequestDto {
    projectId: string;
    setId: string;
    archived: boolean;
    revision: number;
}
