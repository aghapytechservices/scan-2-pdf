import "./styles.css";
import { BridgeError, BridgeUnreachableError, ScanBridge, type ColorMode, type PageSize, type ScanSource } from "./bridge";
import { buildPdfBase64, pageToJpegBase64, type PageImage } from "./pdf";
import { initFeedback, recordSuccessfulAttach, setBridgeEdition } from "./feedback";
import { PdfReadError, readPdf, type PdfPageSource } from "./pdfsource";

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
  fileLabel: $<HTMLLabelElement>("fileLabel"),
  scanControls: $<HTMLDivElement>("scanControls"),
  macHelp: $<HTMLDivElement>("macHelp"),
  dropHint: $<HTMLDivElement>("dropHint"),
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
  fileExt: $<HTMLSpanElement>("fileExt"),
  attachLabel: $<HTMLSpanElement>("attachLabel"),
  year: $<HTMLSpanElement>("year"),
  preview: $<HTMLDivElement>("preview"),
  previewTitle: $<HTMLSpanElement>("previewTitle"),
  previewImg: $<HTMLImageElement>("previewImg"),
  previewInfo: $<HTMLParagraphElement>("previewInfo"),
  previewPrev: $<HTMLButtonElement>("previewPrev"),
  previewNext: $<HTMLButtonElement>("previewNext"),
  previewRotate: $<HTMLButtonElement>("previewRotate"),
  previewDelete: $<HTMLButtonElement>("previewDelete"),
  previewClose: $<HTMLButtonElement>("previewClose"),
};

type OutputFormat = "pdf" | "jpeg";
const formatRadios = () => Array.from(document.querySelectorAll<HTMLInputElement>('input[name="format"]'));
const getFormat = (): OutputFormat => (formatRadios().find((r) => r.checked)?.value as OutputFormat) ?? "pdf";

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
  format: OutputFormat;
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
    format: "pdf",
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
    format: getFormat(),
  };
  try {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
  } catch {
    /* storage unavailable — settings just won't persist */
  }
}

const settings = loadSettings();
const bridge = new ScanBridge(settings.bridgeUrl);

// ---------- Platform ----------
/** Scanning needs the Windows bridge. Elsewhere (Mac, Linux, ChromeOS) we guide people to add files instead. */
type Platform = "windows" | "mac" | "other";
function detectPlatform(): Platform {
  try {
    const p = Office.context.platform;
    if (p === Office.PlatformType.PC) return "windows";
    if (p === Office.PlatformType.Mac) return "mac";
  } catch {
    /* fall through to the user agent */
  }
  const ua = navigator.userAgent;
  if (/Windows/i.test(ua)) return "windows";
  if (/Macintosh|Mac OS X/i.test(ua)) return "mac";
  return "other";
}
let platform: Platform = "windows";

// ---------- UI helpers ----------
type Status = "connecting" | "online" | "offline" | "noscanner" | "files";
function setStatus(state: Status, text: string) {
  const colors = {
    connecting: "bg-slate-300",
    online: "bg-emerald-500",
    offline: "bg-red-500",
    noscanner: "bg-amber-500",
    files: "bg-blue-500",
  };
  el.statusDot.className = `h-2.5 w-2.5 rounded-full ${colors[state]}`;
  el.statusText.textContent = text;
  el.retryBtn.classList.toggle("hidden", state === "online" || state === "connecting" || state === "files");
  el.bridgeHelp.classList.toggle("hidden", state !== "offline");
  // Files-only mode (no bridge on this platform): hide scanner controls, show guidance, promote "Add files".
  const filesOnly = state === "files";
  el.macHelp.classList.toggle("hidden", !filesOnly);
  el.scanControls.classList.toggle("hidden", filesOnly);
  el.fileLabel.className = filesOnly
    ? "block w-full cursor-pointer rounded-md bg-blue-700 px-3 py-2 text-center font-semibold text-white hover:bg-blue-800"
    : "block cursor-pointer text-center text-xs text-blue-700 hover:underline";
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
  refreshFormatUi();
  el.scanBtn.textContent = busy && bridgeOnline ? "Working…" : pages.length ? "Scan more pages" : "Scan";
}

function defaultFileName() {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `Scan_${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}_${pad(d.getHours())}${pad(d.getMinutes())}`;
}

