/**
 * Turns a phone photo of a page into something that looks scanned:
 * find the page, straighten it (perspective correction), and even out lighting and shadows.
 * Plain canvas code, no libraries, so the phone page stays light.
 */

export type Point = { x: number; y: number };
/** Corners in order: top-left, top-right, bottom-right, bottom-left. */
export type Quad = [Point, Point, Point, Point];
export type Look = "document" | "color" | "original";

const MAX_PHOTO_EDGE = 2400;
const MAX_PAGE_EDGE = 2200;

function canvas(w: number, h: number): HTMLCanvasElement {
  const c = document.createElement("canvas");
  c.width = Math.max(1, Math.round(w));
  c.height = Math.max(1, Math.round(h));
  return c;
}

const ctx2d = (c: HTMLCanvasElement) => c.getContext("2d", { willReadFrequently: true })!;

/** Loads a photo (respecting its EXIF rotation) into a canvas no larger than MAX_PHOTO_EDGE. */
export async function loadPhoto(file: Blob): Promise<HTMLCanvasElement> {
  const url = URL.createObjectURL(file);
  try {
    const img = new Image();
    img.src = url;
    await img.decode();
    const scale = Math.min(1, MAX_PHOTO_EDGE / Math.max(img.naturalWidth, img.naturalHeight));
    const c = canvas(img.naturalWidth * scale, img.naturalHeight * scale);
    const g = ctx2d(c);
    g.imageSmoothingQuality = "high";
    g.drawImage(img, 0, 0, c.width, c.height);
    return c;
  } finally {
    URL.revokeObjectURL(url);
  }
}

export function fullQuad(c: HTMLCanvasElement): Quad {
  const w = c.width, h = c.height;
  return [{ x: 0, y: 0 }, { x: w, y: 0 }, { x: w, y: h }, { x: 0, y: h }];
}

/**
 * Finds the page: a page is usually the largest bright, roughly uniform area in the photo.
 * Works on a small copy: Otsu threshold, largest bright region, then its four extreme corners.
 * Falls back to the whole photo when nothing page-like stands out.
 */
export function detectPage(photo: HTMLCanvasElement): Quad {
  const W = 320;
  const scale = W / Math.max(photo.width, photo.height);
  const w = Math.max(8, Math.round(photo.width * scale)), h = Math.max(8, Math.round(photo.height * scale));
  const small = canvas(w, h);
  ctx2d(small).drawImage(photo, 0, 0, w, h);
  const px = ctx2d(small).getImageData(0, 0, w, h).data;

  const gray = new Uint8Array(w * h);
  const hist = new Uint32Array(256);
  for (let i = 0; i < w * h; i++) {
    const r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
    // Paper is bright and not very colorful: penalize saturation so a bright wooden desk doesn't win.
    const max = Math.max(r, g, b), min = Math.min(r, g, b);
    const v = Math.max(0, Math.round(0.299 * r + 0.587 * g + 0.114 * b - (max - min) * 0.6));
    gray[i] = v;
    hist[v]++;
  }

  // Otsu's threshold
  let sum = 0;
  for (let i = 0; i < 256; i++) sum += i * hist[i];
  let sumB = 0, wB = 0, best = 0, threshold = 128;
  for (let t = 0; t < 256; t++) {
    wB += hist[t];
    if (!wB) continue;
    const wF = w * h - wB;
    if (!wF) break;
    sumB += t * hist[t];
    const mB = sumB / wB, mF = (sum - sumB) / wF;
    const between = wB * wF * (mB - mF) ** 2;
    if (between > best) { best = between; threshold = t; }
  }

  // Bright mask, closed a little so text on the page doesn't cut it into pieces.
  let mask: Uint8Array<ArrayBufferLike> = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) mask[i] = gray[i] > threshold ? 1 : 0;
  mask = morph(morph(mask, w, h, true), w, h, false); // dilate then erode

  // Largest connected bright region
  const label = new Int32Array(w * h).fill(-1);
  const stack = new Int32Array(w * h);
  let bestSize = 0, bestLabel = -1;
  for (let start = 0, id = 0; start < w * h; start++) {
    if (!mask[start] || label[start] >= 0) continue;
    let top = 0, size = 0;
    stack[top++] = start;
    label[start] = id;
    while (top) {
      const p = stack[--top];
      size++;
      const x = p % w, y = (p / w) | 0;
      const near = [x > 0 ? p - 1 : -1, x < w - 1 ? p + 1 : -1, y > 0 ? p - w : -1, y < h - 1 ? p + w : -1];
      for (const q of near) {
        if (q >= 0 && mask[q] && label[q] < 0) { label[q] = id; stack[top++] = q; }
      }
    }
    if (size > bestSize) { bestSize = size; bestLabel = id; }
    id++;
  }

  const area = bestSize / (w * h);
  if (bestLabel < 0 || area < 0.15 || area > 0.97) return fullQuad(photo);

  // Extreme points of the region: smallest/largest x+y and x−y give the four corners.
  let tl = { x: 0, y: 0, v: Infinity }, br = { x: 0, y: 0, v: -Infinity };
  let tr = { x: 0, y: 0, v: -Infinity }, bl = { x: 0, y: 0, v: Infinity };
  for (let p = 0; p < w * h; p++) {
    if (label[p] !== bestLabel) continue;
    const x = p % w, y = (p / w) | 0;
    if (x + y < tl.v) tl = { x, y, v: x + y };
    if (x + y > br.v) br = { x, y, v: x + y };
    if (x - y > tr.v) tr = { x, y, v: x - y };
    if (x - y < bl.v) bl = { x, y, v: x - y };
  }
  const up = (p: { x: number; y: number }) => ({ x: (p.x + 0.5) / scale, y: (p.y + 0.5) / scale });
  const quad: Quad = [up(tl), up(tr), up(br), up(bl)];
  return quadArea(quad) / (photo.width * photo.height) < 0.12 ? fullQuad(photo) : quad;
}

