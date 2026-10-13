// Catalog format v2. Mirrors src/ProjectK.Catalog/Models.cs; catalog/catalog.json is the
// reference instance. Image fields hold file names; the public API turns them into URLs.

export const SCHEMA_VERSION = 2;

export const STATUSES = ["approved", "trial", "retired"] as const;
export type Status = (typeof STATUSES)[number];

/** УПП (пташата), УПН (новацтво), УПЮ (юнацтво), УСП (старші пластуни), УПС (сеніори). */
export const ULADY = ["upp", "upn", "upyu", "usp", "ups"] as const;

export interface ImageSet {
  color?: string | null;
  bw?: string | null;
}

export interface Requirement {
  id: string;
  text: string;
}

export interface Category {
  id: string;
  title: string;
}

export interface Badge {
  id: string;
  family?: string | null;
  level?: number | null;
  title: string;
  categories: string[];
  ulady: string[];
  krai?: string | null;
  status: Status;
  images: ImageSet;
  prerequisites: string[];
  requirements: Requirement[];
  instructorRequirements?: string | null;
  author?: string | null;
  updatedOn?: string | null;
  notes: string[];
}

export interface ProbePoint {
  id: string;
  text: string;
}

export interface ProbeSection {
  id: string;
  code: string;
  title: string;
  points: ProbePoint[];
}

export interface Probe {
  id: string;
  title: string;
  ulad: string;
  krai?: string | null;
  order: number;
  status: Status;
  images: ImageSet;
  sections: ProbeSection[];
  points: ProbePoint[];
}

/** Відзначення, відзнака or вишкіл: `group` tells УПЮ відзначення, КВ, ЗБ, ЛШ, ШБ apart. */
export interface Award {
  id: string;
  group: string;
  title: string;
  ulad?: string | null;
  krai?: string | null;
  level?: number | null;
  status: Status;
  images: ImageSet;
  description?: string | null;
  requirements: Requirement[];
}

export interface OverridePoint {
  section?: string | null;
  id: string;
  text: string;
}

/** What one krai changes in an item it did not author. */
export interface Override {
  krai: string;
  /** "badge:<id>", "probe:<id>" or "award:<id>". */
  target: string;
  hidden?: boolean;
  title?: string | null;
  addRequirements: Requirement[];
  removeRequirements: string[];
  addPoints: OverridePoint[];
  removePoints: string[];
  note?: string | null;
}

export interface BundleSource {
  krai?: string | null;
  name?: string | null;
  contact?: string | null;
  generator?: string | null;
  generatedAtUtc?: string | null;
}

export interface Bundle {
  schemaVersion: number;
  source?: BundleSource | null;
  categories: Category[];
  badges: Badge[];
  probes: Probe[];
  awards: Award[];
  overrides: Override[];
}

/** Rows in D1 are one JSON document per item; `kind` says which list it belongs to. */
export const KINDS = ["category", "badge", "probe", "award", "override"] as const;
export type Kind = (typeof KINDS)[number];

export type Entry = Category | Badge | Probe | Award | Override;

export function overrideKey(o: Pick<Override, "krai" | "target">): string {
  return `${o.krai}|${o.target}`;
}

export function entryId(kind: Kind, entry: Entry): string {
  return kind === "override" ? overrideKey(entry as Override) : (entry as { id: string }).id;
}

export function entryKrai(kind: Kind, entry: Entry): string | null {
  if (kind === "category") return null;
  return (entry as { krai?: string | null }).krai ?? null;
}

export function emptyBundle(): Bundle {
  return { schemaVersion: SCHEMA_VERSION, categories: [], badges: [], probes: [], awards: [], overrides: [] };
}

const LISTS: Record<Kind, keyof Bundle> = {
  category: "categories",
  badge: "badges",
  probe: "probes",
  award: "awards",
  override: "overrides",
};

export function listOf(bundle: Bundle, kind: Kind): Entry[] {
  return bundle[LISTS[kind]] as Entry[];
}

/**
 * Fills the defaults a hand-written catalog.json may leave out, the way the .NET models do, so
 * the validator and resolver see the same shape whatever sent it.
 */
export function normalizeBundle(raw: unknown): Bundle {
  const b = (raw ?? {}) as Partial<Bundle>;
  return {
    schemaVersion: b.schemaVersion ?? SCHEMA_VERSION,
    source: b.source ?? null,
    categories: (b.categories ?? []).map((c) => ({ ...c })),
    badges: (b.badges ?? []).map(normalizeBadge),
    probes: (b.probes ?? []).map(normalizeProbe),
    awards: (b.awards ?? []).map(normalizeAward),
    overrides: (b.overrides ?? []).map(normalizeOverride),
  };
}

export function normalizeEntry(kind: Kind, raw: unknown): Entry {
  switch (kind) {
    case "badge":
      return normalizeBadge(raw as Badge);
    case "probe":
      return normalizeProbe(raw as Probe);
    case "award":
      return normalizeAward(raw as Award);
    case "override":
      return normalizeOverride(raw as Override);
    default:
      return { ...(raw as Category) };
  }
}

function normalizeBadge(b: Partial<Badge>): Badge {
  return {
    ...b,
    id: b.id!,
    title: b.title!,
    categories: b.categories ?? [],
    ulady: b.ulady ?? [],
    status: b.status ?? "approved",
    images: b.images ?? {},
    prerequisites: b.prerequisites ?? [],
    requirements: b.requirements ?? [],
    notes: b.notes ?? [],
  };
}

function normalizeProbe(p: Partial<Probe>): Probe {
  return {
    ...p,
    id: p.id!,
    title: p.title!,
    ulad: p.ulad ?? "upyu",
    order: p.order ?? 0,
    status: p.status ?? "approved",
    images: p.images ?? {},
    sections: (p.sections ?? []).map((s) => ({ ...s, points: s.points ?? [] })),
    points: p.points ?? [],
  };
}

function normalizeAward(a: Partial<Award>): Award {
  return {
    ...a,
    id: a.id!,
    group: a.group!,
    title: a.title!,
    status: a.status ?? "approved",
    images: a.images ?? {},
    requirements: a.requirements ?? [],
  };
}

function normalizeOverride(o: Partial<Override>): Override {
  return {
    ...o,
    krai: o.krai!,
    target: o.target!,
    addRequirements: o.addRequirements ?? [],
    removeRequirements: o.removeRequirements ?? [],
    addPoints: o.addPoints ?? [],
    removePoints: o.removePoints ?? [],
  };
}
