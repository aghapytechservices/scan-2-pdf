import "@fontsource/atkinson-hyperlegible/400.css";
import "@fontsource/atkinson-hyperlegible/700.css";
import "./phone.css";
import { applyLook, detectPage, fullQuad, loadPhoto, rotate90, straighten, toJpeg, type Look, type Point, type Quad } from "./docscan";
import { markDone, parseLink, ping, PhoneApiError, sendPage, type PhoneLink } from "./phonelink";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const ui = {
  conn: $("conn"),
  home: $("home"),
  camera: $<HTMLInputElement>("camera"),
  cameraLabel: $("cameraLabel"),
  library: $<HTMLInputElement>("library"),
  sentBox: $("sentBox"),
  sent: $<HTMLOListElement>("sent"),
  sentCount: $("sentCount"),
  doneBtn: $<HTMLButtonElement>("doneBtn"),
  editor: $("editor"),
  stage: $("stage"),
  view: $<HTMLCanvasElement>("view"),
  working: $("working"),
  resultTools: $("resultTools"),
  cropTools: $("cropTools"),
  edgesBtn: $<HTMLButtonElement>("edgesBtn"),
  rotateBtn: $<HTMLButtonElement>("rotateBtn"),
  retakeBtn: $<HTMLButtonElement>("retakeBtn"),
  sendBtn: $<HTMLButtonElement>("sendBtn"),
  wholeBtn: $<HTMLButtonElement>("wholeBtn"),
  applyBtn: $<HTMLButtonElement>("applyBtn"),
  finished: $("finished"),
  finishedText: $("finishedText"),
  moreBtn: $<HTMLButtonElement>("moreBtn"),
  problem: $("problem"),
  problemTitle: $("problemTitle"),
  problemText: $("problemText"),
  toast: $("toast"),
};

type Screen = "home" | "editor" | "finished" | "problem";
function show(screen: Screen) {
  ui.home.hidden = screen !== "home";
  ui.editor.hidden = screen !== "editor";
  ui.finished.hidden = screen !== "finished";
  ui.problem.hidden = screen !== "problem";
}

function setConn(state: "connecting" | "ok" | "bad", text: string) {
  ui.conn.dataset.state = state;
  ui.conn.textContent = text;
}

let toastTimer = 0;
function toast(text: string, kind: "info" | "error" = "info") {
  ui.toast.textContent = text;
  ui.toast.dataset.kind = kind;
  ui.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => (ui.toast.hidden = true), 4000);
}

function problem(title: string, html: string) {
  ui.problemTitle.textContent = title;
  ui.problemText.innerHTML = html;
  setConn("bad", "Not connected");
  show("problem");
}

const expired = () =>
  problem("This code has expired", "In Outlook, open Scan 2 PDF and choose <b>Scan with phone</b> to show a new QR code.");

// ---------- Connection ----------

const link: PhoneLink | null = parseLink(location.hash);
// The key lives only in the address bar's #fragment: keep it out of history and screenshots of the URL.
if (link) history.replaceState(null, "", location.pathname);

async function connect() {
  if (!link) {
    problem("Open this page from Outlook", "In Outlook, open Scan 2 PDF, choose <b>Scan with phone</b>, then scan the QR code with this phone's camera.");
    return;
  }
  try {
    await ping(link);
    setConn("ok", "Connected to Outlook");
    show("home");
  } catch (err) {
    if (err instanceof PhoneApiError && (err.status === 410 || err.status === 403)) expired();
    else problem("Can't connect", err instanceof Error ? escapeHtml(err.message) : "Please try again.");
  }
}

const escapeHtml = (s: string) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);

// ---------- Sending (one page at a time, in order) ----------

interface Sent {
  n: number;
  jpeg: Blob;
  thumb: string;
  state: "waiting" | "sending" | "sent" | "failed";
  li: HTMLLIElement;
}
const sent: Sent[] = [];
let sending = false;

function addToQueue(jpeg: Blob, thumb: string) {
  const li = document.createElement("li");
  const item: Sent = { n: sent.length + 1, jpeg, thumb, state: "waiting", li };
  li.innerHTML = `<img alt="Page ${item.n}" src="${thumb}"><b>${item.n}</b><span class="st"></span>`;
  li.addEventListener("click", () => {
    if (item.state === "failed") {
      item.state = "waiting";
      paint(item);
      void pump();
    }
  });
  sent.push(item);
  ui.sent.append(li);
  paint(item);
  void pump();
}

function paint(item: Sent) {
  item.li.dataset.state = item.state;
  item.li.querySelector(".st")!.textContent =
    { waiting: "Waiting…", sending: "Sending…", sent: "Sent", failed: "Failed · tap to retry" }[item.state];
  ui.sentBox.hidden = sent.length === 0;
  const done = sent.filter((s) => s.state === "sent").length;
  ui.sentCount.textContent = `(${done} of ${sent.length})`;
  ui.cameraLabel.textContent = sent.length ? "Take another photo" : "Take a photo";
}

