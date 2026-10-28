import { fileURLToPath } from "node:url";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Hosts must match WebViewPolicy: the UI document and the resource channel (images and video by handle).
const resourceOrigin = "https://maut-resource.invalid";
const contentSecurityPolicy = [
    "default-src 'self'",
    "script-src 'self'",
    // React and Radix set style attributes.
    "style-src 'self' 'unsafe-inline'",
    `img-src 'self' data: blob: ${resourceOrigin}`,
    `media-src blob: ${resourceOrigin}`,
    `connect-src 'self' ${resourceOrigin}`,
    "font-src 'self' data:",
    "object-src 'none'",
    "frame-src 'none'",
    "base-uri 'none'",
    "form-action 'none'",
].join("; ");

// Only the shipped build: the Vite dev server needs inline scripts and its own websocket.
function contentSecurityPolicyMeta(): Plugin {
    return {
        name: "maut-content-security-policy",
        apply: "build",
        transformIndexHtml: () => [{
            tag: "meta",
            attrs: { "http-equiv": "Content-Security-Policy", content: contentSecurityPolicy },
            injectTo: "head-prepend",
        }],
    };
}

export default defineConfig({
    base: "./",
    plugins: [react(), tailwindcss(), contentSecurityPolicyMeta()],
    resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
    build: {
        outDir: "dist",
        emptyOutDir: true,
        // The page loads from the application directory, not over a network: the main chunk (React, Radix and the shell, ~560 kB) costs
        // a disk read, while sections stay separate lazy chunks. The limit only keeps the warning for real growth.
        chunkSizeWarningLimit: 1024,
    },
});
