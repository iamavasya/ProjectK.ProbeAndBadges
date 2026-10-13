// Who is calling. Cloudflare Access sits in front of /admin and /v1/admin|submissions and signs
// in editors (one-time PIN by email, or Google); it forwards a signed JWT in
// Cf-Access-Jwt-Assertion. The Worker verifies that JWT itself (a request that reaches the
// Worker some other way must not pass), then looks the email up in the editors table.

import type { Env } from "./env";

export type Role = "owner" | "editor" | "contributor";

export interface User {
  email: string;
  role: Role;
  /** Krai codes an editor may change; empty for owners (all) and contributors (none). */
  krai: string[];
}

interface Jwk extends JsonWebKey {
  kid: string;
}

let jwksCache: { keys: Jwk[]; until: number } | null = null;

async function loadKeys(env: Env): Promise<Jwk[]> {
  if (env.ACCESS_JWKS) return (JSON.parse(env.ACCESS_JWKS) as { keys: Jwk[] }).keys;
  if (jwksCache && jwksCache.until > Date.now()) return jwksCache.keys;
  const res = await fetch(`${env.ACCESS_TEAM_DOMAIN}/cdn-cgi/access/certs`);
  if (!res.ok) throw new Error(`Access certs: HTTP ${res.status}`);
  const { keys } = (await res.json()) as { keys: Jwk[] };
  jwksCache = { keys, until: Date.now() + 60 * 60 * 1000 };
  return keys;
}

function b64url(input: string): Uint8Array {
  const s = input.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(input.length / 4) * 4, "=");
  return Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
}

/** Returns the verified email, or null when the token is missing, forged, expired or meant
 * for another application. */
export async function verifyAccessJwt(token: string, env: Env, now = Date.now()): Promise<string | null> {
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  try {
    const header = JSON.parse(new TextDecoder().decode(b64url(parts[0]))) as { alg: string; kid: string };
    if (header.alg !== "RS256") return null;
    const jwk = (await loadKeys(env)).find((k) => k.kid === header.kid);
    if (!jwk) return null;

    const key = await crypto.subtle.importKey("jwk", jwk, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
    const ok = await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, b64url(parts[2]), new TextEncoder().encode(`${parts[0]}.${parts[1]}`));
    if (!ok) return null;

    const claims = JSON.parse(new TextDecoder().decode(b64url(parts[1]))) as { aud: string | string[]; exp: number; nbf?: number; iss: string; email?: string };
    const aud = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
    if (!aud.includes(env.ACCESS_AUD)) return null;
    if (claims.iss !== env.ACCESS_TEAM_DOMAIN) return null;
    if (claims.exp * 1000 < now || (claims.nbf && claims.nbf * 1000 > now + 60_000)) return null;
    return claims.email?.toLowerCase() ?? null;
  } catch {
    return null;
  }
}

export async function authenticate(req: Request, env: Env): Promise<User | null> {
  let email: string | null = null;
  const token = req.headers.get("Cf-Access-Jwt-Assertion");
  if (token) {
    email = await verifyAccessJwt(token, env);
  } else if (env.ENVIRONMENT === "dev" && env.DEV_ACCESS_EMAIL && ["localhost", "127.0.0.1"].includes(new URL(req.url).hostname)) {
    // Local `wrangler dev` has no Access in front; .dev.vars names who you are.
    email = env.DEV_ACCESS_EMAIL.toLowerCase();
  }
  if (!email) return null;

  const owners = (env.OWNER_EMAILS ?? "").split(",").map((e) => e.trim().toLowerCase()).filter(Boolean);
  if (owners.includes(email)) return { email, role: "owner", krai: [] };

  const row = await env.DB.prepare("SELECT role, krai FROM editors WHERE email = ?").bind(email).first<{ role: Role; krai: string | null }>();
  if (!row) return null;
  return { email, role: row.role, krai: (row.krai ?? "").split(",").map((k) => k.trim()).filter(Boolean) };
}

export function canEditKrai(user: User, krai: string | null | undefined): boolean {
  if (user.role === "owner") return true;
  return user.role === "editor" && !!krai && user.krai.includes(krai);
}
