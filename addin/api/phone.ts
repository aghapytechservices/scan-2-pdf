import { Redis } from "@upstash/redis";
import { handle, type Store } from "./_relay.js";

// Upstash Redis, connected to the project from the Vercel dashboard (Storage).
// The integration sets KV_REST_API_* (older) or UPSTASH_REDIS_REST_* (newer) variables.
let redis: Redis | null = null;
function store(): Store {
  if (!redis) {
    const url = process.env.KV_REST_API_URL ?? process.env.UPSTASH_REDIS_REST_URL;
    const token = process.env.KV_REST_API_TOKEN ?? process.env.UPSTASH_REDIS_REST_TOKEN;
    if (!url || !token) throw new Error("Phone scanning storage isn't connected (no Redis variables).");
    redis = new Redis({ url, token, automaticDeserialization: false });
  }
  const r = redis;
  return {
    hset: (key, fields) => r.hset(key, fields),
    // With automaticDeserialization off, HGETALL comes back raw: [field, value, field, value, …].
    hgetall: async (key) => {
      const raw = (await r.hgetall(key)) as unknown;
      if (!raw) return null;
      if (Array.isArray(raw)) {
        if (raw.length === 0) return null;
        const obj: Record<string, unknown> = {};
        for (let i = 0; i + 1 < raw.length; i += 2) obj[String(raw[i])] = raw[i + 1];
        return obj;
      }
      return Object.keys(raw as object).length ? (raw as Record<string, unknown>) : null;
    },
    hincrby: (key, field, by) => r.hincrby(key, field, by),
    set: (key, value, ttl) => r.set(key, value, { ex: ttl }),
    get: (key) => r.get<string>(key),
    del: (...keys) => r.del(...keys),
    sadd: (key, member) => r.sadd(key, member),
    srem: (key, member) => r.srem(key, member),
    smembers: (key) => r.smembers(key),
    expire: (key, ttl) => r.expire(key, ttl),
    ttl: (key) => r.ttl(key),
    incr: (key) => r.incr(key),
  };
}

async function route(request: Request): Promise<Response> {
  let s: Store;
  try {
    s = store();
  } catch (err) {
    console.error(err);
    return new Response(JSON.stringify({ error: "Phone scanning isn't set up on this server yet." }), {
      status: 503,
      headers: { "content-type": "application/json" },
    });
  }
  return handle(request, s);
}

export const GET = route;
export const POST = route;
export const DELETE = route;
