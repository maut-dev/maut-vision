export type McpAccessLevel = "read_only" | "read_write" | "full";

export type GetMcpStateRequestDto = Record<string, never>;

/** A change of the MCP settings; omitted fields keep their values. port — 1024–65535. */
export interface UpdateMcpSettingsRequestDto {
    enabled?: boolean;
    port?: number;
    accessLevel?: McpAccessLevel;
    restrictFolders?: boolean;
    launchOnRequest?: boolean;
}

export type RegenerateMcpTokenRequestDto = Record<string, never>;

/** Opens the native folder dialog; the chosen folder joins the allowed folders. */
export type AddMcpAllowedFolderRequestDto = Record<string, never>;

export interface RemoveMcpAllowedFolderRequestDto {
    path: string;
}

/** limit — 1–500 (100 by default); before — sequence of the last call of the previous page. */
export interface ListMcpCallsRequestDto {
    limit?: number;
    before?: number;
}

export type ListMcpConfirmationsRequestDto = Record<string, never>;

/** allow — "Allow", otherwise "Deny". */
export interface AnswerMcpConfirmationRequestDto {
    id: string;
    allow: boolean;
}
