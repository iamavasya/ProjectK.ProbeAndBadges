import { describe, expect, it } from "vitest";
import app from "../src/index";
import spec from "../public/openapi.json";
import { call } from "./helpers";

// /docs is rendered from public/openapi.json; these keep it in step with the routes.
const specPaths = Object.keys(spec.paths).sort();
const publicRoutes = [
  ...new Set(
    app.routes
      .filter((r) => r.method === "GET" && r.path.startsWith("/v1/") && !r.path.startsWith("/v1/admin") && !r.path.startsWith("/v1/submissions"))
      .map((r) => r.path.replace(/:(\w+)/g, "{$1}")),
  ),
].sort();

describe("openapi.json", () => {
  it("documents every public route and nothing else", () => {
    expect(specPaths).toEqual(publicRoutes);
  });

  it.each(specPaths)("%s answers with a documented status", async (path) => {
    const op = (spec.paths as Record<string, { get: { parameters?: { name?: string; in?: string; example?: unknown }[]; responses: Record<string, unknown> } }>)[path].get;
    const url = path.replace(/\{(\w+)\}/g, (_, name) => String(op.parameters?.find((p) => p.name === name)?.example ?? "x"));
    const res = await call(url);
    expect(Object.keys(op.responses)).toContain(String(res.status));
  });
});
