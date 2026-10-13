import { describe, expect, it } from "vitest";
import committed from "../../catalog/catalog.json";
import { normalizeBundle, type Bundle } from "../src/model";
import { merge, resolve } from "../src/resolve";
import { checkFields, checkImage, validate } from "../src/validate";

const base = (): Bundle =>
  normalizeBundle({
    categories: [{ id: "mystetska", title: "Мистецька" }],
    badges: [
      { id: "aktorstvo-i", title: "Акторство I", krai: "ua", ulady: ["upyu"], categories: ["mystetska"], images: { color: "a1.svg" }, requirements: [{ id: "1", text: "Прочитати п'єсу." }] },
      { id: "staryi", title: "Стара", krai: "ua", ulady: ["upyu"], categories: ["mystetska"], status: "retired", images: { color: "a1.svg" }, requirements: [{ id: "1", text: "x" }] },
    ],
    probes: [
      { id: "probe-1", title: "Перша проба", krai: "ua", sections: [{ id: "probe-1-a", code: "А", title: "А", points: [{ id: "probe-1-a-1", text: "Знає" }] }] },
    ],
  });

describe("committed catalog", () => {
  it("passes the same checks the .NET validator runs", () => {
    expect(checkFields(committed)).toEqual([]);
    const bundle = normalizeBundle(committed);
    const images = new Set(
      [...bundle.badges, ...bundle.probes, ...bundle.awards].flatMap((x) => [x.images.color, x.images.bw]).filter((n): n is string => !!n),
    );
    const report = validate(bundle, images);
    expect(report.issues.filter((i) => i.severity === "error")).toEqual([]);
    expect(bundle.badges.length).toBeGreaterThan(300);
  });
});

describe("validate", () => {
  it("rejects unknown fields so template typos surface", () => {
    expect(checkFields({ schemaVersion: 2, badges: [{ id: "x", requirments: [] }] })).toEqual([
      expect.objectContaining({ path: "badges[0].requirments" }),
    ]);
  });

  it("accepts a krai contribution that overrides base items with prefixed ids", () => {
    const contribution = normalizeBundle({
      source: { krai: "cz" },
      overrides: [{ krai: "cz", target: "probe:probe-1", addPoints: [{ section: "А", id: "cz-probe-1-a-1", text: "Знає історію Пласту в Чехії" }] }],
    });
    expect(validate(contribution, new Set(), base()).valid).toBe(true);
  });

  it("rejects foreign krai, unprefixed ids and unknown sections", () => {
    const contribution = normalizeBundle({
      source: { krai: "cz" },
      overrides: [{ krai: "pl", target: "probe:probe-1", addPoints: [{ section: "Я", id: "point-x", text: "x" }] }],
    });
    const messages = validate(contribution, new Set(), base()).issues.map((i) => i.message);
    expect(messages).toEqual(
      expect.arrayContaining([
        expect.stringContaining("may only carry its own overrides"),
        expect.stringContaining("must start with 'pl-'"),
        expect.stringContaining("no section 'Я'"),
      ]),
    );
  });

  it("does not let a krai take over another krai's badge", () => {
    const contribution = normalizeBundle({ source: { krai: "cz" }, badges: [{ ...base().badges[0], krai: null }] });
    expect(validate(contribution, new Set(["a1.svg"]), base()).issues.map((i) => i.message)).toContainEqual(expect.stringContaining("already belongs to krai 'ua'"));
  });

  it.each(["<svg><script>alert(1)</script></svg>", '<svg><rect onload="x()"/></svg>', '<svg><image href="https://evil.example/x.png"/></svg>'])("rejects active SVG %s", (s) => {
    expect(checkImage("x.svg", new TextEncoder().encode(s)).some((i) => i.severity === "error")).toBe(true);
  });

  it("rejects a .png that is not a PNG", () => {
    expect(checkImage("x.png", new TextEncoder().encode("<svg/>")).some((i) => i.severity === "error")).toBe(true);
  });
});

describe("resolve", () => {
  const withOverrides = (): Bundle => ({
    ...base(),
    overrides: normalizeBundle({
      overrides: [
        { krai: "cz", target: "badge:aktorstvo-i", addRequirements: [{ id: "cz-1", text: "Виступить у Празі." }], removeRequirements: ["1"] },
        { krai: "cz", target: "probe:probe-1", addPoints: [{ section: "А", id: "cz-probe-1-a-1", text: "Чехія" }] },
        { krai: "pl", target: "badge:aktorstvo-i", hidden: true },
      ],
    }).overrides,
  });

  it("applies only the reader's krai overrides and drops retired items", () => {
    const cz = resolve(withOverrides(), "cz");
    expect(cz.badges.map((b) => b.id)).toEqual(["aktorstvo-i"]);
    expect(cz.badges[0].requirements).toEqual([{ id: "cz-1", text: "Виступить у Празі." }]);
    expect(cz.probes[0].sections[0].points.map((p) => p.id)).toEqual(["probe-1-a-1", "cz-probe-1-a-1"]);
    expect(cz.overrides).toEqual([]);

    expect(resolve(withOverrides(), "pl").badges).toEqual([]);
    expect(resolve(withOverrides(), null).badges[0].requirements[0].id).toBe("1");
  });

  it("merges a contribution by id and gives new items the contribution's krai", () => {
    const contribution = normalizeBundle({
      source: { krai: "cz" },
      badges: [{ id: "cz-nova", title: "Нова", ulady: ["upyu"], categories: ["mystetska"], images: { color: "a1.svg" }, requirements: [{ id: "1", text: "x" }] }],
    });
    const { catalog, changes } = merge(base(), contribution);
    expect(changes.map((c) => `${c.action} ${c.kind}:${c.id}`)).toEqual(["added badge:cz-nova"]);
    expect(catalog.badges.find((b) => b.id === "cz-nova")?.krai).toBe("cz");
    expect(merge(catalog, contribution).changes.filter((c) => c.kind === "badge")).toEqual([]);
  });
});
