import { isRecord } from "../../transport/envelopes";

/**
 * Пользователь выходит из приложения («Выйти» в области уведомлений; закрытие окна только прячет его, WPF-окно не вызывает
 * beforeunload). UI отвечает systemApi.closeDecision с этим requestId в течение 1 с; без ответа приложение завершается.
 */
export interface CloseRequestedEventDto {
    kind: "event";
    event: "app.closeRequested";
    data: { requestId: string };
}

export function isCloseRequestedDto(value: unknown): value is CloseRequestedEventDto["data"] {
    return isRecord(value) && typeof value.requestId === "string" && value.requestId.length > 0;
}
