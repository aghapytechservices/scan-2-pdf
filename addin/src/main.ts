import "./styles.css";
import { BridgeError, BridgeUnreachableError, ScanBridge, type ColorMode, type PageSize, type ScanSource } from "./bridge";
import { buildPdfBase64, type PageImage } from "./pdf";

const DEFAULT_BRIDGE_URL = "http://127.0.0.1:52801";
const SETTINGS_KEY = "scan2pdf-settings";
const WARN_SIZE_BYTES = 20 * 1024 * 1024;

// ---------- DOM ----------
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const el = {
  statusDot: $<HTMLSpanElement>("statusDot"),
  statusText: $<HTMLSpanElement>("statusText"),
  retryBtn: $<HTMLButtonElement>("retryBtn"),
  bridgeHelp: $<HTMLDivElement>("bridgeHelp"),
  scanner: $<HTMLSelectElement>("scanner"),
  source: $<HTMLSelectElement>("source"),
  color: $<HTMLSelectElement>("color"),
  dpi: $<HTMLSelectElement>("dpi"),
  pageSize: $<HTMLSelectElement>("pageSize"),
  scanBtn: $<HTMLButtonElement>("scanBtn"),
  fileInput: $<HTMLInputElement>("fileInput"),
  pages: $<HTMLUListElement>("pages"),
  pageCount: $<HTMLSpanElement>("pageCount"),
  emptyHint: $<HTMLParagraphElement>("emptyHint"),
  clearBtn: $<HTMLButtonElement>("clearBtn"),
  fileName: $<HTMLInputElement>("fileName"),
  attachBtn: $<HTMLButtonElement>("attachBtn"),
  message: $<HTMLDivElement>("message"),
  bridgeUrl: $<HTMLInputElement>("bridgeUrl"),
  quality: $<HTMLInputElement>("quality"),
  qualityValue: $<HTMLSpanElement>("qualityValue"),
};

// ---------- State ----------
let pages: PageImage[] = [];
let bridgeOnline = false;
let busy = false;

interface Settings {
  bridgeUrl: string;
  scannerId: string | null;
  source: ScanSource;
  color: ColorMode;
  dpi: number;
  pageSize: PageSize;
  quality: number;
}

function loadSettings(): Settings {
  const defaults: Settings = {
    bridgeUrl: DEFAULT_BRIDGE_URL,
    scannerId: null,
    source: "flatbed",
    color: "color",
    dpi: 200,
    pageSize: "letter",
    quality: 75,
  };
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    return raw ? { ...defaults, ...JSON.parse(raw) } : defaults;
  } catch {
    return defaults;
  }
}

function saveSettings() {
  const s: Settings = {
    bridgeUrl: el.bridgeUrl.value.trim() || DEFAULT_BRIDGE_URL,
    scannerId: el.scanner.value || null,
    source: el.source.value as ScanSource,
    color: el.color.value as ColorMode,
    dpi: Number(el.dpi.value),
    pageSize: el.pageSize.value as PageSize,
    quality: Number(el.quality.value),
  };
  try {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
  } catch {
    /* storage unavailable — settings just won't persist */
  }
}

const settings = loadSettings();
const bridge = new ScanBridge(settings.bridgeUrl);

// ---------- UI helpers ----------
function setStatus(state: "connecting" | "online" | "offline" | "noscanner", text: string) {
  const colors = { connecting: "bg-slate-300", online: "bg-emerald-500", offline: "bg-red-500", noscanner: "bg-amber-500" };
  el.statusDot.className = `h-2.5 w-2.5 rounded-full ${colors[state]}`;
  el.statusText.textContent = text;
  el.retryBtn.classList.toggle("hidden", state === "online" || state === "connecting");
  el.bridgeHelp.classList.toggle("hidden", state !== "offline");
}

function showMessage(kind: "info" | "success" | "error", text: string) {
  const styles = {
    info: "bg-blue-50 text-blue-900 border border-blue-200",
    success: "bg-emerald-50 text-emerald-900 border border-emerald-200",
    error: "bg-red-50 text-red-900 border border-red-200",
  };
  el.message.className = `rounded-md px-2.5 py-2 text-xs ${styles[kind]}`;
  el.message.textContent = text;
}

function clearMessage() {
  el.message.className = "hidden";
}

function refreshButtons() {
  el.scanBtn.disabled = busy || !bridgeOnline || !el.scanner.value;
  el.attachBtn.disabled = busy || pages.length === 0;
  el.scanBtn.textContent = busy && bridgeOnline ? "Working…" : pages.length ? "Scan more pages" : "Scan";
}

function defaultFileName() {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `Scan_${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}_${pad(d.getHours())}${pad(d.getMinutes())}`;
}