function morph(src: Uint8Array<ArrayBufferLike>, w: number, h: number, dilate: boolean): Uint8Array<ArrayBufferLike> {
  const out = new Uint8Array(w * h);
  const r = 2;
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      let v = dilate ? 0 : 1;
      for (let dy = -r; dy <= r && v === (dilate ? 0 : 1); dy++)
        for (let dx = -r; dx <= r; dx++) {
          const xx = x + dx, yy = y + dy;
          const s = xx < 0 || yy < 0 || xx >= w || yy >= h ? 0 : src[yy * w + xx];
          if (dilate && s) { v = 1; break; }
          if (!dilate && !s) { v = 0; break; }
        }
      out[y * w + x] = v;
    }
  return out;
}

export function quadArea(q: Quad): number {
  let a = 0;
  for (let i = 0; i < 4; i++) {
    const p = q[i], n = q[(i + 1) % 4];
    a += p.x * n.y - n.x * p.y;
  }
  return Math.abs(a) / 2;
}

const dist = (a: Point, b: Point) => Math.hypot(a.x - b.x, a.y - b.y);

/** Solves the 3×3 perspective transform that maps the 4 `from` points onto the 4 `to` points. */
function homography(from: Quad, to: Quad): number[] {
  const A: number[][] = [];
  for (let i = 0; i < 4; i++) {
    const { x, y } = from[i], { x: u, y: v } = to[i];
    A.push([x, y, 1, 0, 0, 0, -u * x, -u * y, u]);
    A.push([0, 0, 0, x, y, 1, -v * x, -v * y, v]);
  }
  // Gaussian elimination with partial pivoting on the 8×9 augmented matrix
  for (let c = 0; c < 8; c++) {
    let piv = c;
    for (let r = c + 1; r < 8; r++) if (Math.abs(A[r][c]) > Math.abs(A[piv][c])) piv = r;
    [A[c], A[piv]] = [A[piv], A[c]];
    for (let r = 0; r < 8; r++) {
      if (r === c) continue;
      const f = A[r][c] / A[c][c];
      for (let k = c; k < 9; k++) A[r][k] -= f * A[c][k];
    }
  }
  const h = A.map((row, i) => row[8] / row[i]);
  return [...h, 1];
}

/** Cuts the page out along `quad` and straightens it into a rectangle. */
export function straighten(photo: HTMLCanvasElement, quad: Quad): HTMLCanvasElement {
  const [tl, tr, br, bl] = quad;
  let outW = Math.max(dist(tl, tr), dist(bl, br));
  let outH = Math.max(dist(tl, bl), dist(tr, br));
  const s = Math.min(1, MAX_PAGE_EDGE / Math.max(outW, outH));
  outW = Math.round(outW * s);
  outH = Math.round(outH * s);

  const out = canvas(outW, outH);
  const rect: Quad = [{ x: 0, y: 0 }, { x: outW, y: 0 }, { x: outW, y: outH }, { x: 0, y: outH }];
  const m = homography(rect, quad); // output pixel → photo pixel

  const sw = photo.width, sh = photo.height;
  const src = ctx2d(photo).getImageData(0, 0, sw, sh).data;
  const img = ctx2d(out).createImageData(outW, outH);
  const dst = img.data;

  for (let y = 0; y < outH; y++) {
    for (let x = 0; x < outW; x++) {
      const cx = x + 0.5, cy = y + 0.5;
      const d = m[6] * cx + m[7] * cy + m[8];
      let sx = (m[0] * cx + m[1] * cy + m[2]) / d - 0.5;
      let sy = (m[3] * cx + m[4] * cy + m[5]) / d - 0.5;
      sx = Math.min(sw - 1.001, Math.max(0, sx));
      sy = Math.min(sh - 1.001, Math.max(0, sy));
      const x0 = sx | 0, y0 = sy | 0, fx = sx - x0, fy = sy - y0;
      const i00 = (y0 * sw + x0) * 4, i10 = i00 + 4, i01 = i00 + sw * 4, i11 = i01 + 4;
      const o = (y * outW + x) * 4;
      for (let ch = 0; ch < 3; ch++) {
        const top = src[i00 + ch] + (src[i10 + ch] - src[i00 + ch]) * fx;
        const bot = src[i01 + ch] + (src[i11 + ch] - src[i01 + ch]) * fx;
        dst[o + ch] = top + (bot - top) * fy;
      }
      dst[o + 3] = 255;
    }
  }
  ctx2d(out).putImageData(img, 0, 0);
  return out;
}

