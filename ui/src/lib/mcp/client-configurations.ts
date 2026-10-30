import type { McpStateResponseDto } from "@/api/mcp/response-dto/mcp-response-dto";

/** The server name clients show; the same in every configuration. */
export const mcpServerName = "maut-vision";

export interface McpClientConfiguration {
    key: "claudeCode" | "mcpJson" | "claudeDesktop";
    /** Ready to paste, with the token. */
    text: string;
    /** The same with the token hidden, for display. */
    masked: string;
}

/**
 * Ready configurations for clients: the claude mcp add command and .mcp.json of Claude Code (Streamable HTTP with the Bearer token) and the
 * Claude Desktop entry that starts the stdio proxy next to the application.
 */
export function mcpClientConfigurations(state: Pick<McpStateResponseDto, "endpoint" | "token" | "proxyPath">): McpClientConfiguration[] {
    const build = (token: string): Record<McpClientConfiguration["key"], string> => ({
        claudeCode: `claude mcp add --transport http ${mcpServerName} ${state.endpoint} --header "Authorization: Bearer ${token}"`,
        mcpJson: JSON.stringify({ mcpServers: { [mcpServerName]: { type: "http", url: state.endpoint, headers: { Authorization: `Bearer ${token}` } } } }, null, 2),
        claudeDesktop: JSON.stringify({ mcpServers: { [mcpServerName]: { command: state.proxyPath, args: [] } } }, null, 2),
    });
    const real = build(state.token ?? "");
    const hidden = build("•".repeat(12));
    return (["claudeCode", "mcpJson", "claudeDesktop"] as const).map(key => ({ key, text: real[key], masked: hidden[key] }));
}
