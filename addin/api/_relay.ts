/**
 * Phone-scan relay: carries encrypted page images from a phone to the Outlook panel.
 *
 * The panel creates a session and shows a QR code holding the session id, an upload token and the
 * AES key, all in the URL fragment, which browsers never send to the server. The phone encrypts each
 * page and uploads it; the panel downloads, decrypts and deletes it. This server only ever holds
 * ciphertext, keyed by random ids, and everything expires on its own after SESSION_TTL seconds.
 *
 * Tokens are stored as SHA-256 hashes: the upload token lets the phone add pages, the pickup token
 * (known only to the panel) lets it read and delete them.
 */

export const SESSION_TTL = 15 * 60; // seconds
export const MAX_PAGES = 60;
export const MAX_PAGE_BYTES = 4 * 1024 * 1024; // under Vercel's 4.5 MB request limit
const CREATES_PER_HOUR_PER_IP = 40;

/** The small part of Redis the relay uses (Upstash in production, a Map in tests). */
export interface Store {
  hset(key: string, fields: Record<string, string | number>): Promise<unknown>;
  hgetall(key: string): Promise<Record<string, unknown> | null>;
  hincrby(key: string, field: string, by: number): Promise<number>;
  set(key: string, value: string, ttlSeconds: number): Promise<unknown>;
  get(key: string): Promise<string | null>;
  del(...keys: string[]): Promise<unknown>;
  sadd(key: string, member: string): Promise<unknown>;
  srem(key: string, member: string): Promise<unknown>;
  smembers(key: string): Promise<string[]>;
  expire(key: string, ttlSeconds: number): Promise<unknown>;
  ttl(key: string): Promise<number>;
  incr(key: string): Promise<number>;
}

const SID = /^[A-Za-z0-9_-]{22,64}$/;
const HASH = /^[0-9a-f]{64}$/;

const k = {
  session: (sid: string) => `ph:${sid}`,
  index: (sid: string) => `ph:${sid}:pages`,
  page: (sid: string, i: string | number) => `ph:${sid}:p:${i}`,
  rate: (ip: string) => `ph:rate:${ip}:${Math.floor(Date.now() / 3_600_000)}`,
};

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", "cache-control": "no-store" },
  });
const fail = (status: number, error: string) => json({ error }, status);

async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0")).join("");
}

