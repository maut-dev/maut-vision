export interface SetImageGroupArchivedRequestDto {
    projectId: string;
    groupId: string;
    archived: boolean;
    revision: number;
}
