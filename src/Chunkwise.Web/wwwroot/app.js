// Chunkwise front end: plain ES modules, no framework, no build step.
// All document and chunk text is inserted with textContent, never innerHTML, because the
// corpus is data and should never be able to inject markup.

const $ = (id) => document.getElementById(id);

/** Tiny DOM helper: el("div", { class: "x", onclick: fn }, "text", childNode) */
function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === undefined || value === null || value === false) continue;
    if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else if (key === "style" && typeof value === "object") Object.assign(node.style, value);
    else if (key === "dataset") Object.assign(node.dataset, value);
    else node.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children.flat()) {
    if (child === undefined || child === null || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

const SVG_NS = "http://www.w3.org/2000/svg";
function svg(tag, attrs = {}, ...children) {
  const node = document.createElementNS(SVG_NS, tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value);
  }
  for (const child of children) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  return node;
}

function debounce(fn, ms) {
  let timer;
  return (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
}

async function api(path, body) {
  const response = await fetch(`/api${path}`, body === undefined ? {} : {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.detail || problem.title || `${response.status} ${response.statusText}`);
  }
  return response.json();
}

const fmt2 = (x) => x.toFixed(2);
const fmt0 = (x) => Math.round(x).toLocaleString();

// The golden angle spreads hues so neighbouring chunks never get similar colours,
// however many chunks there are.
const hueOf = (index) => Math.round((index * 137.508) % 360);

// ------------------------------------------------------------------------------------------------
// State
// ------------------------------------------------------------------------------------------------

const state = {
  config: null,
  docs: new Map(),               // id -> { id, title, text, sections, atomicBlocks }
  tab: "visualize",
  documentId: null,
  strategy: "markdown",
  compareLeft: "fixed",
  compareRight: "markdown",
  metric: "recallAt3",
  lastQuestion: null,
  goldenId: null,
  leaderboard: null,
  requestSeq: 0,                 // drops out-of-order responses while sliders are dragged
};

const settings = () => ({
  chunkSize: Number($("size").value),
  chunkOverlap: Number($("overlap").value),
});

async function getDocument(id) {
  if (!state.docs.has(id)) state.docs.set(id, await api(`/documents/${encodeURIComponent(id)}`));
  return state.docs.get(id);
}

const strategyName = (name) => state.config.strategies.find((s) => s.name === name)?.displayName ?? name;

// ------------------------------------------------------------------------------------------------
// Tooltip
// ------------------------------------------------------------------------------------------------

const tooltip = $("tooltip");
function showTooltip(event, ...content) {
  tooltip.replaceChildren(...content);
  tooltip.classList.remove("hidden");
  const pad = 14;
  const { innerWidth: w, innerHeight: h } = window;
  const rect = tooltip.getBoundingClientRect();
  let x = event.clientX + pad, y = event.clientY + pad;
  if (x + rect.width > w - 8) x = event.clientX - rect.width - pad;
  if (y + rect.height > h - 8) y = event.clientY - rect.height - pad;
  tooltip.style.left = `${x}px`;
  tooltip.style.top = `${y}px`;
}
const hideTooltip = () => tooltip.classList.add("hidden");

// ------------------------------------------------------------------------------------------------
// Chunk rendering: the core visual
// ------------------------------------------------------------------------------------------------

/**
 * Renders `text` split at every chunk boundary. Each piece ("segment") knows which chunks cover it,
 * so overlaps (covered by 2+ chunks) can be shaded and hovering one chunk can light up all of its
 * segments. Chunks are offsets into the original text; that is what makes this a simple sweep.
 */
function renderChunks(container, doc, chunks, { strip } = {}) {
  const text = doc.text;
  const points = new Set([0, text.length]);
  for (const c of chunks) { points.add(c.start); points.add(c.end); }
  const cuts = [...points].sort((a, b) => a - b);
  const startsAt = new Set(chunks.map((c) => c.start));

  const segmentsByChunk = new Map(chunks.map((c) => [c.index, []]));
  const fragment = document.createDocumentFragment();
  let active = 0; // chunks are sorted by start, so we can skip ones that ended long ago

  for (let i = 0; i < cuts.length - 1; i++) {
    const s = cuts[i], e = cuts[i + 1];
    if (e <= s) continue;
    while (active < chunks.length && chunks[active].end <= s) active++;
    const covering = [];
    for (let j = active; j < chunks.length && chunks[j].start <= s; j++)
      if (chunks[j].end >= e) covering.push(chunks[j]);

    const span = document.createElement("span");
    span.textContent = text.slice(s, e);
    if (covering.length > 0) {
      const owner = covering[covering.length - 1];
      span.className = "seg chunk" + (covering.length > 1 ? " overlap" : "") + (startsAt.has(s) ? " boundary-start" : "");
      span.style.setProperty("--h", hueOf(owner.index));
      span.dataset.chunks = covering.map((c) => c.index).join(",");
      for (const c of covering) segmentsByChunk.get(c.index).push(span);
    }
    fragment.append(span);
  }

  container.classList.toggle("mono", doc.atomicBlocks.length > 0); // tables and code need a monospace font
  container.replaceChildren(fragment);

  const byIndex = new Map(chunks.map((c) => [c.index, c]));
  let highlighted = [];
  const highlight = (indices) => {
    for (const s of highlighted) s.classList.remove("is-hover");
    highlighted = indices.flatMap((i) => segmentsByChunk.get(i) ?? []);
    for (const s of highlighted) s.classList.add("is-hover");
    if (strip) for (const bar of strip.children) bar.classList.toggle("is-active", indices.includes(Number(bar.dataset.index)));
  };

  container.onmousemove = (event) => {
    const seg = event.target.closest(".seg[data-chunks]");
    if (!seg) { highlight([]); hideTooltip(); return; }
    const indices = seg.dataset.chunks.split(",").map(Number);
    highlight([indices[indices.length - 1]]);
    showTooltip(event, ...indices.map((i) => chunkTooltip(doc, byIndex.get(i), indices.length > 1)));
  };
  container.onmouseleave = () => { highlight([]); hideTooltip(); };

  if (strip) renderSizeStrip(strip, chunks, container, segmentsByChunk, highlight, doc);
}

function chunkTooltip(doc, chunk, isOverlap) {
  return el("div", { style: { marginBottom: "6px" } },
    el("div", { class: "row" },
      el("span", { class: "linekey", style: { background: `hsl(${hueOf(chunk.index)} 70% 50%)` } }),
      el("strong", {}, `Chunk #${chunk.index}`),
      isOverlap ? el("span", { class: "tag" }, "overlap") : null),
    el("div", {}, chunk.sectionPath || doc.title),
    el("div", { class: "mono" }, `chars ${chunk.start}–${chunk.end} · ${chunk.length} long`),
    chunk.parentStart != null ? el("div", { class: "mono" }, `returns parent ${chunk.parentStart}–${chunk.parentEnd} (${chunk.parentEnd - chunk.parentStart} long)`) : null);
}

function renderSizeStrip(strip, chunks, container, segmentsByChunk, highlight, doc) {
  const max = Math.max(...chunks.map((c) => c.length), 1);
  strip.replaceChildren(...chunks.map((c) => el("span", {
    dataset: { index: c.index },
    style: { height: `${Math.max(6, (c.length / max) * 100)}%`, background: `hsl(${hueOf(c.index)} 65% 55%)` },
    onmouseenter: (event) => { highlight([c.index]); showTooltip(event, chunkTooltip(doc, c, false)); },
    onmouseleave: () => { highlight([]); hideTooltip(); },
    onclick: () => segmentsByChunk.get(c.index)?.[0]?.scrollIntoView({ block: "center", behavior: "smooth" }),
  })));
}

/** Generic renderer for the Ask source viewer: shade arbitrary [start, end) ranges with classes. */
function renderRanges(container, doc, ranges) {
  const text = doc.text;
  const points = new Set([0, text.length]);
  for (const r of ranges) { points.add(r.start); points.add(r.end); }
  const cuts = [...points].sort((a, b) => a - b);
  const fragment = document.createDocumentFragment();
  let first = null;
  for (let i = 0; i < cuts.length - 1; i++) {
    const s = cuts[i], e = cuts[i + 1];
    const classes = ranges.filter((r) => r.start <= s && r.end >= e).map((r) => r.cls);
    const span = el("span", { class: classes.length ? "seg " + classes.join(" ") : null }, text.slice(s, e));
    if (classes.includes("retrieved") && !first) first = span;
    fragment.append(span);
  }
  container.classList.toggle("mono", doc.atomicBlocks.length > 0);
  container.replaceChildren(fragment);
  first?.scrollIntoView({ block: "center" });
}

function statTiles(stats) {
  return [
    ["Chunks", fmt0(stats.count)],
    ["Average length", `${fmt0(stats.average)} chars`],
    ["Shortest", `${fmt0(stats.min)} chars`],
    ["Longest", `${fmt0(stats.max)} chars`],
  ].map(([label, value]) => el("div", { class: "stat" }, el("div", { class: "label" }, label), el("div", { class: "value" }, value)));
}

// ------------------------------------------------------------------------------------------------
// Views
// ------------------------------------------------------------------------------------------------

async function renderVisualize() {
  const seq = ++state.requestSeq;
  const [doc, result] = await Promise.all([
    getDocument(state.documentId),
    api("/chunk", { documentId: state.documentId, strategy: state.strategy, ...settings() }),
  ]);
  if (seq !== state.requestSeq) return; // a newer slider value is already on its way

  for (const b of $("strategy-buttons").children) b.setAttribute("aria-pressed", String(b.dataset.name === state.strategy));
  $("strategy-desc").textContent = state.config.strategies.find((s) => s.name === state.strategy)?.description ?? "";
  $("viz-stats").replaceChildren(...statTiles(result.stats));
  const scroll = $("viz-doc").scrollTop;
  renderChunks($("viz-doc"), doc, result.chunks, { strip: $("size-strip") });
  $("viz-doc").scrollTop = scroll; // keep the reader's place while dragging sliders
}

async function renderCompare() {
  const seq = ++state.requestSeq;
  const [doc, left, right] = await Promise.all([
    getDocument(state.documentId),
    api("/chunk", { documentId: state.documentId, strategy: state.compareLeft, ...settings() }),
    api("/chunk", { documentId: state.documentId, strategy: state.compareRight, ...settings() }),
  ]);
  if (seq !== state.requestSeq) return;

  for (const [side, result] of [["left", left], ["right", right]]) {
    const pane = $(`cmp-${side}-doc`);
    const scroll = pane.scrollTop;
    renderChunks(pane, doc, result.chunks);
    pane.scrollTop = scroll;
    $(`cmp-${side}-stats`).textContent = `${result.stats.count} chunks · avg ${fmt0(result.stats.average)} chars`;
  }
}

// Synced scrolling: scroll one pane and the other follows proportionally.
function syncScroll(a, b) {
  let lock = false;
  const follow = (from, to) => () => {
    if (lock) return;
    lock = true;
    const ratio = from.scrollTop / Math.max(1, from.scrollHeight - from.clientHeight);
    to.scrollTop = ratio * (to.scrollHeight - to.clientHeight);
    requestAnimationFrame(() => { lock = false; });
  };
  a.addEventListener("scroll", follow(a, b));
  b.addEventListener("scroll", follow(b, a));
}

// ---------------- Ask ----------------

const golden = () => state.config.questions.find((q) => q.id === state.goldenId);

function containsAnswer(hit, q) {
  return q && hit.documentId.toLowerCase() === q.document.toLowerCase()
    && hit.retrievedStart < q.answerEnd && q.answerStart < hit.retrievedEnd;
}

async function renderAsk() {
  const question = $("question").value.trim();
  if (!question) return;
  state.lastQuestion = question;
  const q = golden()?.question === question ? golden() : null;

  const button = $("ask-btn");
  button.disabled = true;
  $("ask-results").classList.add("loading");
  try {
    const seq = ++state.requestSeq;
    const result = await api("/ask", { question, ...settings(), enrich: $("enrich").checked, topK: 3 });
    if (seq !== state.requestSeq) return;

    $("ask-results").replaceChildren(...result.strategies.map((s) => {
      const firstHit = q ? s.hits.find((h) => containsAnswer(h, q)) : null;
      return el("div", { class: "card result-card" },
        el("div", { class: "card-head" },
          el("h3", {}, s.displayName),
          q ? el("span", { class: `badge ${firstHit ? "good" : "miss"}` }, firstHit ? `✓ answer at #${firstHit.rank}` : "answer not in top 3") : null),
        ...s.hits.map((hit) => el("button", {
          class: "hit", type: "button",
          onclick: (event) => openSource(hit, q, s.displayName, event.currentTarget),
        },
          el("div", { class: "hit-top" },
            el("span", { class: "rank" }, `#${hit.rank}`),
            el("span", { class: "score-track", title: `cosine similarity ${hit.score.toFixed(3)}` },
              el("span", { class: "score-fill", style: { width: `${Math.max(0, hit.score) * 100}%`, display: "block" } })),
            el("span", { class: "mono" }, hit.score.toFixed(3)),
            el("span", { class: "path", title: hit.chunk.sectionPath }, `${hit.documentId} › ${hit.chunk.sectionPath || "(intro)"}`),
            q && containsAnswer(hit, q) ? el("span", { class: "badge good" }, "answer") : null),
          el("div", { class: "snippet" }, hit.text))));
    }));
  } catch (error) {
    $("ask-results").replaceChildren(el("div", { class: "empty" }, `Something went wrong: ${error.message}`));
  } finally {
    button.disabled = false;
    $("ask-results").classList.remove("loading");
  }
}

async function openSource(hit, q, strategyLabel, button) {
  for (const b of document.querySelectorAll(".hit.is-selected")) b.classList.remove("is-selected");
  button.classList.add("is-selected");

  const doc = await getDocument(hit.documentId);
  const ranges = [{ start: hit.retrievedStart, end: hit.retrievedEnd, cls: "retrieved" }];
  if (q && q.document.toLowerCase() === hit.documentId.toLowerCase())
    ranges.push({ start: q.answerStart, end: q.answerEnd, cls: "answer" });

  $("source-title").textContent = `${strategyLabel} · result #${hit.rank} in ${doc.title}`;
  $("source-viewer").classList.remove("hidden");
  renderRanges($("source-doc"), doc, ranges);
  $("source-viewer").scrollIntoView({ behavior: "smooth", block: "start" });
}

// ---------------- Leaderboard ----------------

const METRICS = [
  { key: "recallAt1", label: "Recall@1" },
  { key: "recallAt3", label: "Recall@3" },
  { key: "mrr", label: "MRR" },
  { key: "coverageAt3", label: "Coverage@3" },
];

async function renderLeaderboard() {
  const seq = ++state.requestSeq;
  $("view-leaderboard").classList.add("loading");
  try {
    const { chunkSize, chunkOverlap } = settings();
    const data = await api(`/leaderboard?chunkSize=${chunkSize}&chunkOverlap=${chunkOverlap}`);
    if (seq !== state.requestSeq) return;
    state.leaderboard = data;
    const timing = data.elapsedMs < 5 ? "cached" : `computed in ${data.elapsedMs} ms`;
    $("lb-meta").textContent = `${data.embedder} · size ${data.options.chunkSize} · overlap ${data.options.chunkOverlap} · ${timing}`;
    drawChart();
    drawTable();
    drawHeatmap();
  } finally {
    $("view-leaderboard").classList.remove("loading");
  }
}

/** Groups the flat report list into one entry per strategy: { strategy, label, plain, context }. */
function groupedReports() {
  const groups = new Map();
  for (const r of state.leaderboard.reports) {
    if (!groups.has(r.strategy)) groups.set(r.strategy, { strategy: r.strategy, label: strategyName(r.strategy) });
    groups.get(r.strategy)[r.enriched ? "context" : "plain"] = r;
  }
  return [...groups.values()];
}

/**
 * Horizontal grouped bars: one band per strategy, two bars (plain / + context) per band.
 * Bars are 12px thick with a 2px gap, rounded only at the data end, values at the tip,
 * with hairline gridlines. One metric at a time: these are all 0-1 scores, so they share one axis.
 */
function drawChart() {
  const metric = METRICS.find((m) => m.key === state.metric);
  $("chart-title").textContent = `${metric.label} by strategy`;
  for (const b of $("metric-buttons").children) b.setAttribute("aria-pressed", String(b.dataset.key === state.metric));

  const groups = groupedReports();
  // The SVG scales to its card; a narrower coordinate space keeps labels legible at ~550px.
  const width = 540, labelW = 150, rightPad = 40, top = 8, bottom = 26;
  const barH = 12, gap = 2, bandPad = 16;
  const bandH = barH * 2 + gap + bandPad;
  const height = top + groups.length * bandH + bottom;
  const plotW = width - labelW - rightPad;
  const x = (v) => labelW + v * plotW;

  const root = svg("svg", { viewBox: `0 0 ${width} ${height}`, role: "img", "aria-label": `${metric.label} for each chunking strategy, with and without context` });

  for (const t of [0, 0.25, 0.5, 0.75, 1]) {
    root.append(svg("line", { class: "gridline", x1: x(t), x2: x(t), y1: top, y2: height - bottom }));
    root.append(svg("text", { class: "axis-label", x: x(t), y: height - 8, "text-anchor": "middle" }, t.toFixed(2)));
  }

  // Data end rounded (4px), baseline end square.
  const barPath = (x0, y, w, h) => {
    const r = Math.min(4, w / 2, h / 2);
    return `M${x0},${y} H${x0 + w - r} Q${x0 + w},${y} ${x0 + w},${y + r} V${y + h - r} Q${x0 + w},${y + h} ${x0 + w - r},${y + h} H${x0} Z`;
  };

  groups.forEach((g, i) => {
    const y0 = top + i * bandH + bandPad / 2;
    root.append(svg("text", { class: "cat-label", x: labelW - 10, y: y0 + barH + gap / 2 + 4, "text-anchor": "end" }, g.label));
    [["plain", "var(--series-1)", "Plain chunk"], ["context", "var(--series-2)", "+ context"]].forEach(([key, color, seriesLabel], j) => {
      const report = g[key];
      if (!report) return;
      const value = report[metric.key];
      const y = y0 + j * (barH + gap);
      const w = Math.max(1, value * plotW);
      const bar = svg("path", { class: "bar", d: barPath(x(0), y, w, barH), fill: color, tabindex: 0 });
      const tip = (event) => showTooltip(event,
        el("div", {}, el("strong", {}, fmt2(value))),
        el("div", { class: "row" }, el("span", { class: "linekey", style: { background: color } }), `${g.label} · ${seriesLabel}`),
        el("div", { class: "mono" }, `${report.chunkCount} chunks · avg ${fmt0(report.averageChunkLength)} chars`));
      bar.addEventListener("pointermove", tip);
      bar.addEventListener("focus", (e) => { const r = e.target.getBoundingClientRect(); tip({ clientX: r.right, clientY: r.top }); });
      bar.addEventListener("pointerleave", hideTooltip);
      bar.addEventListener("blur", hideTooltip);
      root.append(bar);
      root.append(svg("text", { class: "value-label", x: x(0) + w + 6, y: y + barH - 2 }, fmt2(value)));
    });
  });

  $("chart").replaceChildren(root);
}

function drawTable() {
  const reports = state.leaderboard.reports;
  const columns = [
    { key: "chunkCount", label: "Chunks", fmt: fmt0 },
    { key: "averageChunkLength", label: "Avg size", fmt: fmt0 },
    { key: "averageRetrievedLength", label: "Returned", fmt: fmt0 },
    ...METRICS.map((m) => ({ ...m, fmt: fmt2, ranked: true })),
  ];
  // "Best" compares displayed (rounded) values, so ties at 0.97 are both highlighted.
  const best = Object.fromEntries(columns.filter((c) => c.ranked).map((c) => [c.key, fmt2(Math.max(...reports.map((r) => r[c.key])))]));

  const head = el("tr", {}, el("th", {}, "Strategy"), ...columns.map((c) => el("th", {
    title: c.ranked ? `Click to chart ${c.label}` : null,
    onclick: c.ranked ? () => { state.metric = c.key; drawChart(); } : null,
  }, c.label)));

  const rows = reports.map((r) => el("tr", {},
    el("td", {},
      el("span", { class: "series-dot", style: { background: r.enriched ? "var(--series-2)" : "var(--series-1)" } }),
      strategyName(r.strategy), r.enriched ? el("span", { class: "ctx" }, " + context") : null),
    ...columns.map((c) => el("td", { class: c.ranked && c.fmt(r[c.key]) === best[c.key] ? "best" : null }, c.fmt(r[c.key])))));

  $("metrics-table").replaceChildren(el("thead", {}, head), el("tbody", {}, rows));
}

function drawHeatmap() {
  const reports = state.leaderboard.reports;
  const questions = state.config.questions;
  const rankClass = (rank) => rank == null ? "rmiss" : rank === 1 ? "r1" : rank === 2 ? "r2" : rank === 3 ? "r3" : "r4";

  const header = el("tr", {}, el("th", {}, ""),
    ...reports.map((r) => el("th", { class: "col", title: r.label }, `${strategyName(r.strategy)}${r.enriched ? " +ctx" : ""}`)));

  const rows = questions.map((q) => el("tr", {},
    el("td", { class: "q", title: q.question }, el("b", {}, q.id), q.question),
    ...reports.map((r) => {
      const result = r.questions.find((x) => x.id === q.id);
      const rank = result?.rank ?? null;
      return el("td", {
        class: `cell ${rankClass(rank)}`,
        onpointermove: (event) => showTooltip(event,
          el("div", {}, el("strong", {}, rank == null ? "not found in top 10" : `rank ${rank}`)),
          el("div", {}, `${strategyName(r.strategy)}${r.enriched ? " + context" : ""}`),
          el("div", {}, q.question),
          el("div", { class: "mono" }, `coverage@3 ${fmt2(result?.coverage ?? 0)}`),
          q.tags.length ? el("div", {}, q.tags.map((t) => el("span", { class: "tag" }, t))) : null),
        onpointerleave: hideTooltip,
      }, rank ?? "–");
    })));

  $("heatmap").replaceChildren(el("table", { class: "heatmap" }, el("thead", {}, header), el("tbody", {}, rows)));
}

// ------------------------------------------------------------------------------------------------
// Wiring
// ------------------------------------------------------------------------------------------------

const views = { visualize: renderVisualize, compare: renderCompare, ask: () => state.lastQuestion && renderAsk(), leaderboard: renderLeaderboard };

function refresh() {
  Promise.resolve(views[state.tab]()).catch((error) => console.error(error));
}

function selectTab(tab) {
  if (!views[tab]) tab = "visualize";
  state.tab = tab;
  for (const b of document.querySelectorAll(".tab")) b.setAttribute("aria-selected", String(b.dataset.tab === tab));
  for (const v of document.querySelectorAll(".view")) v.classList.toggle("hidden", v.id !== `view-${tab}`);
  for (const c of document.querySelectorAll("[data-views]")) c.classList.toggle("hidden", !c.dataset.views.split(" ").includes(tab));
  if (parseHash().tab !== tab) history.replaceState(null, "", `#${tab}`);
  refresh();
}

function syncSliderLabels() {
  const size = $("size"), overlap = $("overlap");
  // Overlap must stay below half the chunk size, or chunks would mostly repeat each other.
  overlap.max = Math.min(300, Math.floor(Number(size.value) / 2 / 10) * 10);
  if (Number(overlap.value) > Number(overlap.max)) overlap.value = overlap.max;
  $("size-out").textContent = size.value;
  $("overlap-out").textContent = overlap.value;
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  localStorage.setItem("chunkwise-theme", theme);
}

async function init() {
  state.config = await api("/config");
  const { config } = state;

  $("embedder-name").textContent = config.embedder;
  if (config.embedderWarning) {
    $("embedder-warning").textContent = config.embedderWarning;
    $("embedder-warning").classList.remove("hidden");
  }

  $("size").value = config.defaults.chunkSize;
  $("overlap").value = config.defaults.chunkOverlap;
  syncSliderLabels();

  state.documentId = config.documents[0].id;
  $("doc-select").replaceChildren(...config.documents.map((d) => el("option", { value: d.id }, `${d.title} (${d.id})`)));

  $("strategy-buttons").replaceChildren(...config.strategies.map((s) => el("button", {
    type: "button", dataset: { name: s.name }, title: s.description, "aria-pressed": "false",
    onclick: () => { state.strategy = s.name; renderVisualize(); },
  }, s.displayName)));

  for (const side of ["left", "right"]) {
    const select = $(`cmp-${side}`);
    select.replaceChildren(...config.strategies.map((s) => el("option", { value: s.name }, s.displayName)));
    select.value = side === "left" ? state.compareLeft : state.compareRight;
    select.addEventListener("change", () => {
      state[side === "left" ? "compareLeft" : "compareRight"] = select.value;
      renderCompare();
    });
  }
  syncScroll($("cmp-left-doc"), $("cmp-right-doc"));

  $("golden-select").append(...config.questions.map((q) => el("option", { value: q.id }, `${q.id} · ${q.question}`)));
  $("golden-select").addEventListener("change", () => {
    state.goldenId = $("golden-select").value || null;
    const q = golden();
    $("golden-tags").replaceChildren(...(q?.tags ?? []).map((t) => el("span", { class: "tag" }, t)));
    $("golden-note").textContent = q?.note ?? "";
    $("golden-note").classList.toggle("hidden", !q?.note);
    if (q) { $("question").value = q.question; renderAsk(); }
  });

  $("metric-buttons").replaceChildren(...METRICS.map((m) => el("button", {
    type: "button", dataset: { key: m.key }, "aria-pressed": "false",
    onclick: () => { state.metric = m.key; drawChart(); },
  }, m.label)));

  $("ask-form").addEventListener("submit", (event) => { event.preventDefault(); renderAsk(); });
  $("enrich").addEventListener("change", () => state.lastQuestion && renderAsk());
  $("doc-select").addEventListener("change", () => { state.documentId = $("doc-select").value; refresh(); });

  // Sliders re-chunk live; the leaderboard is heavier, so it waits until the slider settles.
  const live = debounce(refresh, 120);
  const settled = debounce(refresh, 400);
  for (const id of ["size", "overlap"]) {
    $(id).addEventListener("input", () => { syncSliderLabels(); (state.tab === "leaderboard" ? settled : live)(); });
  }

  for (const b of document.querySelectorAll(".tab")) b.addEventListener("click", () => selectTab(b.dataset.tab));
  window.addEventListener("hashchange", () => selectTab(parseHash().tab));

  $("theme-toggle").addEventListener("click", () => {
    const current = document.documentElement.dataset.theme
      ?? (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    applyTheme(current === "dark" ? "light" : "dark");
  });

  const { tab, params } = parseHash();
  applyDeepLink(params);
  selectTab(tab || "visualize");
}

/** "#ask?q=hb-02&size=300" -> { tab: "ask", params: URLSearchParams } */
function parseHash() {
  const [tab, query = ""] = location.hash.slice(1).split("?");
  return { tab, params: new URLSearchParams(query) };
}

/**
 * Deep links make the demo scriptable: a URL like
 * #visualize?doc=employee-handbook.md&strategy=fixed&size=300 opens straight on a talking point,
 * and README screenshots can be regenerated by a headless browser.
 */
function applyDeepLink(params) {
  const has = (k) => params.has(k);
  if (has("theme")) applyTheme(params.get("theme") === "dark" ? "dark" : "light");
  if (has("size")) $("size").value = params.get("size");
  if (has("overlap")) $("overlap").value = params.get("overlap");
  syncSliderLabels();
  if (has("doc") && state.config.documents.some((d) => d.id === params.get("doc"))) {
    state.documentId = params.get("doc");
    $("doc-select").value = state.documentId;
  }
  const strategies = state.config.strategies.map((s) => s.name);
  if (strategies.includes(params.get("strategy"))) state.strategy = params.get("strategy");
  if (strategies.includes(params.get("left"))) $("cmp-left").value = state.compareLeft = params.get("left");
  if (strategies.includes(params.get("right"))) $("cmp-right").value = state.compareRight = params.get("right");
  if (has("enrich")) $("enrich").checked = params.get("enrich") === "1";
  if (has("metric") && METRICS.some((m) => m.key === params.get("metric"))) state.metric = params.get("metric");

  const q = state.config.questions.find((x) => x.id === params.get("q"));
  if (q) {
    $("golden-select").value = q.id;
    $("golden-select").dispatchEvent(new Event("change"));
  } else if (has("question")) {
    $("question").value = params.get("question");
    state.lastQuestion = params.get("question");
  }
}

init().catch((error) => {
  document.querySelector("main").prepend(el("div", { class: "banner" }, `Could not start: ${error.message}`));
});
