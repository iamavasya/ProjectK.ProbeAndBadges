// API reference rendered from /openapi.json, so the page and the spec cannot drift apart. Every
// operation gets a card with its parameters, responses and a «Спробувати» panel that calls the
// live API; schemas get field tables. The guide sections below are plain HTML in docs.html.

const $ = (sel, root = document) => root.querySelector(sel);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
// Spec descriptions use only `code` and **bold**.
const md = (s) => esc(s).replace(/`([^`]+)`/g, "<code>$1</code>").replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
const IN = { path: "path", query: "query", header: "header" };
const TRUNCATE = 120;

let spec;
const ref = (obj) => {
  if (!obj?.$ref) return obj;
  return obj.$ref.replace(/^#\//, "").split("/").reduce((o, k) => o[k], spec);
};
const refName = (obj) => obj?.$ref?.split("/").pop();

/** "string", "Badge", "Badge[]", "integer | null", with schema names linked. */
function typeHtml(schema) {
  if (!schema) return "";
  if (schema.$ref) return `<a href="#schema-${refName(schema)}">${esc(refName(schema))}</a>`;
  if (schema.type === "array") return `${typeHtml(schema.items)}[]`;
  const types = [schema.type].flat().filter(Boolean);
  let s = types.map(esc).join(" | ");
  if (schema.format && schema.format !== "binary") s += ` <span class="muted">(${esc(schema.format)})</span>`;
  return s;
}

const exampleOf = (p) => p.example ?? p.schema?.example;

function paramRow(p) {
  const s = p.schema ?? {};
  const values = s.enum ?? s.examples;
  const example = exampleOf(p);
  return `<li class="field-row">
    <div class="field-name">
      <code class="name">${esc(p.name)}</code>
      <span class="loc">${IN[p.in] ?? esc(p.in)}</span>
      ${p.required ? '<span class="req">обов’язковий</span>' : ""}
    </div>
    <div class="field-body">
      <div class="type">${typeHtml(s)}${s.pattern ? ` <span class="muted">${esc(s.pattern)}</span>` : ""}</div>
      ${p.description ? `<p>${md(p.description)}</p>` : ""}
      ${values ? `<div class="values">${s.enum ? "Значення:" : "Наприклад:"} ${values.map((v) => `<code>${esc(v)}</code>`).join(" ")}</div>` : ""}
      ${!values && example !== undefined ? `<div class="values">Приклад: <code>${esc(example)}</code></div>` : ""}
    </div>
  </li>`;
}

function responseRow(code, r) {
  r = ref(r);
  const content = r.content ?? {};
  const json = content["application/json"];
  const types = Object.keys(content);
  const kind = code.startsWith("2") ? "ok" : code.startsWith("3") ? "info" : "err";
  return `<li class="resp">
    <span class="status ${kind}">${esc(code)}</span>
    <div>
      <p>${md(r.description)}</p>
      ${json?.schema ? `<div class="type">Тіло: ${typeHtml(json.schema)}</div>` : types.length ? `<div class="type">Тіло: ${types.map((t) => `<code>${esc(t)}</code>`).join(" ")}</div>` : ""}
    </div>
  </li>`;
}

function pathHtml(path) {
  return esc(path).replace(/\{([^}]+)\}/g, '<span class="var">{$1}</span>');
}

function operationCard(path, op) {
  const params = (op.parameters ?? []).map(ref);
  const tryable = params.filter((p) => p.in !== "header");
  return `<article class="op" id="op-${op.operationId}">
    <header class="op-head">
      <span class="method">GET</span>
      <code class="path">${pathHtml(path)}</code>
      <a class="anchor" href="#op-${op.operationId}" aria-label="Посилання на цей запит">#</a>
    </header>
    <h3>${esc(op.summary)}</h3>
    ${op.description ? `<p class="op-desc">${md(op.description)}</p>` : ""}
    ${params.length ? `<h4>Параметри</h4><ul class="fields">${params.map(paramRow).join("")}</ul>` : ""}
    <h4>Відповіді</h4>
    <ul class="responses">${Object.entries(op.responses).map(([c, r]) => responseRow(c, r)).join("")}</ul>
    <details class="try" data-path="${esc(path)}">
      <summary class="btn btn-secondary">Спробувати</summary>
      <form class="try-form">
        ${tryable
          .map((p) => {
            const s = p.schema ?? {};
            const value = p.in === "path" ? exampleOf(p) ?? "" : "";
            const input = s.enum
              ? `<select name="${esc(p.name)}" data-in="${p.in}"><option value="">—</option>${s.enum.map((v) => `<option>${esc(v)}</option>`).join("")}</select>`
              : `<input name="${esc(p.name)}" data-in="${p.in}" value="${esc(value)}" placeholder="${exampleOf(p) !== undefined ? `напр. ${esc(exampleOf(p))}` : ""}" ${p.required ? "required" : ""} autocomplete="off" spellcheck="false">`;
            return `<label class="field"><span><code>${esc(p.name)}</code>${p.required ? " *" : ""}</span>${input}</label>`;
          })
          .join("")}
        <div class="try-url"><code></code><button type="button" class="btn btn-text copy">Копіювати</button></div>
        <button class="btn" type="submit">Надіслати</button>
      </form>
      <div class="try-result" hidden></div>
    </details>
  </article>`;
}

function schemaCard(name, schema) {
  const required = new Set(schema.required ?? []);
  const rows = Object.entries(schema.properties ?? {}).map(([key, prop]) => {
    const p = prop.$ref ? { ...ref(prop), $ref: prop.$ref } : prop;
    const values = p.enum ?? p.items?.enum;
    return `<li class="field-row">
      <div class="field-name">
        <code class="name">${esc(key)}</code>
        ${required.has(key) ? "" : '<span class="loc">може не бути</span>'}
      </div>
      <div class="field-body">
        <div class="type">${typeHtml(prop)}</div>
        ${p.description && !prop.$ref ? `<p>${md(p.description)}</p>` : ""}
        ${values ? `<div class="values">Значення: ${values.map((v) => `<code>${esc(v)}</code>`).join(" ")}</div>` : ""}
      </div>
    </li>`;
  });
  return `<article class="op schema" id="schema-${name}">
    <h3><code>${esc(name)}</code></h3>
    ${schema.description ? `<p class="op-desc">${md(schema.description)}</p>` : ""}
    <ul class="fields">${rows.join("")}</ul>
  </article>`;
}

function render() {
  const byTag = new Map(spec.tags.map((t) => [t.name, { tag: t, ops: [] }]));
  for (const [path, item] of Object.entries(spec.paths)) {
    const op = item.get;
    byTag.get(op.tags[0]).ops.push([path, op]);
  }

  $("#reference").innerHTML =
    [...byTag.values()]
      .map(
        ({ tag, ops }, i) => `<section class="ref-group" id="tag-${i}">
          <h2>${esc(tag.name)}</h2>
          ${tag.description ? `<p class="muted">${md(tag.description)}</p>` : ""}
          ${ops.map(([path, op]) => operationCard(path, op)).join("")}
        </section>`,
      )
      .join("") +
    `<section class="ref-group" id="schemas">
      <h2>Схеми</h2>
      <p class="muted">Поля в тілах відповідей. Поле з позначкою «може не бути» відсутнє або має <code>null</code>.</p>
      ${Object.entries(spec.components.schemas).map(([n, s]) => schemaCard(n, s)).join("")}
    </section>`;

  $("#toc-api").innerHTML =
    [...byTag.values()]
      .map(
        ({ tag, ops }, i) => `<li><a href="#tag-${i}" class="toc-group">${esc(tag.name)}</a><ul>${ops
          .map(([path, op]) => `<li><a href="#op-${op.operationId}"><span class="m">GET</span>${esc(path.replace("/v1", ""))}</a></li>`)
          .join("")}</ul></li>`,
      )
      .join("") + `<li><a href="#schemas" class="toc-group">Схеми</a></li>`;

  for (const details of document.querySelectorAll(".try")) setupTry(details);
  if (location.hash) document.getElementById(location.hash.slice(1))?.scrollIntoView();
}

function requestUrl(details) {
  let path = details.dataset.path;
  const query = new URLSearchParams();
  for (const el of details.querySelectorAll("[data-in]")) {
    const v = el.value.trim();
    if (el.dataset.in === "path") path = path.replace(`{${el.name}}`, encodeURIComponent(v || `{${el.name}}`));
    else if (v) query.set(el.name, v);
  }
  const qs = query.toString();
  return path + (qs ? `?${qs}` : "");
}

function setupTry(details) {
  const form = $("form", details);
  const urlCode = $(".try-url code", details);
  const result = $(".try-result", details);
  const update = () => (urlCode.textContent = `GET ${location.origin}${decodeURIComponent(requestUrl(details))}`);
  form.addEventListener("input", update);
  update();

  $(".copy", details).addEventListener("click", async (e) => {
    await navigator.clipboard.writeText(`${location.origin}${requestUrl(details)}`);
    e.target.textContent = "Скопійовано";
    setTimeout(() => (e.target.textContent = "Копіювати"), 1500);
  });

  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const url = requestUrl(details);
    result.hidden = false;
    result.innerHTML = '<p class="muted">Надсилаю…</p>';
    const started = performance.now();
    try {
      const res = await fetch(url, { cache: "no-store" });
      const ms = Math.round(performance.now() - started);
      const type = res.headers.get("Content-Type") ?? "";
      const kind = res.ok ? "ok" : res.status < 400 ? "info" : "err";
      const head = `<div class="try-status"><span class="status ${kind}">${res.status}</span><span class="muted">${ms} мс · ${esc(type.split(";")[0])}</span></div>`;
      if (type.startsWith("image/")) {
        const blob = await res.blob();
        result.innerHTML = `${head}<img class="try-img" alt="" src="${URL.createObjectURL(blob)}">`;
        return;
      }
      const text = await res.text();
      let pretty = text;
      try {
        pretty = JSON.stringify(JSON.parse(text), null, 2);
      } catch {}
      const lines = pretty.split("\n");
      const cut = lines.length > TRUNCATE;
      result.innerHTML = `${head}<pre><code>${esc(cut ? lines.slice(0, TRUNCATE).join("\n") : pretty)}</code></pre>${
        cut ? `<button type="button" class="btn btn-text more">Показати всі ${lines.length} рядків</button>` : ""
      }`;
      $(".more", result)?.addEventListener("click", (ev) => {
        $("pre code", result).textContent = pretty;
        ev.target.remove();
      });
    } catch (err) {
      result.innerHTML = `<p class="error">Не вдалося: ${esc(err.message)}</p>`;
    }
  });
}

$("#base-url").textContent = `${location.origin}/v1`;
// On a phone the contents collapse above the page instead of sitting in a sidebar.
if (matchMedia("(max-width: 900px)").matches) $(".toc").open = false;

fetch("/openapi.json")
  .then((r) => r.json())
  .then((s) => {
    spec = s;
    render();
  })
  .catch(() => ($("#reference").innerHTML = '<p class="error">Не вдалося завантажити опис API. Спробуй оновити сторінку.</p>'));
