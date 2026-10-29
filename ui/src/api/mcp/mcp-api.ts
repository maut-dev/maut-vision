import { nativeBridge } from "../transport/native-bridge";
import type {
    AddMcpAllowedFolderRequestDto,
    AnswerMcpConfirmationRequestDto,
    GetMcpStateRequestDto,
    ListMcpCallsRequestDto,
    ListMcpConfirmationsRequestDto,
    RegenerateMcpTokenRequestDto,
    RemoveMcpAllowedFolderRequestDto,
    UpdateMcpSettingsRequestDto,
} from "./request-dto/mcp-request-dto";
import {
    isMcpCallListResponseDto,
    isMcpConfirmationAnswerResponseDto,
    isMcpConfirmationListResponseDto,
    isMcpStateResponseDto,
} from "./response-dto/mcp-response-dto";

/** The settings dialog "AI assistants (MCP)" and confirmations of AI clients. No MCP tool can reach these methods. */
export const mcpApi = {
    getState(signal?: AbortSignal) {
        const request: GetMcpStateRequestDto = {};
        return nativeBridge.request("mcp.getState", request, isMcpStateResponseDto, signal);
    },
    /** Starting or stopping the server waits for Kestrel: up to a few seconds. */
    update(request: UpdateMcpSettingsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("mcp.update", request, isMcpStateResponseDto, signal, 30_000);
    },
    regenerateToken(signal?: AbortSignal) {
        const request: RegenerateMcpTokenRequestDto = {};
        return nativeBridge.request("mcp.regenerateToken", request, isMcpStateResponseDto, signal, 30_000);
    },
    /** The native folder dialog waits for the user: no time limit. */
    addAllowedFolder(signal?: AbortSignal) {
        const request: AddMcpAllowedFolderRequestDto = {};
        return nativeBridge.request("mcp.addAllowedFolder", request, isMcpStateResponseDto, signal, null);
    },
    removeAllowedFolder(request: RemoveMcpAllowedFolderRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("mcp.removeAllowedFolder", request, isMcpStateResponseDto, signal);
    },
    listCalls(request: ListMcpCallsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("mcp.listCalls", request, isMcpCallListResponseDto, signal);
    },
    listConfirmations(signal?: AbortSignal) {
        const request: ListMcpConfirmationsRequestDto = {};
        return nativeBridge.request("mcp.listConfirmations", request, isMcpConfirmationListResponseDto, signal);
    },
    answerConfirmation(request: AnswerMcpConfirmationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("mcp.answerConfirmation", request, isMcpConfirmationAnswerResponseDto, signal);
    },
};
