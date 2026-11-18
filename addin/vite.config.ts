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

// Public URL the add-in is served from (written into dist/manifest.xml).
// Order: explicit ADDIN_URL → production domain on Vercel → this deployment's own URL (previews) → local dev.
const PRODUCTION_URL = "https://scan2pdf.aghapyts.com";
const addinUrl = (
  process.env.ADDIN_URL ??
  (process.env.VERCEL_ENV === "production"
    ? PRODUCTION_URL
    : process.env.VERCEL_URL
      ? `https://${process.env.VERCEL_URL}`
      : "https://localhost:3000")
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

/** Public pages: preload the main (latin) font files so text renders sooner. Skips the add-in's own pages. */
function preloadFontsPlugin(): Plugin {
  return {
    name: "preload-fonts",
    apply: "build",
    transformIndexHtml: {
      order: "post",
      handler(html, ctx) {
        if (!ctx.bundle || /taskpane|commands/.test(ctx.filename)) return html;
        const fonts = Object.keys(ctx.bundle).filter(
          (f) =>
            f.endsWith(".woff2") &&
            /bricolage-grotesque-latin-standard-normal|atkinson-hyperlegible-latin-(400|700)-normal/.test(f),
        );
        return {
          html,
          tags: fonts.map((f) => ({
            tag: "link",
            attrs: { rel: "preload", as: "font", type: "font/woff2", href: `/${f}`, crossorigin: "" },
            injectTo: "head-prepend" as const,
          })),
        };
      },
    },
  };
}

export default defineConfig({
  plugins: [tailwindcss(), manifestPlugin(), preloadFontsPlugin()],
  server: { port: 3000, strictPort: true, https },
  preview: { port: 3000, strictPort: true, https },
  build: {
    rollupOptions: {
      input: {
        index: resolve(__dirname, "index.html"),
        privacy: resolve(__dirname, "privacy.html"),
        terms: resolve(__dirname, "terms.html"),
        support: resolve(__dirname, "support.html"),
        taskpane: resolve(__dirname, "taskpane.html"),
        commands: resolve(__dirname, "commands.html"),
        phone: resolve(__dirname, "phone.html"),
      },
    },
  },
});
