/** The Badge variant of a job, run or check state; the badge always carries the state word as well. */
export type StateBadgeVariant = "ok" | "warn" | "danger" | "info" | "brand" | "plain";

/** Running work is brand, waiting is info, cancelling is warn, a success is ok, a failure or an interruption is danger. */
export function stateBadgeVariant(state: string): StateBadgeVariant {
    switch (state) {
        case "succeeded":
            return "ok";
        case "failed":
        case "interrupted":
            return "danger";
        case "cancelling":
            return "warn";
        case "queued":
        case "blocked":
            return "info";
        case "preparing":
        case "training":
        case "running":
            return "brand";
        default:
            return "plain";
    }
}
