import QRCode from "qrcode";
import { closeSession, createSession, discardPage, linkUrl, listPages, PhoneApiError, takePage, type PanelSession } from "./phonelink";

/**
 * "Scan with phone" in the Outlook panel: shows the QR code, then picks up each page the phone
 * sends, decrypts it and hands it to the page list.
 */

export interface PhonePage {
  dataUrl: string;
  width: number;
  height: number;
}

interface Options {
  /** Called for each page, in the order the phone sent them. */
  onPage: (page: PhonePage) => Promise<void> | void;
  /** Called when the phone says it's done (after its pages have been added). */
  onFinished?: (count: number) => void;
}

const POLL_MS = 2000;
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

export function initPhonePanel(opts: Options) {
  const ui = {
    btn: $<HTMLButtonElement>("phoneBtn"),
    panel: $("phonePanel"),
    close: $<HTMLButtonElement>("phoneClose"),
    qrBox: $("phoneQrBox"),
    qr: $<HTMLCanvasElement>("phoneQr"),
    steps: $("phoneSteps"),
    dot: $("phoneDot"),
    status: $("phoneStatusText"),
    expiry: $("phoneExpiry"),
    renew: $<HTMLButtonElement>("phoneRenew"),
  };

  let session: PanelSession | null = null;
  let timer = 0;
  let expiresAt = 0;
  let received = 0;
  let generation = 0; // ignores late results from a closed session
  let polling = false;

  function status(text: string, tone: "wait" | "ok" | "bad" = "wait") {
    ui.status.textContent = text;
    ui.dot.className = `h-2 w-2 rounded-full ${tone === "ok" ? "bg-emerald-500" : tone === "bad" ? "bg-red-500" : "bg-slate-400 animate-pulse"}`;
  }

  function stop() {
    clearTimeout(timer);
    timer = 0;
  }

  async function open() {
    const gen = ++generation;
    stop();
    if (session) void closeSession(session);
    session = null;
    received = 0;
    ui.panel.classList.remove("hidden");
    ui.btn.classList.add("hidden");
    ui.qrBox.classList.remove("hidden");
    ui.steps.classList.remove("hidden");
    ui.renew.classList.add("hidden");
    ui.expiry.textContent = "";
    status("Setting up…");
    try {
      const s = await createSession();
      if (gen !== generation) return void closeSession(s);
      session = s;
      expiresAt = Date.now() + 15 * 60_000;
      await QRCode.toCanvas(ui.qr, linkUrl(location.origin, s), {
        width: 180,
        margin: 1,
        errorCorrectionLevel: "M",
        color: { dark: "#0e1b36", light: "#ffffff" },
      });
      status("Waiting for your phone…");
      schedule(gen);
    } catch (err) {
      status(err instanceof Error ? err.message : "Couldn't start phone scanning.", "bad");
      ui.qrBox.classList.add("hidden");
      ui.renew.textContent = "Try again";
      ui.renew.classList.remove("hidden");
    }
  }

  function close() {
    generation++;
    stop();
    if (session) void closeSession(session);
    session = null;
    ui.panel.classList.add("hidden");
    ui.btn.classList.remove("hidden");
  }

  function schedule(gen: number) {
    stop();
    timer = window.setTimeout(() => void poll(gen), POLL_MS);
  }

  function showExpiry() {
    const left = Math.max(0, Math.round((expiresAt - Date.now()) / 1000));
    ui.expiry.textContent = left > 0 ? `Code works for ${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")} more.` : "";
  }

  function expired() {
    stop();
    session = null;
    ui.qrBox.classList.add("hidden");
    ui.steps.classList.add("hidden");
    ui.expiry.textContent = "";
    status(received ? `Code expired. ${received} page${received === 1 ? "" : "s"} added.` : "This code has expired.", "bad");
    ui.renew.textContent = "Show a new code";
    ui.renew.classList.remove("hidden");
  }

  async function poll(gen: number) {
    if (gen !== generation || !session || polling) return;
    if (document.hidden) return schedule(gen); // don't poll while the panel isn't visible
    polling = true;
    const s = session;
    try {
      const state = await listPages(s);
      if (gen !== generation) return;
      for (const index of state.pages) {
        let jpeg: ArrayBuffer;
        try {
          jpeg = await takePage(s, index);
        } catch (err) {
          if (err instanceof PhoneApiError) throw err;
          // Not encrypted with this panel's key (or damaged): drop it rather than retry forever.
          await discardPage(s, index);
          continue;
        }
        if (gen !== generation) return;
        const page = await toPage(jpeg);
        await opts.onPage(page);
        received++;
      }
      if (gen !== generation) return;
      expiresAt = Date.now() + state.expiresIn * 1000;
      showExpiry();

      if (state.done && state.pages.length === 0) {
        opts.onFinished?.(received);
        close();
        return;
      }
      if (received) {
        status(`${received} page${received === 1 ? "" : "s"} received. Keep going, or tap “I'm done” on your phone.`, "ok");
        ui.qrBox.classList.add("hidden"); // the phone is clearly connected: give the room to the pages
        ui.steps.classList.add("hidden");
      } else if (state.phoneSeen) {
        status("Phone connected. Take a photo of the first page.", "ok");
      }
      if (state.expiresIn <= 0) return expired();
    } catch (err) {
      if (gen !== generation) return;
      if (err instanceof PhoneApiError && (err.status === 410 || err.status === 403)) return expired();
      status(err instanceof Error ? `${err.message} Retrying…` : "Connection problem. Retrying…", "bad");
    } finally {
      polling = false;
    }
    schedule(gen);
  }

  ui.btn.addEventListener("click", () => void open());
  ui.renew.addEventListener("click", () => void open());
  ui.close.addEventListener("click", close);
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden && session) schedule(generation);
  });
  // Outlook closes the panel: delete what's left on the server now rather than waiting for expiry.
  window.addEventListener("pagehide", () => {
    if (session) void closeSession(session);
  });
}

async function toPage(jpeg: ArrayBuffer): Promise<PhonePage> {
  const blob = new Blob([jpeg], { type: "image/jpeg" });
  const dataUrl = await new Promise<string>((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result as string);
    r.onerror = () => reject(r.error);
    r.readAsDataURL(blob);
  });
  const img = new Image();
  img.src = dataUrl;
  await img.decode();
  return { dataUrl, width: img.naturalWidth, height: img.naturalHeight };
}
