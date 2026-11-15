import { PDFDocument, degrees } from "pdf-lib";

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

  for (const p of pages) {
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
