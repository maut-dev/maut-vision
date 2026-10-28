/**
 * История правок для отмены и повтора (Ctrl+Z / Ctrl+Y). Состояния неизменяемы: каждое действие кладёт новое состояние,
 * отмена возвращает предыдущее, новое действие после отмены отбрасывает повторы. Глубина ограничена.
 */
export interface History<T> {
    readonly past: readonly T[];
    readonly present: T;
    readonly future: readonly T[];
}

export const historyLimit = 200;

export function createHistory<T>(present: T): History<T> {
    return { past: [], present, future: [] };
}

/** Новое состояние; то же самое значение (по ссылке) историю не меняет. */
export function record<T>(history: History<T>, next: T): History<T> {
    if (next === history.present) return history;
    const past = [...history.past, history.present];
    return { past: past.length > historyLimit ? past.slice(past.length - historyLimit) : past, present: next, future: [] };
}

export function undo<T>(history: History<T>): History<T> {
    if (!history.past.length) return history;
    return {
        past: history.past.slice(0, -1),
        present: history.past[history.past.length - 1],
        future: [history.present, ...history.future],
    };
}

export function redo<T>(history: History<T>): History<T> {
    if (!history.future.length) return history;
    return { past: [...history.past, history.present], present: history.future[0], future: history.future.slice(1) };
}

export const canUndo = (history: History<unknown>) => history.past.length > 0;
export const canRedo = (history: History<unknown>) => history.future.length > 0;
