-- Catalog items as JSON documents, one row each. The shape is checked by the Worker
-- (src/validate.ts), so adding a field to the format needs no migration.
CREATE TABLE entries (
  kind        TEXT NOT NULL CHECK (kind IN ('category', 'badge', 'probe', 'award', 'override')),
  id          TEXT NOT NULL,           -- item id; "<krai>|<target>" for overrides
  krai        TEXT,
  json        TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  updated_by  TEXT NOT NULL,
  PRIMARY KEY (kind, id)
);

-- Image files live in R2 under images/<name>; this is their index. sha256 versions the public
-- URL (?v=...), so a replaced image is never served stale from a cache.
CREATE TABLE images (
  name          TEXT PRIMARY KEY,
  sha256        TEXT NOT NULL,
  content_type  TEXT NOT NULL,
  size          INTEGER NOT NULL,
  uploaded_at   TEXT NOT NULL,
  uploaded_by   TEXT NOT NULL
);

-- Who may sign in. Owners can also be named in OWNER_EMAILS.
CREATE TABLE editors (
  email       TEXT PRIMARY KEY,        -- lowercase
  role        TEXT NOT NULL CHECK (role IN ('owner', 'editor', 'contributor')),
  krai        TEXT,                    -- comma-separated krai codes an editor may change
  created_at  TEXT NOT NULL,
  created_by  TEXT NOT NULL
);

-- A bundle (catalog.json from a zip, or a single edit by a contributor) waiting for review.
-- Its images wait in R2 under pending/<id>/<name>.
CREATE TABLE submissions (
  id            TEXT PRIMARY KEY,
  status        TEXT NOT NULL CHECK (status IN ('pending', 'applied', 'rejected')),
  krai          TEXT,
  bundle_json   TEXT NOT NULL,
  images_json   TEXT NOT NULL DEFAULT '{}',  -- name -> sha256 of uploaded pending images
  created_at    TEXT NOT NULL,
  created_by    TEXT NOT NULL,
  reviewed_at   TEXT,
  reviewed_by   TEXT,
  note          TEXT
);
CREATE INDEX submissions_status ON submissions (status, created_at);

-- Every change, with the document before and after, so any edit can be traced and undone.
CREATE TABLE audit (
  seq          INTEGER PRIMARY KEY AUTOINCREMENT,
  at           TEXT NOT NULL,
  actor        TEXT NOT NULL,
  action       TEXT NOT NULL,
  kind         TEXT,
  entry_id     TEXT,
  before_json  TEXT,
  after_json   TEXT,
  submission   TEXT
);

-- revision: bumped by every write; public responses are cached per revision.
CREATE TABLE meta (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL
);
INSERT INTO meta (key, value) VALUES ('revision', '0');