async function pump() {
  if (sending || !link) return;
  sending = true;
  try {
    for (const item of sent) {
      if (item.state !== "waiting") continue;
      item.state = "sending";
      paint(item);
      try {
        await sendPage(link, item.jpeg);
        item.state = "sent";
      } catch (err) {
        item.state = "failed";
        if (err instanceof PhoneApiError && err.status === 410) {
          paint(item);
          expired();
          return;
        }
        toast(err instanceof Error ? err.message : "Couldn't send the page.", "error");
      }
      paint(item);
    }
  } finally {
    sending = false;
  }
  if (finishing) void finish();
}

let finishing = false;
async function finish() {
  finishing = true;
  if (sending || sent.some((s) => s.state === "waiting")) {
    ui.doneBtn.textContent = "Finishing…";
    return; // pump() calls back when the queue is empty
  }
  finishing = false;
  ui.doneBtn.textContent = "I'm done";
  const failed = sent.filter((s) => s.state === "failed").length;
  if (failed) {
    toast(`${failed} page${failed === 1 ? "" : "s"} didn't send. Tap ${failed === 1 ? "it" : "them"} to try again.`, "error");
    return;
  }
  try {
    await markDone(link!);
  } catch {
    /* Outlook still has every page; "done" only closes its QR panel */
  }
  const n = sent.length;
  ui.finishedText.textContent = `${n} page${n === 1 ? " was" : "s were"} sent to Outlook. You can close this page.`;
  show("finished");
}

ui.doneBtn.addEventListener("click", () => void finish());
ui.moreBtn.addEventListener("click", () => show("home"));

// ---------- Editor ----------

interface Edit {
  photo: HTMLCanvasElement;
  quad: Quad;
  look: Look;
  turns: number;
  straight: HTMLCanvasElement | null; // cached: photo cut out along quad
  result: HTMLCanvasElement | null;
}
let edit: Edit | null = null;
const queue: File[] = []; // more photos picked from the library, edited one after another

const nextFrame = () => new Promise<void>((r) => requestAnimationFrame(() => setTimeout(r, 0)));

async function openPhoto(file: File) {
  show("editor");
  setMode("result");
  ui.working.hidden = false;
  ui.sendBtn.disabled = true;
  await nextFrame();
  try {
    const photo = await loadPhoto(file);
    edit = { photo, quad: detectPage(photo), look: lastLook, turns: 0, straight: null, result: null };
    await render();
  } catch {
    ui.working.hidden = true;
    toast("That photo couldn't be opened. Try taking it again.", "error");
    closeEditor();
  }
}

let lastLook: Look = "document";

async function render() {
  if (!edit) return;
  ui.working.hidden = false;
  ui.sendBtn.disabled = true;
  await nextFrame();
  edit.straight ??= straighten(edit.photo, edit.quad);
  let out = applyLook(edit.straight, edit.look);
  for (let i = 0; i < edit.turns; i++) out = rotate90(out);
  edit.result = out;
  drawResult();
  ui.working.hidden = true;
  ui.sendBtn.disabled = false;
  for (const b of ui.resultTools.querySelectorAll<HTMLButtonElement>("[data-look]"))
    b.setAttribute("aria-pressed", String(b.dataset.look === edit.look));
}

function drawResult() {
  const r = edit!.result!;
  ui.view.className = "";
  ui.view.width = r.width;
  ui.view.height = r.height;
  ui.view.getContext("2d")!.drawImage(r, 0, 0);
}

function setMode(mode: "result" | "crop") {
  ui.resultTools.hidden = mode !== "result";
  ui.cropTools.hidden = mode !== "crop";
}

function closeEditor() {
  edit = null;
  const next = queue.shift();
  if (next) void openPhoto(next);
  else show("home");
}

for (const b of ui.resultTools.querySelectorAll<HTMLButtonElement>("[data-look]")) {
  b.addEventListener("click", () => {
    if (!edit || edit.look === b.dataset.look) return;
    edit.look = lastLook = b.dataset.look as Look;
    void render();
  });
}
ui.rotateBtn.addEventListener("click", () => {
  if (!edit) return;
  edit.turns = (edit.turns + 1) % 4;
  void render();
});
ui.retakeBtn.addEventListener("click", () => {
  edit = null;
  show("home");
  ui.camera.click();
});
ui.sendBtn.addEventListener("click", async () => {
  if (!edit?.result) return;
  ui.sendBtn.disabled = true;
  const page = edit.result;
  const jpeg = await toJpeg(page, edit.look === "document" ? 0.78 : 0.82);
  addToQueue(jpeg, thumbnail(page));
  closeEditor();
});

function thumbnail(c: HTMLCanvasElement): string {
  const s = 240 / Math.max(c.width, c.height);
  const t = document.createElement("canvas");
  t.width = Math.round(c.width * s);
  t.height = Math.round(c.height * s);
  t.getContext("2d")!.drawImage(c, 0, 0, t.width, t.height);
  return t.toDataURL("image/jpeg", 0.7);
}

// ---------- Adjust edges ----------

