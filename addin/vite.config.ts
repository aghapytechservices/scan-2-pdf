import { defineConfig } from "vite";
import tailwindcss from "@tailwindcss/vite";
import { resolve } from "node:path";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";

// Dev certs created by `npm run certs` (office-addin-dev-certs). Outlook requires HTTPS.
const certDir = resolve(homedir(), ".office-addin-dev-certs");
const https =
  existsSync(resolve(certDir, "localhost.crt"))
    ? {
        cert: readFileSync(resolve(certDir, "localhost.crt")),
        key: readFileSync(resolve(certDir, "localhost.key")),
        ca: readFileSync(resolve(certDir, "ca.crt")),
      }
    : undefined;

export default defineConfig({
  plugins: [tailwindcss()],
  server: { port: 3000, strictPort: true, https },
  preview: { port: 3000, strictPort: true, https },
  build: {
    rollupOptions: {
      input: {
        taskpane: resolve(__dirname, "taskpane.html"),
        commands: resolve(__dirname, "commands.html"),
      },
    },
  },
});
