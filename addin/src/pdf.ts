import { PDFDocument, degrees } from "pdf-lib";
import type { PdfPageSource } from "./pdfsource";

export interface PageImage {
  id: string;
  /** data: URL (image/jpeg or image/png) */
  dataUrl: string;
  /** Clockwise rotation in degrees: 0, 90, 180, 270 */
  rotation: number;
  /** Scan resolution, used to size the page at its real physical size. Null for uploaded images. */
  dpi: number | null;
  width: number;
  height: number;
  /** Set for pages that came from a PDF file: merged from the original, without re-rendering. */
  pdf?: PdfPageSource;
}

const LETTER = { w: 612, h: 792 }; // points

function dataUrlToBytes(dataUrl: string): { bytes: Uint8Array; mime: string } {
  const [header, b64] = dataUrl.split(",", 2);
  const mime = header.slice(5, header.indexOf(";"));
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return { bytes, mime };
}

/** Builds a single PDF (one image per page) and returns it as base64. */
export async function buildPdfBase64(pages: PageImage[], title: string): Promise<{ base64: string; size: number }> {
  const doc = await PDFDocument.create();
  doc.setTitle(title);
  doc.setCreator("Scan 2 PDF");
  doc.setProducer("Scan 2 PDF (pdf-lib)");

  const sources = new Map<Uint8Array, PDFDocument>();

  for (const p of pages) {
    if (p.pdf) {
      let src = sources.get(p.pdf.bytes);
      if (!src) {
        src = await PDFDocument.load(p.pdf.bytes, { ignoreEncryption: true });
        sources.set(p.pdf.bytes, src);
      }
      const [copied] = await doc.copyPages(src, [p.pdf.index]);
      const original = copied.getRotation().angle;
      doc.addPage(copied);
      if (p.rotation) copied.setRotation(degrees((original + p.rotation) % 360));
      continue;
    }

    const { bytes, mime } = dataUrlToBytes(p.dataUrl);
    const img = mime === "image/png" ? await doc.embedPng(bytes) : await doc.embedJpg(bytes);

    let w: number, h: number;
    if (p.dpi) {
      // True physical size: pixels / dpi * 72 points per inch
      w = (img.width / p.dpi) * 72;
      h = (img.height / p.dpi) * 72;
    } else {
      // Unknown dpi: fit inside Letter keeping aspect ratio
      const scale = Math.min(LETTER.w / img.width, LETTER.h / img.height);
      w = img.width * scale;
      h = img.height * scale;
    }

    const page = doc.addPage([w, h]);
    page.drawImage(img, { x: 0, y: 0, width: w, height: h });
    if (p.rotation) page.setRotation(degrees(p.rotation));
  }

  const base64 = await doc.saveAsBase64();
  return { base64, size: Math.floor((base64.length * 3) / 4) };
}

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error("Could not read image"));
    img.src = src;
  });
}

/**
 * Returns the page as a base64 JPEG with its rotation applied to the pixels
 * (so it shows the right way up in every viewer). Unrotated JPEG scans are passed through untouched.
 */
export async function pageToJpegBase64(p: PageImage, quality: number): Promise<string> {
  const [header, b64] = p.dataUrl.split(",", 2);
  if (p.rotation === 0 && header.includes("image/jpeg")) return b64;

  const img = await loadImage(p.dataUrl);
  const quarterTurn = p.rotation % 180 !== 0;
  const canvas = document.createElement("canvas");
  canvas.width = quarterTurn ? img.naturalHeight : img.naturalWidth;
  canvas.height = quarterTurn ? img.naturalWidth : img.naturalHeight;

  const ctx = canvas.getContext("2d")!;
  ctx.fillStyle = "#fff"; // PNG transparency → white
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.translate(canvas.width / 2, canvas.height / 2);
  ctx.rotate((p.rotation * Math.PI) / 180);
  ctx.drawImage(img, -img.naturalWidth / 2, -img.naturalHeight / 2);

  return canvas.toDataURL("image/jpeg", Math.min(0.95, Math.max(0.4, quality / 100))).split(",", 2)[1];
}
