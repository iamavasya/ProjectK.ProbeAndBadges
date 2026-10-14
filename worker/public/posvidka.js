// Посвідка про здобуття вмілості чи відзначення, landscape A6. Reads the same public `GET /v1/catalog` as
// the catalog page and prints the sheet with the browser's own print dialog, so nothing is stored
// anywhere. Links: `/posvidka?kind=badges&id=<id>&krai=cz`. The name stays out of the URL.

// One template per kind: «відповів всім вимогам <noun> … та має право носити <pronoun> відзнаку».
const TEMPLATES = {
  badges: { noun: "вмілості", pronoun: "її" },
  awards: { noun: "відзначення", pronoun: "його" },
};

const $ = (sel) => document.querySelector(sel);
const params = new URLSearchParams(location.search);
const state = {
  catalog: null,
  kind: TEMPLATES[params.get("kind")] ? params.get("kind") : "badges",
  krai: params.get("krai")?.toLowerCase().replace(/[^a-z]/g, "").slice(0, 3) ?? "",
};

const isoToday = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};
// "2026-10-14" → "14 жовтня 2026 р."; parsed as local noon so no time zone shifts the day.
const longDate = (iso) => (iso ? new Date(`${iso}T12:00`).toLocaleDateString("uk-UA", { day: "numeric", month: "long", year: "numeric" }) : "");

const current = () => state.catalog?.[state.kind].find((x) => x.id === $("#item").value);

function fillItems(selectedId) {
  const list = [...state.catalog[state.kind]].sort(
    (a, b) => (a.group ?? "").localeCompare(b.group ?? "") || (a.level ?? 0) - (b.level ?? 0) || a.title.localeCompare(b.title, "uk"),
  );
  const select = $("#item");
  select.replaceChildren(...list.map((x) => new Option(x.title, x.id)));
  if (list.some((x) => x.id === selectedId)) select.value = selectedId;
  select.disabled = list.length === 0;
  $("#print").disabled = list.length === 0;
}

function render() {
  const t = TEMPLATES[state.kind];
  const x = current();
  const images = x?.images ?? {};
  const both = Boolean(images.color && images.bw);
  $("#field-image").hidden = !both;
  const src = (both ? images[$("#image").value] : images.color ?? images.bw) ?? null;

  $("#s-verb").textContent = $("#verb").value;
  $("#s-noun").textContent = t.noun;
  $("#s-pronoun").textContent = t.pronoun;
  $("#s-title").textContent = x?.title ?? "";
  if (src) $("#s-image").src = src;
  else $("#s-image").removeAttribute("src");
  $("#s-name").textContent = $("#name").value.trim();
  $("#s-date").textContent = longDate($("#date").value);
  document.title = x ? `Посвідка: ${x.title} · Лілейка` : "Посвідка · Каталог · Лілейка";

  if (!state.catalog) return;
  const url = new URL(location.href);
  url.searchParams.set("kind", state.kind);
  if (x) url.searchParams.set("id", x.id);
  else url.searchParams.delete("id");
  history.replaceState(null, "", url);
  $("#back").href = `/${state.krai ? `?krai=${state.krai}` : ""}${x ? `#${state.kind}/${encodeURIComponent(x.id)}` : ""}`;
}

// The A6 sheet is ~397px wide; on a phone it is zoomed down to fit. Print resets the zoom.
function fit() {
  const sheet = $("#sheet");
  sheet.style.zoom = "";
  const scale = Math.min(1, $("#preview").clientWidth / sheet.offsetWidth);
  if (scale < 1) sheet.style.zoom = String(scale);
}

async function load() {
  const res = await fetch(`/v1/catalog${state.krai ? `?krai=${state.krai}` : ""}`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  state.catalog = await res.json();
  document.querySelector(`input[name="kind"][value="${state.kind}"]`).checked = true;
  fillItems(params.get("id"));
  render();
}

$("#form").addEventListener("change", (e) => {
  if (e.target.name === "kind") {
    state.kind = e.target.value;
    fillItems(null);
  }
  render();
});
$("#form").addEventListener("input", render);
$("#today").addEventListener("click", () => {
  $("#date").value = isoToday();
  render();
});
$("#form").addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!$("#date").value) $("#date").value = isoToday();
  render();
  // Print only once the picture is decoded, or the sheet may go out without it.
  await $("#s-image").decode?.().catch(() => {});
  await document.fonts.ready;
  print();
});

$("#date").value = isoToday();
new ResizeObserver(fit).observe($("#preview"));
render();

load().catch((e) => {
  $("#error-body").textContent = `Каталог не завантажився (${e.message}). Спробуй за хвилину.`;
  $("#error").hidden = false;
  $("#item").replaceChildren(new Option("недоступно", ""));
});
