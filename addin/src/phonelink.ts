/**
 * Phone scanning link: the encrypted channel between the phone page and the Outlook panel.
 *
 * The QR code holds https://<site>/phone#s=<session>&u=<upload token>&k=<AES key>. The part after
 * "#" never leaves the phone's browser, so the server sees only the session id and token on its own
 * requests, never the key. Pages are AES-GCM encrypted on the phone and decrypted in Outlook.
 */

export interface PhoneLink {
  sid: string;
  upload: string;
  key: string; // base64url, 32 bytes
}

const API = "/api/phone";

export function b64url(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function fromB64url(text: string): Uint8Array<ArrayBuffer> {
  const b64 = text.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((text.length + 3) % 4);
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export const randomToken = (bytes = 16) => b64url(crypto.getRandomValues(new Uint8Array(bytes)));

export async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0")).join("");
}

const importKey = (key: string, use: KeyUsage) =>
  crypto.subtle.importKey("raw", fromB64url(key), { name: "AES-GCM" }, false, [use]);

/** iv (12 bytes) followed by the AES-GCM ciphertext. */
export async function encrypt(key: string, data: ArrayBuffer): Promise<Uint8Array<ArrayBuffer>> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv }, await importKey(key, "encrypt"), data));
  const out = new Uint8Array(iv.length + ct.length);
  out.set(iv);
  out.set(ct, iv.length);
  return out;
}

export async function decrypt(key: string, data: ArrayBuffer): Promise<ArrayBuffer> {
  const bytes = new Uint8Array(data);
  return crypto.subtle.decrypt({ name: "AES-GCM", iv: bytes.slice(0, 12) }, await importKey(key, "decrypt"), bytes.slice(12));
}

export function linkUrl(origin: string, link: PhoneLink): string {
  return `${origin}/phone#s=${link.sid}&u=${link.upload}&k=${link.key}`;
}

export function parseLink(hash: string): PhoneLink | null {
  const p = new URLSearchParams(hash.replace(/^#/, ""));
  const sid = p.get("s"), upload = p.get("u"), key = p.get("k");
  if (!sid || !upload || !key || fromB64url(key).length !== 32) return null;
  return { sid, upload, key };
}

export class PhoneApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function call(query: string, init: RequestInit = {}): Promise<Response> {
  let res: Response;
  try {
    res = await fetch(`${API}?${query}`, { cache: "no-store", ...init });
  } catch {
    throw new PhoneApiError("No connection. Check your internet and try again.", 0);
  }
  if (!res.ok) {
    const body = (await res.json().catch(() => null)) as { error?: string } | null;
    throw new PhoneApiError(body?.error ?? `Request failed (${res.status}).`, res.status);
  }
  return res;
}

// ---------- Outlook panel side ----------

export interface PanelSession extends PhoneLink {
  pickup: string;
}

export async function createSession(): Promise<PanelSession> {
  const s: PanelSession = { sid: randomToken(16), upload: randomToken(16), pickup: randomToken(16), key: randomToken(32) };
  await call("op=create", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ sid: s.sid, up: await sha256Hex(s.upload), pk: await sha256Hex(s.pickup) }),
  });
  return s;
}

export interface SessionState {
  pages: number[];
  sent: number;
  done: boolean;
  phoneSeen: boolean;
  expiresIn: number;
}

const pickup = (s: PanelSession) => ({ "x-pickup-token": s.pickup });

export async function listPages(s: PanelSession): Promise<SessionState> {
  return (await call(`op=list&sid=${s.sid}`, { headers: pickup(s) })).json();
}

/** Downloads, decrypts and then deletes one page. Returns the JPEG bytes. */
export async function takePage(s: PanelSession, index: number): Promise<ArrayBuffer> {
  const res = await call(`op=page&sid=${s.sid}&i=${index}`, { headers: pickup(s) });
  const plain = await decrypt(s.key, await res.arrayBuffer());
  await call(`op=page&sid=${s.sid}&i=${index}`, { method: "DELETE", headers: pickup(s) }).catch(() => undefined);
  return plain;
}

export async function discardPage(s: PanelSession, index: number): Promise<void> {
  await call(`op=page&sid=${s.sid}&i=${index}`, { method: "DELETE", headers: pickup(s) }).catch(() => undefined);
}

export async function closeSession(s: PanelSession): Promise<void> {
  await call(`op=session&sid=${s.sid}`, { method: "DELETE", headers: pickup(s), keepalive: true }).catch(() => undefined);
}

// ---------- Phone side ----------

const upload = (l: PhoneLink) => ({ "x-upload-token": l.upload });

export async function ping(l: PhoneLink): Promise<{ expiresIn: number; pages: number }> {
  return (await call(`op=ping&sid=${l.sid}`, { headers: upload(l) })).json();
}

export async function sendPage(l: PhoneLink, jpeg: Blob): Promise<number> {
  const body = await encrypt(l.key, await jpeg.arrayBuffer());
  const res = await call(`op=upload&sid=${l.sid}`, {
    method: "POST",
    headers: { ...upload(l), "content-type": "application/octet-stream" },
    body,
  });
  return ((await res.json()) as { index: number }).index;
}

export async function markDone(l: PhoneLink): Promise<void> {
  await call(`op=done&sid=${l.sid}`, { method: "POST", headers: upload(l) });
}
