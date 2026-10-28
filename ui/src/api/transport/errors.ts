/** Ошибка host: code — стабильный код, message — текст для пользователя, details — значения для реакции UI (например, field). */
export class BridgeError extends Error {
    constructor(public readonly code: string, message: string, public readonly details: Readonly<Record<string, string>> | null = null) {
        super(message);
        this.name = "BridgeError";
    }
}
