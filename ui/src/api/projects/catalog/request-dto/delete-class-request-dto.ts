export interface DeleteClassRequestDto {
    projectId: string;
    classId: string;
    /** The revision from the answer of the deletion review. */
    reviewRevision: string;
}