/**
 * Evens out lighting: estimates the paper's brightness everywhere (text removed by taking local
 * maxima, then blurred) and divides by it, so shadows and uneven light turn white.
 * "document" also turns the page into crisp grayscale; "color" keeps the colors.
 */
export function applyLook(page: HTMLCanvasElement, look: Look): HTMLCanvasElement {
  const w = page.width, h = page.height;
  const out = canvas(w, h);
  ctx2d(out).drawImage(page, 0, 0);
  if (look === "original") return out;

  // Paper brightness map: shrink, take local max (removes ink), blur, grow back.
  const sw = Math.max(4, Math.round(w / 24)), sh = Math.max(4, Math.round(h / 24));
  const small = canvas(sw, sh);
  ctx2d(small).drawImage(page, 0, 0, sw, sh);
  const sd = ctx2d(small).getImageData(0, 0, sw, sh);
  const lum = new Float32Array(sw * sh);
  for (let i = 0; i < sw * sh; i++) lum[i] = Math.max(sd.data[i * 4], sd.data[i * 4 + 1], sd.data[i * 4 + 2]);
  let bg: Float32Array<ArrayBufferLike> = lum;
  for (let pass = 0; pass < 2; pass++) bg = maxFilter(bg, sw, sh);
  for (let pass = 0; pass < 2; pass++) bg = boxBlur(bg, sw, sh);
  for (let i = 0; i < sw * sh; i++) {
    const v = Math.max(40, Math.min(255, bg[i]));
    sd.data[i * 4] = sd.data[i * 4 + 1] = sd.data[i * 4 + 2] = v;
    sd.data[i * 4 + 3] = 255;
  }
  ctx2d(small).putImageData(sd, 0, 0);
  const light = canvas(w, h);
  const lg = ctx2d(light);
  lg.imageSmoothingQuality = "high";
  lg.drawImage(small, 0, 0, w, h);
  const L = lg.getImageData(0, 0, w, h).data;

  const img = ctx2d(out).getImageData(0, 0, w, h);
  const d = img.data;
  for (let i = 0; i < d.length; i += 4) {
    const k = 255 / L[i];
    if (look === "document") {
      const g = (0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2]) * k;
      // Contrast curve: paper to pure white, ink darker, light marks kept readable.
      const v = Math.max(0, Math.min(255, ((g - 70) / (225 - 70)) * 255));
      const c = 255 * Math.pow(v / 255, 1.6);
      d[i] = d[i + 1] = d[i + 2] = c;
    } else {
      for (let ch = 0; ch < 3; ch++) {
        const v = Math.min(255, d[i + ch] * k * 1.04);
        d[i + ch] = 255 * Math.pow(v / 255, 1.25);
      }
    }
  }
  ctx2d(out).putImageData(img, 0, 0);
  return out;
}

function maxFilter(src: Float32Array<ArrayBufferLike>, w: number, h: number): Float32Array {
  const out = new Float32Array(w * h);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      let m = 0;
      for (let dy = -1; dy <= 1; dy++)
        for (let dx = -1; dx <= 1; dx++) {
          const xx = Math.min(w - 1, Math.max(0, x + dx)), yy = Math.min(h - 1, Math.max(0, y + dy));
          m = Math.max(m, src[yy * w + xx]);
        }
      out[y * w + x] = m;
    }
  return out;
}

function boxBlur(src: Float32Array<ArrayBufferLike>, w: number, h: number): Float32Array {
  const out = new Float32Array(w * h);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      let s = 0, n = 0;
      for (let dy = -2; dy <= 2; dy++)
        for (let dx = -2; dx <= 2; dx++) {
          const xx = x + dx, yy = y + dy;
          if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
          s += src[yy * w + xx];
          n++;
        }
      out[y * w + x] = s / n;
    }
  return out;
}

export function rotate90(src: HTMLCanvasElement): HTMLCanvasElement {
  const out = canvas(src.height, src.width);
  const g = ctx2d(out);
  g.translate(out.width, 0);
  g.rotate(Math.PI / 2);
  g.drawImage(src, 0, 0);
  return out;
}

export function toJpeg(c: HTMLCanvasElement, quality = 0.82): Promise<Blob> {
  return new Promise((resolve, reject) =>
    c.toBlob((b) => (b ? resolve(b) : reject(new Error("Couldn't encode the page."))), "image/jpeg", quality),
  );
}