function sanitizeFileName(name: string) {
  const cleaned = name.replace(/\.pdf$/i, "").replace(/[\\/:*?"<>|]+/g, "_").trim();
  return (cleaned || defaultFileName()) + ".pdf";
}

const formatSize = (bytes: number) =>
  bytes > 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;

// ---------- Pages ----------
function loadImageSize(dataUrl: string): Promise<{ width: number; height: number }> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve({ width: img.naturalWidth, height: img.naturalHeight });
    img.onerror = () => reject(new Error("Could not read image"));
    img.src = dataUrl;
  });
}

async function addPage(dataUrl: string, dpi: number | null) {
  const { width, height } = await loadImageSize(dataUrl);
  pages.push({ id: crypto.randomUUID(), dataUrl, rotation: 0, dpi, width, height });
}

function iconButton(label: string, title: string, onClick: () => void, danger = false) {
  const b = document.createElement("button");
  b.type = "button";
  b.title = title;
  b.textContent = label;
  b.className = `h-6 min-w-6 rounded bg-white/90 px-1 text-xs shadow-sm hover:bg-white ${danger ? "text-red-700" : "text-slate-700"}`;
  b.addEventListener("click", onClick);
  return b;
}

function renderPages() {
  el.pages.replaceChildren();
  pages.forEach((p, i) => {
    const li = document.createElement("li");
    li.className = "thumb group relative overflow-hidden rounded border border-slate-200 bg-slate-50";

    const frame = document.createElement("div");
    frame.className = "flex aspect-[3/4] items-center justify-center p-1";
    const img = document.createElement("img");
    img.src = p.dataUrl;
    img.alt = `Page ${i + 1}`;
    img.className = "max-h-full max-w-full shadow-sm";
    img.style.transform = `rotate(${p.rotation}deg)`;
    if (p.rotation % 180 !== 0) img.style.scale = String(Math.min(1, p.height / p.width, p.width / p.height) || 1);
    frame.append(img);

    const label = document.createElement("span");
    label.className = "absolute bottom-1 left-1 rounded bg-slate-800/70 px-1.5 text-[10px] text-white";
    label.textContent = String(i + 1);

    const tools = document.createElement("div");
    tools.className = "absolute right-1 top-1 flex gap-1";
    tools.append(
      iconButton("◀", "Move earlier", () => movePage(i, -1)),
      iconButton("▶", "Move later", () => movePage(i, 1)),
      iconButton("⟳", "Rotate", () => {
        p.rotation = (p.rotation + 90) % 360;
        renderPages();
      }),
      iconButton("✕", "Remove page", () => {
        pages.splice(i, 1);
        renderPages();
      }, true),
    );

    li.append(frame, label, tools);
    el.pages.append(li);
  });

  el.pageCount.textContent = `(${pages.length})`;
  el.emptyHint.classList.toggle("hidden", pages.length > 0);
  el.clearBtn.classList.toggle("hidden", pages.length === 0);
  refreshButtons();
}

function movePage(i: number, delta: number) {
  const j = i + delta;
  if (j < 0 || j >= pages.length) return;
  [pages[i], pages[j]] = [pages[j], pages[i]];
  renderPages();
}

// ---------- Bridge ----------
async function connect() {
  setStatus("connecting", "Connecting to scanner service…");
  bridgeOnline = false;
  refreshButtons();
  try {
    await bridge.health();
    const scanners = await bridge.listScanners();
    el.scanner.replaceChildren(
      ...scanners.map((s) => {
        const o = document.createElement("option");
        o.value = s.id;
        o.textContent = s.manufacturer && !s.name.includes(s.manufacturer) ? `${s.name} (${s.manufacturer})` : s.name;
        return o;
      }),
    );
    if (settings.scannerId && scanners.some((s) => s.id === settings.scannerId)) el.scanner.value = settings.scannerId;

    bridgeOnline = true;
    if (scanners.length === 0) setStatus("noscanner", "Scanner service is running, but no scanner was found.");
    else setStatus("online", scanners.length === 1 ? "Scanner ready" : `${scanners.length} scanners available`);
  } catch (err) {
    el.scanner.replaceChildren();
    if (err instanceof BridgeUnreachableError) setStatus("offline", "Scanner service not reachable");
    else setStatus("noscanner", err instanceof Error ? err.message : String(err));
  }
  refreshButtons();
}

