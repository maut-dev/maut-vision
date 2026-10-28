/** Ответ на app.closeRequested: allow — окно можно закрыть (нет несохранённых изменений). */
export interface CloseDecisionRequestDto {
    requestId: string;
    allow: boolean;
}

/** UI сохранила или отбросила изменения после попытки закрыть окно и просит закрыть его. */
export type CloseRequestDto = Record<string, never>;
