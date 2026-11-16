// Turns a PDF file into page entries for the task pane.
// Each page keeps a reference to the original PDF so it can be merged without quality loss,
// plus a rendered image used for the thumbnail, the preview and JPEG export.
// pdf.js is loaded only when a PDF is actually added (it's large).

export interface PdfPageSource {
  bytes: Uint8Array; // the original file, shared by all its pages
  index: number; // 0-based page index in that file
}

export interface RenderedPdfPage {
  dataUrl: string;
  width: number;
  height: number;
  source: PdfPageSource;
}

const RENDER_DPI = 150; // good for previews and JPEG export; PDF output reuses the original pages
const MAX_PAGES = 200;

export class PdfReadError extends Error {}

export async function readPdf(file: File): Promise<RenderedPdfPage[]> {
  const [pdfjs, worker] = await Promise.all([
    import("pdfjs-dist/legacy/build/pdf.mjs"),
    import("pdfjs-dist/legacy/build/pdf.worker.min.mjs?url"),
  ]);
  pdfjs.GlobalWorkerOptions.workerSrc = worker.default;

  const bytes = new Uint8Array(await file.arrayBuffer());
  let doc;
  try {
    // pdf.js takes ownership of the buffer it's given, so pass a copy and keep the original for merging.
    doc = await pdfjs.getDocument({ data: bytes.slice(), isEvalSupported: false }).promise;
  } catch (err) {
    const name = (err as { name?: string })?.name;
    throw new PdfReadError(
      name === "PasswordException"
        ? `${file.name} is password-protected. Remove the password and try again.`
        : `${file.name} couldn't be read as a PDF.`,
    );
  }

  if (doc.numPages > MAX_PAGES) {
    throw new PdfReadError(`${file.name} has ${doc.numPages} pages. The limit is ${MAX_PAGES}.`);
  }

  const pages: RenderedPdfPage[] = [];
  for (let i = 1; i <= doc.numPages; i++) {
    const page = await doc.getPage(i);
    const viewport = page.getViewport({ scale: RENDER_DPI / 72 });
    const canvas = document.createElement("canvas");
    canvas.width = Math.ceil(viewport.width);
    canvas.height = Math.ceil(viewport.height);
    const ctx = canvas.getContext("2d")!;
    ctx.fillStyle = "#fff";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    await page.render({ canvasContext: ctx, viewport }).promise;
    pages.push({
      dataUrl: canvas.toDataURL("image/jpeg", 0.85),
      width: canvas.width,
      height: canvas.height,
      source: { bytes, index: i - 1 },
    });
    page.cleanup();
  }
  await doc.destroy();
  return pages;
}
