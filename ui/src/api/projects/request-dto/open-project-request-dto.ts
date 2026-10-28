export interface OpenProjectRequestDto {
    projectId: string;
    /**
     * Номер перехода, растущий на странице. Если, пока проект открывался, началось открытие с большим номером, ответ —
     * ошибка projects.open_superseded: результат устарел, его надо отбросить. Без номера ответ не сравнивается.
     */
    sequence?: number;
}
