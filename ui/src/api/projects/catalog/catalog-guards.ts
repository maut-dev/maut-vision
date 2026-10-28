/** Shared checks of catalog DTO fields. */

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export const isId = (value: unknown): value is string => typeof value === "string" && guidPattern.test(value);

export const isDate = (value: unknown): value is string => typeof value === "string" && Number.isFinite(Date.parse(value));

export const isDateOrNull = (value: unknown): value is string | null => value === null || isDate(value);

export const isCount = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) >= 0;

export const isRevision = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) >= 1;

export const isHexColor = (value: unknown): value is string => typeof value === "string" && /^#[0-9a-f]{6}$/.test(value);
