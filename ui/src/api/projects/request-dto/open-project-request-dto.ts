export interface OpenProjectRequestDto {
    projectId: string;
    /**
     * The navigation number, growing on the page. If an opening with a greater number started while the project was opening, the answer is
     * the projects.open_superseded error: the result is stale and must be dropped. Without a number the answer is not compared.
     */
    sequence?: number;
}