let cropQuad: Quad | null = null;
let cropScale = 1; // view pixels per photo pixel
let dragging = -1;

ui.edgesBtn.addEventListener("click", () => {
  if (!edit) return;
  cropQuad = edit.quad.map((p) => ({ ...p })) as Quad;
  setMode("crop");
  drawCrop();
});
ui.wholeBtn.addEventListener("click", () => {
  if (!edit) return;
  cropQuad = fullQuad(edit.photo);
  drawCrop();
});
ui.applyBtn.addEventListener("click", () => {
  if (!edit || !cropQuad) return;
  edit.quad = cropQuad;
  edit.straight = null;
  cropQuad = null;
  setMode("result");
  void render();
});

function drawCrop() {
  if (!edit || !cropQuad) return;
  const photo = edit.photo;
  const box = ui.stage.getBoundingClientRect();
  const dpr = Math.min(2, window.devicePixelRatio || 1);
  const fit = Math.min((box.width - 28) / photo.width, (box.height - 24) / photo.height);
  ui.view.className = "crop";
  ui.view.style.width = `${photo.width * fit}px`;
  ui.view.style.height = `${photo.height * fit}px`;
  ui.view.width = Math.round(photo.width * fit * dpr);
  ui.view.height = Math.round(photo.height * fit * dpr);
  cropScale = ui.view.width / photo.width;

  const g = ui.view.getContext("2d")!;
  const s = cropScale;
  g.drawImage(photo, 0, 0, ui.view.width, ui.view.height);
  // Dim everything outside the page
  g.save();
  g.fillStyle = "rgba(14, 27, 54, 0.55)";
  g.beginPath();
  g.rect(0, 0, ui.view.width, ui.view.height);
  g.moveTo(cropQuad[0].x * s, cropQuad[0].y * s);
  for (let i = 3; i >= 1; i--) g.lineTo(cropQuad[i].x * s, cropQuad[i].y * s);
  g.closePath();
  g.fill("evenodd");
  g.restore();
  // Outline and corner handles
  g.lineWidth = 2.5 * dpr;
  g.strokeStyle = "#10b981";
  g.beginPath();
  cropQuad.forEach((p, i) => (i ? g.lineTo(p.x * s, p.y * s) : g.moveTo(p.x * s, p.y * s)));
  g.closePath();
  g.stroke();
  for (const [i, p] of cropQuad.entries()) {
    g.beginPath();
    g.arc(p.x * s, p.y * s, (dragging === i ? 16 : 12) * dpr, 0, Math.PI * 2);
    g.fillStyle = dragging === i ? "rgba(16, 185, 129, 0.45)" : "rgba(255, 255, 255, 0.3)";
    g.fill();
    g.lineWidth = 3 * dpr;
    g.strokeStyle = "#fff";
    g.stroke();
  }
}

function photoPoint(e: PointerEvent): Point {
  const r = ui.view.getBoundingClientRect();
  const k = ui.view.width / r.width / cropScale;
  const x = (e.clientX - r.left) * k, y = (e.clientY - r.top) * k;
  return {
    x: Math.max(0, Math.min(edit!.photo.width, x)),
    y: Math.max(0, Math.min(edit!.photo.height, y)),
  };
}

ui.view.addEventListener("pointerdown", (e) => {
  if (!cropQuad || !edit) return;
  const p = photoPoint(e);
  const r = ui.view.getBoundingClientRect();
  const reach = (44 * edit.photo.width) / r.width; // 44 css px, in photo pixels
  let best = -1, bestD = reach;
  cropQuad.forEach((c, i) => {
    const d = Math.hypot(c.x - p.x, c.y - p.y);
    if (d < bestD) { bestD = d; best = i; }
  });
  if (best < 0) return;
  dragging = best;
  ui.view.setPointerCapture(e.pointerId);
  drawCrop();
});
ui.view.addEventListener("pointermove", (e) => {
  if (dragging < 0 || !cropQuad) return;
  cropQuad[dragging] = photoPoint(e);
  drawCrop();
});
const endDrag = () => {
  if (dragging < 0) return;
  dragging = -1;
  drawCrop();
};
ui.view.addEventListener("pointerup", endDrag);
ui.view.addEventListener("pointercancel", endDrag);
window.addEventListener("resize", () => {
  if (cropQuad) drawCrop();
});

// ---------- Picking photos ----------

ui.camera.addEventListener("change", () => {
  const f = ui.camera.files?.[0];
  ui.camera.value = "";
  if (f) void openPhoto(f);
});
ui.library.addEventListener("change", () => {
  const files = Array.from(ui.library.files ?? []).filter((f) => f.type.startsWith("image/") || /\.(jpe?g|png|heic|webp)$/i.test(f.name));
  ui.library.value = "";
  if (!files.length) return;
  queue.push(...files.slice(1));
  void openPhoto(files[0]);
});

// Warn before leaving with pages still on their way.
window.addEventListener("beforeunload", (e) => {
  if (sent.some((s) => s.state === "waiting" || s.state === "sending")) e.preventDefault();
});

void connect();
