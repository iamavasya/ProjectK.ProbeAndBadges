import { env } from "cloudflare:test";
import { exports } from "cloudflare:workers";

const testEnv = env;

const b64url = (bytes: Uint8Array | string) =>
  btoa(typeof bytes === "string" ? bytes : String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

/** A Cf-Access-Jwt-Assertion as Cloudflare Access would send it for this email. */
export async function accessToken(email: string, overrides: Record<string, unknown> = {}): Promise<string> {
  const jwk = JSON.parse(testEnv.TEST_PRIVATE_JWK) as JsonWebKey & { kid: string };
  const key = await crypto.subtle.importKey("jwk", jwk, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const header = b64url(JSON.stringify({ alg: "RS256", kid: jwk.kid }));
  const payload = b64url(
    JSON.stringify({ aud: [testEnv.ACCESS_AUD], iss: testEnv.ACCESS_TEAM_DOMAIN, email, exp: Math.floor(Date.now() / 1000) + 600, ...overrides }),
  );
  const signature = new Uint8Array(await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, new TextEncoder().encode(`${header}.${payload}`)));
  return `${header}.${payload}.${b64url(signature)}`;
}

export async function call(path: string, init: RequestInit & { as?: string; json?: unknown } = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  if (init.as) headers.set("Cf-Access-Jwt-Assertion", await accessToken(init.as));
  let body = init.body;
  if (init.json !== undefined) {
    headers.set("Content-Type", "application/json");
    body = JSON.stringify(init.json);
  }
  return (exports as unknown as { default: Fetcher }).default.fetch(new Request(`https://catalog.example${path}`, { ...init, headers, body }));
}

export const OWNER = "owner@example.com";

export const svg = (label = "x") => new TextEncoder().encode(`<svg xmlns="http://www.w3.org/2000/svg"><text>${label}</text></svg>`);
