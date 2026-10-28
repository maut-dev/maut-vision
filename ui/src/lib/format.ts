import { currentLanguage, i18n } from "@/i18n";

export { errorMessage } from "./errors";

/** The Intl locale of the interface language: numbers, dates and sorting follow the language the user chose. */
export function locale(): string {
    return currentLanguage() === "ru" ? "ru-RU" : "en-US";
}

export function formatNumber(value: number, options?: Intl.NumberFormatOptions): string {
    return value.toLocaleString(locale(), options);
}

/** Sizes: «512 Б», «1,5 МБ», «2,25 ГБ» / «1.5 MB». */
export function formatBytes(bytes: number): string {
    if (bytes < 1024) return i18n.t("units.bytes", { value: formatNumber(bytes) });
    if (bytes < 1024 ** 2) return i18n.t("units.kilobytes", { value: formatNumber(bytes / 1024, { maximumFractionDigits: 1 }) });
    if (bytes < 1024 ** 3) return i18n.t("units.megabytes", { value: formatNumber(bytes / 1024 ** 2, { maximumFractionDigits: 1 }) });
    return i18n.t("units.gigabytes", { value: formatNumber(bytes / 1024 ** 3, { maximumFractionDigits: 2 }) });
}

export function megabytes(bytes: number): string {
    return i18n.t("units.megabytes", { value: formatNumber(bytes / 1024 / 1024, { maximumFractionDigits: 1 }) });
}

/** Durations: «1 ч 5 мин», «2 мин 3 с», «45 с» / «1 h 5 min»… */
export function formatDuration(milliseconds: number): string {
    const seconds = Math.max(0, Math.round(milliseconds / 1000));
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const rest = seconds % 60;
    if (hours) return i18n.t("units.hoursMinutes", { hours: formatNumber(hours), minutes: formatNumber(minutes) });
    if (minutes) return i18n.t("units.minutesSeconds", { minutes: formatNumber(minutes), seconds: formatNumber(rest) });
    return i18n.t("units.seconds", { seconds: formatNumber(rest) });
}

/** «28 октября 2026 г., 14:05» / «October 28, 2026 at 02:05 PM». */
export function formatDate(value: string): string {
    return new Date(value).toLocaleString(locale(), {
        day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit",
    });
}

/** Date with time in the short form of the locale. */
export function formatDateTime(value: string | number | Date): string {
    return new Date(value).toLocaleString(locale());
}

export function formatDay(value: string | number | Date): string {
    return new Date(value).toLocaleDateString(locale());
}

export function formatClock(value: string | number | Date): string {
    return new Date(value).toLocaleTimeString(locale());
}

/** Sorting of names in the order of the interface language. */
export function compareText(left: string, right: string): number {
    return left.localeCompare(right, locale());
}
