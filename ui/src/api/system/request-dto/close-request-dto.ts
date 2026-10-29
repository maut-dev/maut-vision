/** The answer to app.closeRequested: allow — the window may close (there are no unsaved changes). */
export interface CloseDecisionRequestDto {
    requestId: string;
    allow: boolean;
}

/** The UI saved or discarded the changes after an attempt to close the window and asks to close it. */
export type CloseRequestDto = Record<string, never>;
