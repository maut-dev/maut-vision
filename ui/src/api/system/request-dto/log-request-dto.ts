export type LogLevelDto = "error" | "warning";

export const maxLogMessageLength = 2000;
export const maxLogStackLength = 8000;

/** Необработанная ошибка UI для журнала host; host ограничивает частоту записей. */
export interface LogRequestDto {
    level: LogLevelDto;
    /** Не длиннее maxLogMessageLength. */
    message: string;
    /** Не длиннее maxLogStackLength. */
    stack?: string;
}