async function scan() {
  clearMessage();
  busy = true;
  refreshButtons();
  saveSettings();
  const dpi = Number(el.dpi.value);
  const source = el.source.value as ScanSource;
  showMessage("info", source === "flatbed" ? "Scanning…" : "Scanning from the feeder…");

  try {
    const scanned = await bridge.scan({
      deviceId: el.scanner.value || null,
      source,
      dpi,
      color: el.color.value as ColorMode,
      pageSize: el.pageSize.value as PageSize,
      jpegQuality: Number(el.quality.value),
    });
    for (const b64 of scanned) await addPage(`data:image/jpeg;base64,${b64}`, dpi);
    renderPages();
    showMessage(
      "success",
      `Added ${scanned.length} page${scanned.length === 1 ? "" : "s"}.` +
        (source === "flatbed" ? " Place the next page and scan again, or attach the PDF." : ""),
    );
  } catch (err) {
    if (err instanceof BridgeUnreachableError) {
      setStatus("offline", "Lost connection to the scanner service");
      bridgeOnline = false;
      showMessage("error", "The scanner service stopped responding.");
    } else {
      showMessage("error", err instanceof BridgeError ? err.message : `Scan failed: ${String(err)}`);
    }
  } finally {
    busy = false;
    refreshButtons();
  }
}

// ---------- Attach ----------
function addAttachment(base64: string, name: string): Promise<string> {
  return new Promise((resolve, reject) => {
    Office.context.mailbox.item!.addFileAttachmentFromBase64Async(base64, name, { isInline: false }, (r) =>
      r.status === Office.AsyncResultStatus.Succeeded ? resolve(r.value) : reject(r.error),
    );
  });
}

async function attach() {
  if (!pages.length) return;
  busy = true;
  refreshButtons();
  clearMessage();
  const name = sanitizeFileName(el.fileName.value);

  try {
    showMessage("info", "Creating PDF…");
    const { base64, size } = await buildPdfBase64(pages, name.replace(/\.pdf$/, ""));
    if (size > WARN_SIZE_BYTES) {
      showMessage(
        "error",
        `The PDF is ${formatSize(size)}, which may exceed your mail server's limit. ` +
          "Try a lower resolution, grayscale, or lower JPEG quality (Advanced).",
      );
      return;
    }
    showMessage("info", `Attaching ${name} (${formatSize(size)})…`);
    await addAttachment(base64, name);

    showMessage("success", `Attached ${name} (${pages.length} page${pages.length === 1 ? "" : "s"}, ${formatSize(size)}).`);
    pages = [];
    el.fileName.value = defaultFileName();
    renderPages();
  } catch (err) {
    const msg = (err as Office.Error)?.message ?? String(err);
    showMessage("error", `Couldn't attach the PDF: ${msg}`);
  } finally {
    busy = false;
    refreshButtons();
  }
}

// ---------- File fallback ----------
function readAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result as string);
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
}

async function addFiles(files: FileList) {
  let added = 0;
  for (const f of Array.from(files)) {
    if (!/^image\/(jpeg|png)$/.test(f.type)) continue;
    await addPage(await readAsDataUrl(f), null);
    added++;
  }
  renderPages();
  if (added) showMessage("success", `Added ${added} image${added === 1 ? "" : "s"}.`);
  else showMessage("error", "Only JPG and PNG images can be added.");
}

// ---------- Init ----------
Office.onReady((info) => {
  if (info.host !== Office.HostType.Outlook) {
    showMessage("error", "This add-in runs in Outlook.");
  } else if (!Office.context.requirements.isSetSupported("Mailbox", "1.8")) {
    showMessage("error", "This version of Outlook doesn't support adding attachments from add-ins (needs Mailbox 1.8).");
  }

  el.bridgeUrl.value = settings.bridgeUrl;
  el.source.value = settings.source;
  el.color.value = settings.color;
  el.dpi.value = String(settings.dpi);
  el.pageSize.value = settings.pageSize;
  el.quality.value = String(settings.quality);
  el.qualityValue.textContent = String(settings.quality);
  el.fileName.value = defaultFileName();

  el.retryBtn.addEventListener("click", connect);
  el.scanBtn.addEventListener("click", scan);
  el.attachBtn.addEventListener("click", attach);
  el.clearBtn.addEventListener("click", () => {
    pages = [];
    renderPages();
    clearMessage();
  });
  el.fileInput.addEventListener("change", () => {
    if (el.fileInput.files?.length) void addFiles(el.fileInput.files);
    el.fileInput.value = "";
  });
  for (const s of [el.scanner, el.source, el.color, el.dpi, el.pageSize]) s.addEventListener("change", saveSettings);
  el.quality.addEventListener("input", () => (el.qualityValue.textContent = el.quality.value));
  el.quality.addEventListener("change", saveSettings);
  el.bridgeUrl.addEventListener("change", () => {
    saveSettings();
    bridge.setBaseUrl(el.bridgeUrl.value.trim() || DEFAULT_BRIDGE_URL);
    void connect();
  });

  renderPages();
  void connect();
});
