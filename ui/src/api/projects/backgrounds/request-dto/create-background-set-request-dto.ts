/** "Background set name" (1–120 characters) and a description up to 4000; the set size is the current project input. */
export interface CreateBackgroundSetRequestDto {
    projectId: string;
    name: string;
    description: string;
}
