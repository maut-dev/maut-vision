/** Series color --series-N: a version by its number (unchecking one does not recolor the others), a class by its output index. */
export function seriesColor(position: number): string {
    return `var(--series-${((Math.max(1, position) - 1) % 8) + 1})`;
}
