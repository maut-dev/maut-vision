/**
 * URL of a project file served by the host resource channel: https://maut-resource.invalid/r/<token>.
 * DTOs carry it instead of image bytes. It works only in the page that received it and only for a limited time;
 * after expiry or a reload the screen asks the host for a fresh DTO.
 */
export type ResourceUrl = string & { readonly __resourceUrl: true };

const pattern = /^https:\/\/maut-resource\.invalid\/r\/[A-Za-z0-9_-]{22}$/;

export function isResourceUrl(value: unknown): value is ResourceUrl {
    return typeof value === "string" && pattern.test(value);
}

/** For DTO guards of fields that may be absent, e.g. a material without a thumbnail. */
export function isResourceUrlOrNull(value: unknown): value is ResourceUrl | null {
    return value === null || isResourceUrl(value);
}