/** File name without extension, safe for attachments. */
function baseFileName(name: string) {
  const cleaned = name.replace(/\.(pdf|jpe?g)$/i, "").replace(/[\\/:*?"<>|]+/g, "_").trim();
  return cleaned || defaultFileName();
}

function refreshFormatUi() {
  const jpeg = getFormat() === "jpeg";
  el.fileExt.textContent = jpeg ? ".jpg" : ".pdf";
  el.attachLabel.textContent = jpeg
    ? pages.length > 1 ? `Attach ${pages.length} JPEGs to email` : "Attach JPEG to email"
    : "Attach PDF to email";
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

let idCounter = 0;
const newId = () => `p${Date.now().toString(36)}${(idCounter++).toString(36)}`;

async function addPage(dataUrl: string, dpi: number | null, pdf?: PdfPageSource, size?: { width: number; height: number }) {
  const { width, height } = size ?? (await loadImageSize(dataUrl));
  pages.push({ id: newId(), dataUrl, rotation: 0, dpi, width, height, pdf });
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

    const frame = document.createElement("button");
    frame.type = "button";
    frame.title = "Click to preview";
    frame.className = "flex aspect-[3/4] w-full cursor-zoom-in items-center justify-center p-1";
    frame.addEventListener("click", () => openPreview(i));
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
  renderPreview();
}

// ---------- Preview ----------
let previewIndex = -1;

function openPreview(i: number) {
  previewIndex = i;
  el.preview.classList.remove("hidden");
  el.preview.classList.add("flex");
  renderPreview();
  el.previewClose.focus();
}

function closePreview() {
  previewIndex = -1;
  el.preview.classList.add("hidden");
  el.preview.classList.remove("flex");
}

function renderPreview() {
  if (previewIndex < 0) return;
  if (pages.length === 0) return closePreview();
  previewIndex = Math.min(previewIndex, pages.length - 1);
  const p = pages[previewIndex];

  el.previewTitle.textContent = `Page ${previewIndex + 1} of ${pages.length}`;
  el.previewImg.src = p.dataUrl;
  el.previewImg.alt = `Page ${previewIndex + 1}`;
  el.previewImg.style.transform = `rotate(${p.rotation}deg)`;

  // When turned sideways, shrink so the rotated page still fits the stage.
  const stage = el.previewImg.parentElement!.getBoundingClientRect();
  if (p.rotation % 180 !== 0 && stage.width && stage.height) {
    const fit = Math.min(stage.width / p.height, stage.height / p.width);
    const shown = Math.min(stage.width / p.width, stage.height / p.height);
    el.previewImg.style.scale = String(Math.min(1, fit / shown));
  } else {
    el.previewImg.style.scale = "1";
  }

  const dims = p.dpi
    ? `${(p.width / p.dpi).toFixed(1)} × ${(p.height / p.dpi).toFixed(1)} in · ${p.dpi} dpi`
    : `${p.width} × ${p.height} px`;
  el.previewInfo.textContent = `${dims}${p.rotation ? ` · rotated ${p.rotation}°` : ""}`;
  el.previewPrev.disabled = previewIndex === 0;
  el.previewNext.disabled = previewIndex === pages.length - 1;
}

function stepPreview(delta: number) {
  const j = previewIndex + delta;
  if (j >= 0 && j < pages.length) {
    previewIndex = j;
    renderPreview();
  }
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
    const health = await bridge.health();
    setBridgeEdition(health.edition, () => bridge.requestStoreReview());
    const scanners = await bridge.listScanners();
    const group = document.createElement("optgroup");
    group.label = `${scanners.length} scanner${scanners.length === 1 ? "" : "s"} available`;
    group.append(
      ...scanners.map((s) => {
        const o = document.createElement("option");
        o.value = s.id;
        o.textContent = s.manufacturer && !s.name.includes(s.manufacturer) ? `${s.name} (${s.manufacturer})` : s.name;
        return o;
      }),
    );
    el.scanner.replaceChildren(...(scanners.length ? [group] : []));
    if (settings.scannerId && scanners.some((s) => s.id === settings.scannerId)) el.scanner.value = settings.scannerId;

    bridgeOnline = true;
    if (scanners.length === 0) setStatus("noscanner", "Scanner service is running, but no scanner was found.");
    else setStatus("online", "Scanner ready");
  } catch (err) {
    el.scanner.replaceChildren();
    if (err instanceof BridgeUnreachableError && platform !== "windows")
      setStatus("files", platform === "mac" ? "Add scanned files on Mac" : "Add scanned files");
    else if (err instanceof BridgeUnreachableError) setStatus("offline", "Scanner service not reachable");
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
  const base = baseFileName(el.fileName.value);
  const format = getFormat();
  const tooBig = (size: number, what: string) =>
    `${what} ${formatSize(size)}, which may exceed your mail server's limit. ` +
    "Try a lower resolution, grayscale, or lower JPEG quality (Advanced).";

  try {
    if (format === "pdf") {
      const name = `${base}.pdf`;
      showMessage("info", "Creating PDF…");
      const { base64, size } = await buildPdfBase64(pages, base);
      if (size > WARN_SIZE_BYTES) return showMessage("error", tooBig(size, "The PDF is"));

      showMessage("info", `Attaching ${name} (${formatSize(size)})…`);
      await addAttachment(base64, name);
      showMessage("success", `Attached ${name} (${pages.length} page${pages.length === 1 ? "" : "s"}, ${formatSize(size)}).`);
    } else {
      showMessage("info", "Preparing JPEG images…");
      const quality = Number(el.quality.value);
      const files: { name: string; base64: string }[] = [];
      const digits = String(pages.length).length < 2 ? 2 : String(pages.length).length;
      for (const [i, p] of pages.entries()) {
        files.push({
          name: pages.length === 1 ? `${base}.jpg` : `${base}_${String(i + 1).padStart(digits, "0")}.jpg`,
          base64: await pageToJpegBase64(p, quality),
        });
      }
      const total = files.reduce((n, f) => n + Math.floor((f.base64.length * 3) / 4), 0);
      if (total > WARN_SIZE_BYTES) return showMessage("error", tooBig(total, "The images total"));

      for (const [i, f] of files.entries()) {
        showMessage("info", `Attaching ${f.name} (${i + 1} of ${files.length})…`);
        await addAttachment(f.base64, f.name);
      }
      showMessage(
        "success",
        files.length === 1
          ? `Attached ${files[0].name} (${formatSize(total)}).`
          : `Attached ${files.length} JPEG images (${formatSize(total)} total).`,
      );
    }

    pages = [];
    el.fileName.value = defaultFileName();
    renderPages();
    recordSuccessfulAttach();
  } catch (err) {
    const msg = (err as Office.Error)?.message ?? String(err);
    showMessage("error", `Couldn't attach the ${format === "pdf" ? "PDF" : "images"}: ${msg}`);
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

const isPdf = (f: File) => f.type === "application/pdf" || /\.pdf$/i.test(f.name);
const isImage = (f: File) => /^image\/(jpeg|png)$/.test(f.type) || /\.(jpe?g|png)$/i.test(f.name);

async function addFiles(files: FileList | File[]) {
  const list = Array.from(files);
  if (!list.length) return;
  busy = true;
  refreshButtons();
  let added = 0;
  let skipped = 0;
  const problems: string[] = [];
  try {
    for (const f of list) {
      if (isPdf(f)) {
        showMessage("info", `Reading ${f.name}…`);
        try {
          const rendered = await readPdf(f);
          for (const r of rendered) await addPage(r.dataUrl, null, r.source, { width: r.width, height: r.height });
          added += rendered.length;
        } catch (err) {
          problems.push(err instanceof PdfReadError ? err.message : `${f.name} couldn't be read.`);
        }
      } else if (isImage(f)) {
        await addPage(await readAsDataUrl(f), null);
        added++;
      } else {
        skipped++;
      }
    }
  } finally {
    busy = false;
    renderPages();
  }

  if (problems.length) showMessage("error", problems.join(" "));
  else if (added)
    showMessage("success", `Added ${added} page${added === 1 ? "" : "s"}.` + (skipped ? ` Skipped ${skipped} file${skipped === 1 ? "" : "s"} that weren't PDF, JPG or PNG.` : ""));
  else showMessage("error", "Only PDF, JPG and PNG files can be added.");
}

// Drag and drop files anywhere on the panel
function initDragAndDrop() {
  let depth = 0;
  const hasFiles = (e: DragEvent) => Array.from(e.dataTransfer?.types ?? []).includes("Files");
  document.addEventListener("dragenter", (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    depth++;
    el.dropHint.classList.replace("hidden", "grid");
  });
  document.addEventListener("dragover", (e) => {
    if (hasFiles(e)) e.preventDefault();
  });
  document.addEventListener("dragleave", () => {
    depth = Math.max(0, depth - 1);
    if (depth === 0) el.dropHint.classList.replace("grid", "hidden");
  });
  document.addEventListener("drop", (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    depth = 0;
    el.dropHint.classList.replace("grid", "hidden");
    if (e.dataTransfer?.files.length) void addFiles(e.dataTransfer.files);
  });
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
  el.year.textContent = String(new Date().getFullYear());
  for (const r of formatRadios()) {
    r.checked = r.value === settings.format;
    r.addEventListener("change", () => {
      saveSettings();
      refreshFormatUi();
    });
  }

  // Preview controls
  el.previewClose.addEventListener("click", closePreview);
  el.previewPrev.addEventListener("click", () => stepPreview(-1));
  el.previewNext.addEventListener("click", () => stepPreview(1));
  el.previewRotate.addEventListener("click", () => {
    const p = pages[previewIndex];
    if (!p) return;
    p.rotation = (p.rotation + 90) % 360;
    renderPages();
  });
  el.previewDelete.addEventListener("click", () => {
    if (previewIndex < 0) return;
    pages.splice(previewIndex, 1);
    renderPages();
  });
  el.preview.addEventListener("click", (e) => {
    if (e.target === el.preview || e.target === el.previewImg.parentElement) closePreview();
  });
  document.addEventListener("keydown", (e) => {
    if (previewIndex < 0) return;
    if (e.key === "Escape") closePreview();
    else if (e.key === "ArrowLeft") stepPreview(-1);
    else if (e.key === "ArrowRight") stepPreview(1);
    else if (e.key.toLowerCase() === "r") el.previewRotate.click();
    else return;
    e.preventDefault();
  });
  window.addEventListener("resize", renderPreview);

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

  platform = detectPlatform();
  initFeedback();
  initDragAndDrop();
  renderPages();
  void connect();
});