function sameHex(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function toBase64(bytes: Uint8Array): string {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

function fromBase64(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

type Role = "upload" | "pickup";

/** Loads the session and checks the caller's token for the given role. */
async function authorize(store: Store, sid: string, role: Role, req: Request) {
  if (!SID.test(sid)) return { error: fail(400, "Bad session id.") };
  const token = req.headers.get(role === "upload" ? "x-upload-token" : "x-pickup-token") ?? "";
  const session = await store.hgetall(k.session(sid));
  if (!session || !session.up) return { error: fail(410, "This code has expired. Show a new QR code in Outlook.") };
  const expected = String(role === "upload" ? session.up : session.pk);
  if (!token || !sameHex(await sha256Hex(token), expected)) return { error: fail(403, "Not allowed.") };
  return { session };
}

export async function handle(req: Request, store: Store): Promise<Response> {
  const url = new URL(req.url);
  const op = url.searchParams.get("op") ?? "";
  const sid = url.searchParams.get("sid") ?? "";

  try {
    // Panel: start a session.
    if (op === "create" && req.method === "POST") {
      const ip = (req.headers.get("x-forwarded-for") ?? "unknown").split(",")[0].trim();
      const rateKey = k.rate(ip);
      const count = await store.incr(rateKey);
      if (count === 1) await store.expire(rateKey, 3600);
      if (count > CREATES_PER_HOUR_PER_IP) return fail(429, "Too many phone sessions. Try again later.");

      const body = (await req.json().catch(() => null)) as { sid?: string; up?: string; pk?: string } | null;
      if (!body || !SID.test(body.sid ?? "") || !HASH.test(body.up ?? "") || !HASH.test(body.pk ?? ""))
        return fail(400, "Bad request.");
      const existing = await store.hgetall(k.session(body.sid!));
      if (existing && existing.up) return fail(409, "Session exists.");
      await store.hset(k.session(body.sid!), { up: body.up!, pk: body.pk!, n: 0, done: 0, at: Date.now() });
      await store.expire(k.session(body.sid!), SESSION_TTL);
      return json({ ok: true, expiresIn: SESSION_TTL });
    }

    // Phone: check the code is still valid when the page opens.
    if (op === "ping" && req.method === "GET") {
      const a = await authorize(store, sid, "upload", req);
      if (a.error) return a.error;
      await store.hset(k.session(sid), { seen: 1 }); // shown in Outlook as "Phone connected"
      return json({ ok: true, expiresIn: await store.ttl(k.session(sid)), pages: Number(a.session.n ?? 0) });
    }

    // Phone: add one encrypted page.
    if (op === "upload" && req.method === "POST") {
      const a = await authorize(store, sid, "upload", req);
      if (a.error) return a.error;
      if (Number(a.session.n ?? 0) >= MAX_PAGES) return fail(413, `Up to ${MAX_PAGES} pages can be sent at once.`);
      const bytes = new Uint8Array(await req.arrayBuffer());
      if (bytes.length < 29) return fail(400, "Empty page.");
      if (bytes.length > MAX_PAGE_BYTES) return fail(413, "This page is too large.");
      const ttl = await store.ttl(k.session(sid));
      if (ttl <= 0) return fail(410, "This code has expired. Show a new QR code in Outlook.");
      const index = await store.hincrby(k.session(sid), "n", 1);
      await store.set(k.page(sid, index), toBase64(bytes), ttl);
      await store.sadd(k.index(sid), String(index));
      await store.expire(k.index(sid), ttl);
      return json({ ok: true, index });
    }

    // Phone: the person tapped Done.
    if (op === "done" && req.method === "POST") {
      const a = await authorize(store, sid, "upload", req);
      if (a.error) return a.error;
      await store.hset(k.session(sid), { done: 1 });
      return json({ ok: true });
    }

    // Panel: which pages are waiting, and is the phone done?
    if (op === "list" && req.method === "GET") {
      const a = await authorize(store, sid, "pickup", req);
      if (a.error) return a.error;
      const pages = (await store.smembers(k.index(sid))).map(Number).filter((n) => n > 0).sort((x, y) => x - y);
      return json({
        pages,
        sent: Number(a.session.n ?? 0),
        done: String(a.session.done) === "1",
        phoneSeen: String(a.session.seen ?? "0") === "1",
        expiresIn: await store.ttl(k.session(sid)),
      });
    }

    // Panel: fetch one page (ciphertext).
    if (op === "page" && req.method === "GET") {
      const a = await authorize(store, sid, "pickup", req);
      if (a.error) return a.error;
      const i = url.searchParams.get("i") ?? "";
      if (!/^\d{1,3}$/.test(i)) return fail(400, "Bad page.");
      const data = await store.get(k.page(sid, i));
      if (!data) return fail(404, "Page not found.");
      return new Response(fromBase64(data).buffer as ArrayBuffer, {
        headers: { "content-type": "application/octet-stream", "cache-control": "no-store" },
      });
    }

    // Panel: delete a page it has received.
    if (op === "page" && req.method === "DELETE") {
      const a = await authorize(store, sid, "pickup", req);
      if (a.error) return a.error;
      const i = url.searchParams.get("i") ?? "";
      if (!/^\d{1,3}$/.test(i)) return fail(400, "Bad page.");
      await store.del(k.page(sid, i));
      await store.srem(k.index(sid), i);
      return json({ ok: true });
    }

    // Panel: close the session and delete everything left.
    if (op === "session" && req.method === "DELETE") {
      const a = await authorize(store, sid, "pickup", req);
      if (a.error) return a.error;
      const left = await store.smembers(k.index(sid));
      await store.del(k.session(sid), k.index(sid), ...left.map((i) => k.page(sid, i)));
      return json({ ok: true });
    }

    return fail(404, "Unknown request.");
  } catch (err) {
    console.error("phone relay error", op, err);
    return fail(500, "The phone service had a problem. Try again.");
  }
}
