import { isRecord } from "../../transport/envelopes";
import type { McpAccessLevel } from "../request-dto/mcp-request-dto";

export type McpServerStatus = "stopped" | "starting" | "running" | "port_busy" | "failed";

/**
 * The MCP settings and the server state (also the event mcp.stateChanged). statusCode — the error code of port_busy and failed; endpoint —
 * the URL clients connect to; proxyPath — the stdio proxy for clients that speak only stdio, proxyAvailable — it ships with this build;
 * token — null until the server was turned on.
 */
export interface McpStateResponseDto {
    enabled: boolean;
    port: number;
    accessLevel: McpAccessLevel;
    token: string | null;
    restrictFolders: boolean;
    allowedFolders: string[];
    launchOnRequest: boolean;
    status: McpServerStatus;
    statusCode: string | null;
    endpoint: string;
    proxyPath: string;
    proxyAvailable: boolean;
}

/** A tool call of the log (also mcp.callAdded): arguments — JSON without file contents; error — the code of a failed call. */
export interface McpCallResponseDto {
    sequence: number;
    time: string;
    client: string;
    tool: string;
    arguments: string;
    succeeded: boolean;
    error: string | null;
    result: string | null;
    durationMs: number;
}

/** A question before a tool acts (also mcp.confirmationRequested): action — a key of mcp:confirm.actions with its values. */
export interface McpConfirmationResponseDto {
    id: string;
    client: string;
    tool: string;
    action: string;
    values: Record<string, string>;
    requestedAt: string;
    expiresAt: string;
}

export interface McpConfirmationAnswerResponseDto {
    answered: boolean;
}

const levels: readonly string[] = ["read_only", "read_write", "full"];
const statuses: readonly string[] = ["stopped", "starting", "running", "port_busy", "failed"];

export function isMcpStateResponseDto(value: unknown): value is McpStateResponseDto {
    return isRecord(value) && typeof value.enabled === "boolean" && typeof value.port === "number" && levels.includes(value.accessLevel as string)
        && (value.token === null || typeof value.token === "string") && typeof value.restrictFolders === "boolean"
        && Array.isArray(value.allowedFolders) && value.allowedFolders.every(item => typeof item === "string")
        && typeof value.launchOnRequest === "boolean" && statuses.includes(value.status as string)
        && (value.statusCode === null || typeof value.statusCode === "string") && typeof value.endpoint === "string"
        && typeof value.proxyPath === "string" && typeof value.proxyAvailable === "boolean";
}

export function isMcpCallResponseDto(value: unknown): value is McpCallResponseDto {
    return isRecord(value) && typeof value.sequence === "number" && typeof value.time === "string" && typeof value.client === "string"
        && typeof value.tool === "string" && typeof value.arguments === "string" && typeof value.succeeded === "boolean"
        && (value.error === null || typeof value.error === "string") && (value.result === null || typeof value.result === "string")
        && typeof value.durationMs === "number";
}

export function isMcpCallListResponseDto(value: unknown): value is McpCallResponseDto[] {
    return Array.isArray(value) && value.every(isMcpCallResponseDto);
}

export function isMcpConfirmationResponseDto(value: unknown): value is McpConfirmationResponseDto {
    return isRecord(value) && typeof value.id === "string" && typeof value.client === "string" && typeof value.tool === "string"
        && typeof value.action === "string" && isRecord(value.values) && Object.values(value.values).every(item => typeof item === "string")
        && typeof value.requestedAt === "string" && typeof value.expiresAt === "string";
}

export function isMcpConfirmationListResponseDto(value: unknown): value is McpConfirmationResponseDto[] {
    return Array.isArray(value) && value.every(isMcpConfirmationResponseDto);
}

export function isMcpConfirmationAnswerResponseDto(value: unknown): value is McpConfirmationAnswerResponseDto {
    return isRecord(value) && typeof value.answered === "boolean";
}
