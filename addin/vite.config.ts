import { defineConfig, type Plugin } from "vite";
import tailwindcss from "@tailwindcss/vite";
import { resolve } from "node:path";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
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

// Public URL the add-in is served from. Set ADDIN_URL explicitly, or let Vercel provide its production domain.
const addinUrl = (
  process.env.ADDIN_URL ??
  (process.env.VERCEL_PROJECT_PRODUCTION_URL ? `https://${process.env.VERCEL_PROJECT_PRODUCTION_URL}` : "https://localhost:3000")
).replace(/\/+$/, "");

/** Writes dist/manifest.xml with every https://localhost:3000 replaced by the hosting URL. */
function manifestPlugin(): Plugin {
  return {
    name: "addin-manifest",
    apply: "build",
    closeBundle() {
      const xml = readFileSync(resolve(__dirname, "manifest.xml"), "utf8").replaceAll("https://localhost:3000", addinUrl);
      writeFileSync(resolve(__dirname, "dist/manifest.xml"), xml);
      console.log(`manifest.xml → ${addinUrl}`);
    },
  };
}

export default defineConfig({
  plugins: [tailwindcss(), manifestPlugin()],
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
