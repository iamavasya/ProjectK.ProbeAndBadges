import type { Env } from "./env";
import type { Bundle, Entry, Kind } from "./model";
import { emptyBundle, entryKrai, listOf, SCHEMA_VERSION } from "./model";
import type { Change } from "./resolve";

export interface ImageInfo {
  name: string;
  sha256: string;
  contentType: string;
  size: number;
}

export async function revision(env: Env): Promise<number> {
  const row = await env.DB.prepare("SELECT value FROM meta WHERE key = 'revision'").first<{ value: string }>();
  return Number(row?.value ?? 0);
}

/** The whole stored catalog, overrides and retired items included. A few hundred rows. */
export async function loadCatalog(env: Env): Promise<Bundle> {
  const { results } = await env.DB.prepare("SELECT kind, json FROM entries ORDER BY rowid").all<{ kind: Kind; json: string }>();
  const bundle = emptyBundle();
  bundle.source = { name: "Пласт: каталог вмілостей, проб і відзначень" };
  for (const row of results) listOf(bundle, row.kind).push(JSON.parse(row.json) as Entry);
  bundle.schemaVersion = SCHEMA_VERSION;
  return bundle;
}

export async function loadImages(env: Env): Promise<Map<string, ImageInfo>> {
  const { results } = await env.DB.prepare("SELECT name, sha256, content_type, size FROM images").all<{ name: string; sha256: string; content_type: string; size: number }>();
  return new Map(results.map((r) => [r.name, { name: r.name, sha256: r.sha256, contentType: r.content_type, size: r.size }]));
}

/**
 * Writes changes, their audit rows and the revision bump in one D1 batch, which runs as a
 * single transaction: readers see all of it or none.
 */
export function changeStatements(env: Env, changes: Change[], actor: string, action: string, submission: string | null = null): D1PreparedStatement[] {
  const now = new Date().toISOString();
  const statements: D1PreparedStatement[] = [];
  for (const c of changes) {
    statements.push(
      env.DB.prepare(
        `INSERT INTO entries (kind, id, krai, json, updated_at, updated_by) VALUES (?, ?, ?, ?, ?, ?)
         ON CONFLICT (kind, id) DO UPDATE SET krai = excluded.krai, json = excluded.json, updated_at = excluded.updated_at, updated_by = excluded.updated_by`,
      ).bind(c.kind, c.id, entryKrai(c.kind, c.after), JSON.stringify(c.after), now, actor),
      auditStatement(env, now, actor, `${action}:${c.action}`, c.kind, c.id, c.before, c.after, submission),
    );
  }
  return statements;
}

export function deleteStatements(env: Env, kind: Kind, id: string, before: Entry, actor: string): D1PreparedStatement[] {
  const now = new Date().toISOString();
  return [
    env.DB.prepare("DELETE FROM entries WHERE kind = ? AND id = ?").bind(kind, id),
    auditStatement(env, now, actor, "delete", kind, id, before, null, null),
  ];
}

export function auditStatement(env: Env, at: string, actor: string, action: string, kind: string | null, id: string | null, before: unknown, after: unknown, submission: string | null): D1PreparedStatement {
  return env.DB.prepare("INSERT INTO audit (at, actor, action, kind, entry_id, before_json, after_json, submission) VALUES (?, ?, ?, ?, ?, ?, ?, ?)").bind(
    at,
    actor,
    action,
    kind,
    id,
    before == null ? null : JSON.stringify(before),
    after == null ? null : JSON.stringify(after),
    submission,
  );
}

export function bumpRevision(env: Env): D1PreparedStatement {
  return env.DB.prepare("UPDATE meta SET value = CAST(value AS INTEGER) + 1 WHERE key = 'revision'");
}

export function imageStatement(env: Env, image: ImageInfo, actor: string): D1PreparedStatement {
  return env.DB.prepare(
    `INSERT INTO images (name, sha256, content_type, size, uploaded_at, uploaded_by) VALUES (?, ?, ?, ?, ?, ?)
     ON CONFLICT (name) DO UPDATE SET sha256 = excluded.sha256, content_type = excluded.content_type, size = excluded.size, uploaded_at = excluded.uploaded_at, uploaded_by = excluded.uploaded_by`,
  ).bind(image.name, image.sha256, image.contentType, image.size, new Date().toISOString(), actor);
}

export async function sha256Hex(bytes: Uint8Array | ArrayBuffer): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export interface SubmissionRow {
  id: string;
  status: "pending" | "applied" | "rejected";
  krai: string | null;
  bundle_json: string;
  images_json: string;
  created_at: string;
  created_by: string;
  reviewed_at: string | null;
  reviewed_by: string | null;
  note: string | null;
}

export async function getSubmission(env: Env, id: string): Promise<SubmissionRow | null> {
  return env.DB.prepare("SELECT * FROM submissions WHERE id = ?").bind(id).first<SubmissionRow>();
}
