export function errorMessage(error: unknown, fallback = "Не удалось выполнить действие. Повторите попытку."): string {
    return error instanceof Error && error.message ? error.message : fallback;
}

export function megabytes(bytes: number): string {
    return `${(bytes / 1024 / 1024).toLocaleString("ru-RU", { maximumFractionDigits: 1 })} МБ`;
}

export function formatDate(value: string): string {
    return new Date(value).toLocaleString("ru-RU", {
        day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit",
    });
}
