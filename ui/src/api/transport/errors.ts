/**
 * An error of the host or the bridge: code — the stable code the UI translates (errors catalog, see lib/errors.ts), details — values for the
 * text and for the reaction of the UI (for example, field). The host sends no text; message is the code, for logs only.
 */
export class BridgeError extends Error {
    constructor(public readonly code: string, public readonly details: Readonly<Record<string, string>> | null = null) {
        super(code);
        this.name = "BridgeError";
    }
}