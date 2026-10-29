/**
 * Colors of annotation boxes. The color of a binding (a class variant) comes from the catalog (displayColor); a box without a variant
 * takes a color of its class from the same palette as Core (VariantColors): FNV-1a of the ID, so it is stable.
 */
const palette = [
    "#ef4444", "#f97316", "#fcd34d", "#a3e635", "#10b981", "#5eead4",
    "#22d3ee", "#3b82f6", "#a78bfa", "#d946ef", "#f472b6", "#f5f5f4",
] as const;

export const roiColor = "#fcd34d";

export function defaultColor(id: string): string {
    let hash = 0x811c9dc5;
    for (let index = 0; index < id.length; index++) {
        hash ^= id.charCodeAt(index);
        hash = Math.imul(hash, 0x01000193);
    }
    return palette[(hash >>> 0) % palette.length];
}

/** Black or white text over a color, by its relative luminance. */
export function contrastText(color: string): string {
    if (!/^#[0-9a-f]{6}$/i.test(color)) return "#ffffff";
    const channel = (offset: number) => {
        const value = parseInt(color.slice(offset, offset + 2), 16) / 255;
        return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
    };
    const luminance = 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
    return luminance > 0.4 ? "#0a0a0a" : "#ffffff";
}
