export type LogLevelDto = "error" | "warning";

export const maxLogMessageLength = 2000;
export const maxLogStackLength = 8000;

/** An unhandled UI error for the host log; the host limits the rate of records. */
export interface LogRequestDto {
    level: LogLevelDto;
    /** At most maxLogMessageLength. */
    message: string;
    /** At most maxLogStackLength. */
    stack?: string;
}
