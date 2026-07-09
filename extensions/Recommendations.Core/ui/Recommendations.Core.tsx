import React from "react";
// Native host components (shared via the @cove/runtime import map → same React instance + theme).
import {
  VideoCard, ImageTile, PerformerTile, InteractiveRating, VideoPlayer,
  ListPage, VIDEO_CRITERIA, VIDEO_SORT_OPTIONS, useListUrlState, RelatedEntityListView,
} from "@cove/runtime/components";

const { useState, useEffect, useCallback, useRef, useMemo } = React;

const API_BASE = "/api/ext/recommendations";
const HOST_API = "/api";

const ENTITY_TYPES = ["video", "image"] as const;
const PLURAL: Record<string, string> = {
  video: "videos", image: "images", audio: "audios", text: "texts",
  performer: "performers", gallery: "galleries", studio: "studios", group: "groups", tag: "tags",
};

async function api(path: string, options: RequestInit = {}): Promise<any> {
  const response = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
  });
  const text = await response.text();
  let data: any = null;
  if (text) { try { data = JSON.parse(text); } catch { data = text; } }
  if (!response.ok) {
    const detail = (data && (data.detail || data.message || data.error)) || (typeof data === "string" ? data : response.statusText);
    throw new Error(detail || "Request failed.");
  }
  return data;
}

// Hydrate bare (type,id) refs into full host entity objects in ONE batched request (GET /api/{plural}?ids=…),
// so every card gets complete data at once and renders like the rest of Cove (no per-card "fill-in later").
// Throws on failure — we surface that rather than silently degrading.
async function hydrate(entityType: string, ids: number[]): Promise<Record<number, any>> {
  const plural = PLURAL[entityType];
  if (!plural || ids.length === 0) return {};
  const res = await fetch(`${HOST_API}/${plural}?ids=${ids.join(",")}&perPage=${ids.length}`);
  if (!res.ok) throw new Error(`Failed to load ${plural} (HTTP ${res.status}).`);
  const data = await res.json();
  const items = Array.isArray(data) ? data : (data.items || []);
  const map: Record<number, any> = {};
  for (const item of items) if (item && item.id != null) map[item.id] = item;
  return map;
}

// Normalize feed (ItemScore) and inspector (ScoredEntity) shapes to one row shape.
function normalizeRow(r: any) {
  const entityType = r.entity?.entityType ?? r.entityType;
  const entityId = r.entity?.entityId ?? r.entityId;
  return { entityType, entityId, score: r.score, confidence: r.confidence, why: r.why };
}

// ── Score / confidence / why presentation ───────────────────────────────────

function scoreColor(score: number) {
  if (score >= 0.25) return "bg-emerald-500/90";
  if (score > -0.25) return "bg-slate-500/90";
  return "bg-rose-500/90";
}

function ScoreBadge({ score, confidence }: { score: number; confidence: number }) {
  return (
    <div className="pointer-events-none absolute left-1.5 top-1.5 z-10 flex items-center gap-1 rounded-md bg-black/60 px-1.5 py-0.5 text-[11px] font-semibold text-white backdrop-blur-sm">
      <span className={`inline-block h-2 w-2 rounded-full ${scoreColor(score ?? 0)}`} />
      <span className="tabular-nums">{(score ?? 0).toFixed(2)}</span>
      <span className="font-normal text-white/70">·{Math.round((confidence ?? 0) * 100)}%</span>
    </div>
  );
}

function WhyFactors({ why }: { why: any }) {
  if (!why || !why.factors || why.factors.length === 0) {
    return <p className="text-xs text-muted-foreground">{why?.summary || "No engagement signals."}</p>;
  }
  return (
    <div className="space-y-1">
      <p className="text-xs font-medium text-foreground">{why.summary}</p>
      <table className="w-full text-xs">
        <tbody>
          {why.factors.map((f: any, i: number) => {
            const isSub = typeof f.label === "string" && f.label.trim().startsWith("↳");
            return (
              <tr key={i} className="text-muted-foreground">
                <td className={`py-0.5 pr-2 whitespace-nowrap ${isSub ? "pl-2 text-muted-foreground/80" : "font-medium text-foreground"}`}>{f.label}</td>
                <td className={`py-0.5 pr-2 ${isSub ? "text-muted-foreground/60" : "text-muted-foreground"}`}>{f.detail}</td>
                <td className={`py-0.5 text-right tabular-nums ${isSub ? "opacity-70" : ""} ${f.contribution >= 0 ? "text-emerald-400" : "text-rose-400"}`}>
                  {f.contribution >= 0 ? "+" : ""}{(f.contribution ?? 0).toFixed(3)}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function ResultCard({ row, entity, onNavigate, expandAll, showScores = true }: { row: any; entity: any; onNavigate?: (t: any) => void; expandAll: boolean; showScores?: boolean }) {
  const [open, setOpen] = useState(false);
  useEffect(() => { setOpen(expandAll); }, [expandAll]);
  // VideoCard.onClick (and its hover performer/tag badges' onNavigate) preventDefault the card's own
  // href, so if onNavigate is absent (e.g. on a detail tab) we must navigate ourselves — otherwise
  // clicks do nothing. Use this for both the card click AND the cards' onNavigate (the hover badges).
  const nav = (t: any) => (onNavigate ? onNavigate(t) : window.location.assign(`/${t.page}${t.id != null ? "/" + t.id : ""}`));
  const open_ = () => nav({ page: row.entityType, id: row.entityId });

  // Cove's standard entity cards (same components the native pages + RelatedEntityListView use) — proper preview,
  // hover badges, engagement. If the entity didn't hydrate (or an unsupported type sneaks in), skip it entirely
  // rather than render a bespoke placeholder.
  let card: any;
  if (entity && row.entityType === "video") {
    card = <VideoCard video={entity} onClick={open_} onNavigate={nav} />;
  } else if (entity && row.entityType === "image") {
    card = <ImageTile image={entity} onClick={open_} onNavigate={nav} />;
  } else if (entity && row.entityType === "performer") {
    card = <PerformerTile performer={entity} onClick={open_} onNavigate={nav} />;
  } else {
    return null;
  }

  return (
    <div className="relative flex flex-col gap-1">
      <div className="relative">
        {showScores && row.score != null && <ScoreBadge score={row.score} confidence={row.confidence} />}
        {card}
        {row.why && (
          <button
            type="button"
            onClick={() => setOpen((o) => !o)}
            className="absolute bottom-1.5 right-1.5 z-10 rounded-md bg-black/60 px-1.5 py-0.5 text-[11px] font-medium text-white backdrop-blur-sm hover:bg-black/80"
          >
            {open ? "Hide" : "Why"}
          </button>
        )}
      </div>
      {open && row.why && <div className="rounded-md border border-border bg-surface px-2.5 py-2"><WhyFactors why={row.why} /></div>}
    </div>
  );
}

function ResultsGrid({ rows, onNavigate, displayMode = "grid", entityType = "video", showScores = true }: { rows: any[]; onNavigate?: (t: any) => void; displayMode?: string; entityType?: string; showScores?: boolean }) {
  const [entities, setEntities] = useState<Record<string, any>>({});
  const [hydrating, setHydrating] = useState(false);
  const [hydrateError, setHydrateError] = useState("");
  const [expandAll, setExpandAll] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setEntities({}); setHydrateError("");
    if (rows.length === 0) return;
    setHydrating(true);
    (async () => {
      try {
        const byType: Record<string, number[]> = {};
        for (const r of rows) (byType[r.entityType] ||= []).push(r.entityId);
        const result: Record<string, any> = {};
        await Promise.all(Object.entries(byType).map(async ([type, ids]) => {
          const map = await hydrate(type, ids);
          for (const id of ids) if (map[id]) result[`${type}:${id}`] = map[id];
        }));
        if (!cancelled) { setEntities(result); setHydrating(false); }
      } catch (e: any) {
        if (!cancelled) { setHydrateError(e?.message || "Failed to load card data."); setHydrating(false); }
      }
    })();
    return () => { cancelled = true; };
  }, [rows]);

  if (rows.length === 0) {
    return <p className="rounded-md border border-dashed border-border bg-muted/20 p-4 text-sm text-muted-foreground">Nothing to show yet.</p>;
  }

  if (hydrateError) {
    return <p className="rounded-md border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{hydrateError}</p>;
  }

  const ready = !hydrating && Object.keys(entities).length > 0;
  const nav = (t: any) => (onNavigate ? onNavigate(t) : window.location.assign(`/${t.page}${t.id != null ? "/" + t.id : ""}`));

  // Non-grid modes reuse Cove's own multi-mode renderer (list / feed / vertical) so they look and behave exactly
  // like the native list pages. The per-card score badge + "Why" live only in the grid mode below (Cove's
  // renderer owns its item markup); ranking order still conveys the scoring, and reverse-sort still works.
  if (displayMode !== "grid") {
    if (!ready) return <p className="text-sm text-muted-foreground">Loading…</p>;
    const items = rows.map((r) => entities[`${r.entityType}:${r.entityId}`]).filter(Boolean);
    const plural = PLURAL[entityType] || "videos";
    return <RelatedEntityListView entityType={plural as any} items={items as any} displayMode={displayMode as any} onNavigate={nav} />;
  }

  return (
    <div className="space-y-2">
      {rows.some((r) => r.why) && (
        <div className="flex justify-end">
          <button
            type="button"
            onClick={() => setExpandAll((v) => !v)}
            className="rounded-md border border-border bg-surface px-3 py-1.5 text-xs text-muted-foreground hover:bg-card-hover"
          >
            {expandAll ? "Collapse all why" : "Expand all why"}
          </button>
        </div>
      )}
      <div className="grid gap-3" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(var(--card-min-width, 220px), 1fr))" }}>
        {ready
          // Cards only render once their full data is in hand (one batched fetch) — they appear complete, the
          // way every other Cove grid behaves, instead of empty cards that fill in a moment later.
          ? rows.map((r) => (
              <ResultCard key={`${r.entityType}:${r.entityId}`} row={r} entity={entities[`${r.entityType}:${r.entityId}`]} onNavigate={onNavigate} expandAll={expandAll} showScores={showScores} />
            ))
          // Lightweight skeletons while the page hydrates.
          : rows.map((r) => (
              <div key={`${r.entityType}:${r.entityId}`} className="aspect-video animate-pulse rounded-lg border border-border bg-card/60" />
            ))}
      </div>
    </div>
  );
}

// ── Controls shared bits ────────────────────────────────────────────────────

function Labeled({ label, children }: { label: string; children: any }) {
  return <label className="flex flex-col gap-1 text-xs text-muted-foreground">{label}{children}</label>;
}
const selectCls = "rounded-md border border-border bg-input px-2 py-1.5 text-sm text-foreground";
const btnCls = "rounded-md bg-accent px-3 py-2 text-sm font-semibold text-white hover:bg-accent-hover disabled:opacity-60";

// ── The page (feed + inspector views) ───────────────────────────────────────

function RecommendedPage({ onNavigate }: { onNavigate?: (t: any) => void }) {
  const [view, setView] = useState<"feed" | "tastes" | "training" | "profile" | "inspector">("feed");
  // Shared across the feed/tastes/training/profile tabs so the chosen recommender persists when you switch tabs
  // (each tab still falls back to its own default if the shared pick isn't available there).
  const [recommender, setRecommender] = useState("");
  const labels: Record<string, string> = { feed: "Recommended", tastes: "My tastes", training: "Training", profile: "Taste profile", inspector: "Engagement inspector" };
  return (
    <div className={`recommendations-page mx-auto space-y-4 p-4 ${view === "feed" ? "max-w-none" : view === "training" ? "max-w-[1800px]" : "max-w-[1400px]"}`}>
      <div className="flex items-center justify-between gap-3">
        {/* The feed view uses ListPage's own title+count header; other views get the page heading. */}
        {view === "feed" ? <div /> : <h1 className="text-xl font-semibold text-foreground">Recommended</h1>}
        <div className="flex rounded-md border border-border bg-surface p-0.5 text-sm">
          {(["feed", "tastes", "training", "profile", "inspector"] as const).map((v) => (
            <button
              key={v}
              type="button"
              onClick={() => setView(v)}
              className={`rounded px-3 py-1 ${view === v ? "bg-accent text-white" : "text-muted-foreground hover:text-foreground"}`}
            >
              {labels[v]}
            </button>
          ))}
        </div>
      </div>
      {view === "feed" ? <FeedView onNavigate={onNavigate} recommender={recommender} onRecommender={setRecommender} />
        : view === "tastes" ? <TastesView onNavigate={onNavigate} recommender={recommender} onRecommender={setRecommender} />
        : view === "training" ? <TrainingView onNavigate={onNavigate} recommender={recommender} onRecommender={setRecommender} />
        : view === "profile" ? <ProfileView recommender={recommender} onRecommender={setRecommender} />
        : <InspectorView onNavigate={onNavigate} />}
    </div>
  );
}

// ── "Training": a focused player + rating panel, one item at a time ──

const VIDEO_ASPECTS = [
  { key: "content", label: "Content" },
  { key: "performers", label: "Performers" },
  { key: "audio", label: "Audio" },
  { key: "video_quality", label: "Video Quality" },
];
const IMAGE_ASPECTS = [
  { key: "content", label: "Content" },
  { key: "performers", label: "Performers" },
  { key: "quality", label: "Quality" },
];
const videoStreamUrl = (id: number) => `${HOST_API}/stream/video/${id}`;
const videoPosterUrl = (id: number) => `${HOST_API}/videos/${id}/image?max=1280`;
const imageUrl = (id: number) => `${HOST_API}/stream/image/${id}`;

function AspectRatingRow({ label, value, highlight, onRate }: { label: string; value: number | undefined; highlight?: boolean; onRate: (v: number) => void }) {
  return (
    <div className={`flex items-center justify-between gap-3 rounded px-2 py-1.5 ${highlight ? "bg-accent/10 ring-1 ring-accent/40" : ""}`}>
      <span className={`text-sm ${highlight ? "font-medium text-accent" : "text-muted-foreground"}`}>{label}{highlight ? "  ← learning" : ""}</span>
      <InteractiveRating value={value} onChange={onRate} />
    </div>
  );
}

function TrainingView({ onNavigate, recommender, onRecommender }: { onNavigate?: (t: any) => void; recommender: string; onRecommender: (id: string) => void }) {
  const [recommenders, setRecommenders] = useState<any[]>([]);
  const [mediaType, setMediaType] = useState<"video" | "image">("video");
  const [probes, setProbes] = useState<any[]>([]);
  const [summary, setSummary] = useState("");
  const [entities, setEntities] = useState<Record<string, any>>({});
  const [idx, setIdx] = useState(0);
  const [ratings, setRatings] = useState<Record<string, Record<string, number>>>({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [saveErr, setSaveErr] = useState("");
  const [videoDetail, setVideoDetail] = useState<any>(null);

  useEffect(() => {
    (async () => {
      try {
        const list = ((await api("/recommenders")) || []).filter((r: any) => (r.targetEntityTypes || []).includes("video"));
        setRecommenders(list);
        if (!recommender || !list.some((r: any) => r.id === recommender))
          onRecommender(list.find((r: any) => r.id.includes("deep"))?.id || list[0]?.id || "");
      } catch (e: any) { setError(e.message || "Failed to load recommenders."); }
    })();
  }, []);

  const load = useCallback(async () => {
    if (!recommender) return;
    setLoading(true); setError(""); setProbes([]); setEntities({}); setIdx(0); setRatings({});
    try {
      const res = await api(`/training?recommender=${encodeURIComponent(recommender)}&mediaType=${mediaType}&limit=24`);
      const list = res?.probes || [];
      setProbes(list);
      setSummary(res?.summary || "");
      const ids = Array.from(new Set(list.map((p: any) => p.candidate?.entityId).filter((x: any) => x != null))) as number[];
      const map = await hydrate(mediaType, ids);
      setEntities(Object.fromEntries(Object.entries(map).map(([id, v]) => [`${mediaType}:${id}`, v])));
    } catch (e: any) { setError(e.message || "Failed to load probes."); }
    finally { setLoading(false); }
  }, [recommender, mediaType]);

  useEffect(() => { if (recommender) load(); }, [recommender, mediaType]);

  const probe = probes[idx];
  const et: string = probe?.candidate?.entityType || mediaType;
  const id: number | undefined = probe?.candidate?.entityId;
  const entity = id != null ? entities[`${et}:${id}`] : null;
  const ratingKey = id != null ? `${et}:${id}` : "";
  const current = ratingKey ? (ratings[ratingKey] || {}) : {};
  const aspects = et === "image" ? IMAGE_ASPECTS : VIDEO_ASPECTS;
  const atEnd = idx >= probes.length - 1;

  // The standard VideoPlayer needs the file's format/duration/audioCodec — fetch the current item's full DTO
  // (one at a time, only for video mode). Resets immediately on item change so we never show a stale player.
  useEffect(() => {
    setVideoDetail(null);
    if (et !== "video" || id == null) return;
    let cancelled = false;
    (async () => {
      try { const res = await fetch(`${HOST_API}/videos/${id}`); if (res.ok && !cancelled) setVideoDetail(await res.json()); } catch { /* ignore */ }
    })();
    return () => { cancelled = true; };
  }, [et, id]);
  const videoFile = videoDetail?.files?.[0];

  const rate = useCallback(async (aspect: string, value: number) => {
    if (id == null) return;
    setRatings((r) => ({ ...r, [ratingKey]: { ...(r[ratingKey] || {}), [aspect]: value } }));
    setSaveErr("");
    try {
      const res = await fetch(`${HOST_API}/engagement/${et}/${id}/rating`, {
        method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ value, aspect }),
      });
      if (!res.ok) throw new Error(`Rating failed (${res.status}).`);
    } catch (e: any) { setSaveErr(e?.message || "Couldn't save the rating."); }
  }, [id, et, ratingKey]);

  const go = useCallback((delta: number) => {
    setIdx((i) => Math.min(Math.max(0, i + delta), Math.max(0, probes.length - 1)));
  }, [probes.length]);

  // Arrow keys for fast triage (← prev, → next) — but NOT while the player/inputs are focused, so the
  // video's own seek keys still work. Click anywhere off the controls to use arrow navigation.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const tag = (document.activeElement?.tagName || "").toUpperCase();
      if (tag === "VIDEO" || tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || tag === "BUTTON") return;
      if (e.key === "ArrowRight") { e.preventDefault(); go(1); }
      else if (e.key === "ArrowLeft") { e.preventDefault(); go(-1); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [go]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <Labeled label="Recommender">
          <select className={selectCls} value={recommender} onChange={(e) => onRecommender(e.target.value)}>
            {recommenders.map((r) => <option key={r.id} value={r.id}>{r.label}</option>)}
          </select>
        </Labeled>
        <Labeled label="Media">
          <div className="flex rounded-md border border-border bg-surface p-0.5 text-sm">
            {(["video", "image"] as const).map((m) => (
              <button key={m} type="button" onClick={() => setMediaType(m)}
                className={`rounded px-3 py-1 capitalize ${mediaType === m ? "bg-accent text-white" : "text-muted-foreground hover:text-foreground"}`}>
                {m === "image" ? "Images" : "Videos"}
              </button>
            ))}
          </div>
        </Labeled>
        <button type="button" className={btnCls} onClick={load} disabled={loading}>{loading ? "Loading…" : "New batch"}</button>
      </div>
      <p className="text-sm text-muted-foreground">
        {mediaType === "image"
          ? <>Rate on-taste <strong>images</strong> — a single frame is the fastest, least-ambiguous way to pin a taste. Your image ratings feed the same model that drives video recs. </>
          : <>Watch each on-taste video and rate it. </>}
        Each item targets <em>one aspect</em> the model has a strong but unconfirmed guess about (highlighted in the breakdown) — a mix of predicted-likes and predicted-dislikes. Rating <strong>that aspect</strong> confirms or corrects the guess, which teaches the model far faster (and fixes false assumptions) than a random feed. Overall is still useful, but the highlighted aspect is the point. Use <kbd>←</kbd>/<kbd>→</kbd> or Next to move through the batch.
      </p>
      {error && <p className="rounded-md border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{error}</p>}

      {loading ? <p className="text-sm text-muted-foreground">Finding the most useful {mediaType === "image" ? "images" : "videos"} to rate…</p>
        : probes.length === 0 ? <p className="rounded-md border border-dashed border-border bg-muted/20 p-4 text-sm text-muted-foreground">{summary || "No probes right now — engage with a few more items and try again."}</p>
        : (
          <div className="flex flex-col gap-4 lg:flex-row">
            {/* Player — fills the column up to most of the viewport height */}
            <div className="lg:flex-1 min-w-0">
              {et === "image"
                ? <img key={id} src={id != null ? imageUrl(id) : undefined} alt={entity?.title || `Image #${id}`} className="w-full rounded-lg bg-black object-contain" style={{ maxHeight: "80vh" }} />
                : (id != null && videoFile)
                  // The standard Cove player (HLS/adaptive, scrubbing, resume). Tracking OFF — a training
                  // preview shouldn't count as watch-time engagement; only the explicit rating teaches the model.
                  ? <div className="overflow-hidden rounded-lg bg-black" style={{ maxHeight: "80vh" }}>
                      <VideoPlayer
                        key={id}
                        videoId={id}
                        streamUrl={videoStreamUrl(id)}
                        posterUrl={videoPosterUrl(id)}
                        format={videoFile.format}
                        duration={videoFile.duration}
                        audioCodec={videoFile.audioCodec}
                        captions={videoFile.captions}
                        trackingEnabled={false}
                        videoStyle={{ maxHeight: "80vh" }}
                      />
                    </div>
                  // Still fetching the item's file details — a brief loading state, not a fallback player.
                  : <div className="aspect-video w-full animate-pulse rounded-lg bg-card/60" style={{ maxHeight: "80vh" }} />}
              <div className="mt-2 flex items-center justify-between gap-2">
                <div className="min-w-0 truncate text-sm text-foreground/90" title={entity?.title || `${et} #${id}`}>{entity?.title || `${et === "image" ? "Image" : "Video"} #${id}`}</div>
                <button type="button" onClick={() => onNavigate ? onNavigate({ page: et, id }) : window.location.assign(`/${et}/${id}`)} className="shrink-0 text-xs text-muted-foreground hover:text-foreground">Open ↗</button>
              </div>
            </div>

            {/* Rating panel */}
            <div className="lg:w-96 shrink-0 space-y-3 rounded-lg border border-border bg-surface/40 p-3">
              <div className="flex items-center justify-between gap-2">
                <span className="text-xs text-muted-foreground">{idx + 1} / {probes.length}</span>
                <span className="rounded bg-accent/15 px-1.5 py-0.5 text-xs font-medium text-accent" title="The aspect the model most wants you to confirm on this item">
                  Rate: {(aspects.find((a) => a.key === probe.aspect)?.label) || probe.attributeType} {probe.attributeName ? `· ${probe.attributeName}` : ""}
                </span>
              </div>
              {probe.rationale ? (
                <p className="text-[11px] text-muted-foreground/80">{probe.rationale}</p>
              ) : null}

              <div className="rounded-md border border-border bg-card/60 p-2">
                <div className="mb-1 flex items-center justify-between">
                  <span className="text-sm font-semibold text-foreground">Overall</span>
                  <InteractiveRating value={current["overall"]} onChange={(v: number) => rate("overall", v)} />
                </div>
                <p className="text-[11px] text-muted-foreground/70">Your overall take — this is what enters your taste model.</p>
              </div>

              <div>
                <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-muted">Breakdown (optional)</div>
                <div className="space-y-1">
                  {aspects.map((a) => (
                    <AspectRatingRow
                      key={a.key}
                      label={a.label}
                      value={current[a.key]}
                      highlight={probe.aspect === a.key}
                      onRate={(v: number) => rate(a.key, v)}
                    />
                  ))}
                </div>
              </div>

              {saveErr && <p className="text-xs text-rose-300">{saveErr}</p>}

              <div className="flex items-center gap-2 pt-1">
                <button type="button" onClick={() => go(-1)} disabled={idx === 0} className="rounded-md border border-border bg-card px-3 py-2 text-sm text-muted-foreground hover:text-foreground disabled:opacity-40">← Prev</button>
                {atEnd
                  ? <button type="button" onClick={load} className={`${btnCls} flex-1`}>Done — new batch</button>
                  : <button type="button" onClick={() => go(1)} className={`${btnCls} flex-1`}>Next →</button>}
              </div>
              {summary && <p className="text-[11px] text-muted-foreground/60">{summary}</p>}
            </div>
          </div>
        )}
    </div>
  );
}

// ── "My tastes": visualize the user's taste clusters + per-cluster feeds ─────

function ClusterChips({ tags }: { tags: any[] }) {
  if (!tags || tags.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-1">
      {tags.map((t: any, i: number) => (
        <span key={i} className="rounded bg-card px-1.5 py-0.5 text-xs text-muted-foreground" title={`weight ${(t.weight ?? 0).toFixed(2)}`}>
          {t.name}
          <span className="ml-1 text-[10px] text-muted-foreground/60">{Math.round((t.weight ?? 0) * 100)}</span>
        </span>
      ))}
    </div>
  );
}

function TastesView({ onNavigate, recommender, onRecommender }: { onNavigate?: (t: any) => void; recommender: string; onRecommender: (id: string) => void }) {
  const [recommenders, setRecommenders] = useState<any[]>([]);
  const [clusters, setClusters] = useState<any[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [feed, setFeed] = useState<{ label: string; rows: any[] } | null>(null);
  const feedRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => { if (feed) feedRef.current?.scrollIntoView({ behavior: "smooth", block: "start" }); }, [feed?.label]);

  useEffect(() => {
    (async () => {
      try {
        const list = ((await api("/recommenders")) || []).filter((r: any) => (r.targetEntityTypes || []).includes("video"));
        setRecommenders(list);
        if (!recommender || !list.some((r: any) => r.id === recommender))
          onRecommender(list.find((r: any) => r.id.includes("clusters"))?.id || list[0]?.id || "");
      } catch (e: any) { setError(e.message || "Failed to load recommenders."); }
    })();
  }, []);

  const load = useCallback(async () => {
    if (!recommender) return;
    setLoading(true); setError(""); setClusters([]); setFeed(null);
    try {
      const data = await api(`/clusters?recommender=${encodeURIComponent(recommender)}`);
      setClusters(Array.isArray(data) ? data : []);
    } catch (e: any) { setError(e.message || "Failed to load clusters."); }
    finally { setLoading(false); }
  }, [recommender]);

  useEffect(() => { load(); }, [load]);

  async function showClusterFeed(c: any) {
    setFeed({ label: c.label, rows: [] });
    try {
      const result = await api(`/feed?recommender=${encodeURIComponent(recommender)}&context=globalfeed&entityType=video&limit=40&clusterId=${encodeURIComponent(c.id)}`);
      setFeed({ label: c.label, rows: (result?.items || []).map(normalizeRow) });
    } catch { /* ignore */ }
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <Labeled label="Recommender">
          <select className={selectCls} value={recommender} onChange={(e) => onRecommender(e.target.value)}>
            {recommenders.map((r) => <option key={r.id} value={r.id}>{r.label}</option>)}
          </select>
        </Labeled>
      </div>
      <p className="text-sm text-muted-foreground">Your taste clusters from this recommender — each a distinct niche with its own visual + tag profile. The cards below each cluster are <em>your own liked content</em> that fell into it, so you can judge whether the clustering makes sense. Click "Recommend from this" to get suggestions scoped to one niche.</p>

      {error && <p className="rounded-md border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{error}</p>}
      {loading
        ? <p className="text-sm text-muted-foreground">Computing your clusters…</p>
        : clusters.length === 0
          ? <p className="rounded-md border border-dashed border-border bg-muted/20 p-4 text-sm text-muted-foreground">This recommender doesn't expose clusters, or you don't have enough clustered taste yet (need liked videos).</p>
          : clusters.map((c) => (
              <div key={c.id} className="space-y-2 rounded-md border border-border bg-surface/50 p-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="flex items-baseline gap-2">
                    <span className="text-base font-semibold text-foreground">{c.label}</span>
                    <span className="text-xs text-muted-foreground">{c.size} liked videos</span>
                  </div>
                  <button className="rounded-md border border-border bg-card px-3 py-1.5 text-xs text-foreground hover:bg-card-hover" type="button" onClick={() => showClusterFeed(c)}>
                    Recommend from this
                  </button>
                </div>
                <ClusterChips tags={c.topTags} />
                <ResultsGrid rows={(c.samples || []).map((s: any) => ({ entityType: s.entityType, entityId: s.entityId }))} onNavigate={onNavigate} />
              </div>
            ))}

      {feed && (
        <div ref={feedRef} className="space-y-2 rounded-md border border-accent/40 bg-accent/5 p-3">
          <h3 className="text-base font-semibold text-foreground">Recommended from {feed.label}</h3>
          {feed.rows.length === 0 ? <p className="text-sm text-muted-foreground">Loading…</p> : <ResultsGrid rows={feed.rows} onNavigate={onNavigate} />}
        </div>
      )}
    </div>
  );
}

// ── "Taste profile": what tags/performers/studios a recommender thinks you like ─

function AffinityBars({ items, emptyNote }: { items: any[]; emptyNote: string }) {
  const [q, setQ] = useState("");
  if (!items || items.length === 0) return <p className="text-xs text-muted-foreground">{emptyNote}</p>;
  const term = q.trim().toLowerCase();
  const shown = term ? items.filter((e: any) => (e.name || "").toLowerCase().includes(term)) : items;
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between gap-2">
        <input
          className={`${selectCls} h-7 w-full py-0.5 text-xs`}
          placeholder={`Filter ${items.length}…`}
          value={q}
          onChange={(e) => setQ(e.target.value)}
        />
        <span className="shrink-0 text-[11px] tabular-nums text-muted-foreground/70">{shown.length}/{items.length}</span>
      </div>
      <div className="max-h-[26rem] space-y-1 overflow-y-auto pr-1">
        {shown.map((e: any, i: number) => {
          const pct = Math.round((e.weight ?? 0) * 100);
          const neg = pct < 0;
          return (
            <div key={i} className="flex items-center gap-2">
              <div className="w-40 shrink-0 truncate text-sm text-foreground" title={e.name}>{e.name}</div>
              <div className="relative h-2 flex-1 overflow-hidden rounded bg-card">
                <div className={neg ? "h-full bg-rose-500/70" : "h-full bg-accent"} style={{ width: `${Math.abs(pct)}%` }} />
              </div>
              <div className={`w-9 shrink-0 text-right text-xs tabular-nums ${neg ? "text-rose-300" : "text-muted-foreground"}`}>{pct}</div>
              {e.detail && <div className="w-44 shrink-0 truncate text-xs text-muted-foreground/70" title={e.detail}>{e.detail}</div>}
            </div>
          );
        })}
        {shown.length === 0 && <p className="text-xs text-muted-foreground">No match.</p>}
      </div>
    </div>
  );
}

function ProfileView({ recommender, onRecommender }: { recommender: string; onRecommender: (id: string) => void }) {
  const [recommenders, setRecommenders] = useState<any[]>([]);
  const [profile, setProfile] = useState<any>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [count, setCount] = useState(80);

  useEffect(() => {
    (async () => {
      try {
        const list = ((await api("/recommenders")) || []).filter((r: any) => (r.targetEntityTypes || []).includes("video"));
        setRecommenders(list);
        if (!recommender || !list.some((r: any) => r.id === recommender))
          onRecommender(list.find((r: any) => r.id.includes("deep"))?.id || list[0]?.id || "");
      } catch (e: any) { setError(e.message || "Failed to load recommenders."); }
    })();
  }, []);

  useEffect(() => {
    (async () => {
      if (!recommender) return;
      setLoading(true); setError(""); setProfile(null);
      try { setProfile(await api(`/profile?recommender=${encodeURIComponent(recommender)}&limit=${count}`)); }
      catch (e: any) { setError(e.message || "Failed to load profile."); }
      finally { setLoading(false); }
    })();
  }, [recommender, count]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <Labeled label="Recommender">
          <select className={selectCls} value={recommender} onChange={(e) => onRecommender(e.target.value)}>
            {recommenders.map((r) => <option key={r.id} value={r.id}>{r.label}</option>)}
          </select>
        </Labeled>
        <Labeled label="Detail (per facet)">
          <select className={selectCls} value={count} onChange={(e) => setCount(Number(e.target.value))}>
            {[40, 80, 150, 300].map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        </Labeled>
      </div>
      <p className="text-sm text-muted-foreground">What this recommender thinks about your taste — the tags, performers, and studios it derived from your engagement, and how strongly (−100…100). <span className="text-rose-300">Red / negative</span> = signals it thinks you <em>dislike</em> (they push candidates down); accent = signals you like. Compare recommenders to judge whose model matches reality.</p>

      {error && <p className="rounded-md border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{error}</p>}
      {profile?.summary && <p className="text-xs text-muted-foreground/70">{profile.summary}</p>}
      {loading
        ? <p className="text-sm text-muted-foreground">Computing your profile…</p>
        : profile && (
          <div className="grid gap-4 md:grid-cols-2">
            <section className="space-y-2 rounded-md border border-border bg-surface/50 p-3">
              <h3 className="text-sm font-semibold text-foreground">Tags</h3>
              <AffinityBars items={profile.tags} emptyNote="No tag affinity (this recommender may be visual-only)." />
            </section>
            <section className="space-y-2 rounded-md border border-border bg-surface/50 p-3">
              <h3 className="text-sm font-semibold text-foreground">Performers</h3>
              <AffinityBars items={profile.performers} emptyNote="No performer affinity yet." />
            </section>
            {profile.studios && profile.studios.length > 0 && (
              <section className="space-y-2 rounded-md border border-border bg-surface/50 p-3">
                <h3 className="text-sm font-semibold text-foreground">Studios</h3>
                <AffinityBars items={profile.studios} emptyNote="No studio affinity yet." />
              </section>
            )}
          </div>
        )}
    </div>
  );
}

// Recommended list sorts by the model's overall score; the direction toggle (best-first / worst-first) is what
// lets you inspect a signal — zero the other knobs, reverse, and the lowest-scoring-by-that-signal float up.
const RECOMMENDED_SORT = [{ value: "recommended", label: "Recommended" }];

function FeedView({ onNavigate, recommender, onRecommender }: { onNavigate?: (t: any) => void; recommender: string; onRecommender: (id: string) => void }) {
  const [recommenders, setRecommenders] = useState<any[]>([]);
  const [rows, setRows] = useState<any[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [diag, setDiag] = useState<any>(null);
  const [totalCount, setTotalCount] = useState(0);
  const [reload, setReload] = useState(0);
  const [showTuning, setShowTuning] = useState(false);
  const [showAdvanced, setShowAdvanced] = useState(false);

  // Persist the whole view (content type, sort/filters/display, and tuning knobs) so it survives page nav.
  const saved = useMemo(() => { try { return JSON.parse(localStorage.getItem("rec-feed-state") || "{}"); } catch { return {}; } }, []);
  const [entityType, setEntityType] = useState(saved.entityType || "video");
  const isVideo = entityType === "video";

  // Standard list state (search / sort / filters / pagination / page-size / card-size) via the host hook, so
  // the recommended feed behaves like every other Cove list page — seeded from the persisted state.
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "rec-feed",
    // Seed sort/paging from the persisted view, but NEVER the search query: useListUrlState falls back to
    // defaultFilter.q whenever q is absent from the URL, so a persisted q would spring back the instant you clear
    // the search box (making it impossible to remove). Search lives in the URL only.
    defaultFilter: { page: 1, perPage: 60, sort: "recommended", direction: "desc", ...(saved.filter || {}), q: undefined },
    defaultObjectFilter: saved.objectFilter || {},
    defaultDisplayMode: saved.displayMode || "grid",
    allowedDisplayModes: ["grid", "list", "feed", "vertical"] as const,
    allowInfinitePageSize: true,
  });

  // Persist view state on change (debounce-free; it's tiny).
  useEffect(() => {
    try { localStorage.setItem("rec-feed-state", JSON.stringify({ entityType, filter: { ...filter, q: undefined }, objectFilter, displayMode })); } catch { /* ignore */ }
  }, [entityType, filter, objectFilter, displayMode]);

  // Recommenders that can target the selected content type (only those are offered / picked).
  const supported = recommenders.filter((r) => (r.targetEntityTypes || []).includes(entityType));

  const [knobs, setKnobs] = useState<Record<string, number>>({});
  const knobsRef = useRef<Record<string, number>>({});
  const [steerTags, setSteerTags] = useState("");
  const [steerPerformers, setSteerPerformers] = useState("");
  const steerRef = useRef({ tags: "", performers: "" });

  const active = recommenders.find((r) => r.id === recommender);

  useEffect(() => {
    (async () => {
      try {
        setRecommenders((await api("/recommenders")) || []);
      } catch (e: any) { setError(e.message || "Failed to load recommenders."); }
    })();
  }, []);

  // Keep the chosen recommender valid for the current content type (fall back to a preferred/first supported one).
  useEffect(() => {
    if (recommenders.length === 0) return;
    if (recommender && supported.some((r) => r.id === recommender)) return;
    (async () => {
      const pref = await api(`/preference?context=globalfeed&entityType=${entityType}`).catch(() => null);
      onRecommender((pref?.recommenderId && supported.some((r) => r.id === pref.recommenderId) ? pref.recommenderId : supported[0]?.id) || "");
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entityType, recommenders.length]);

  // Initialize knob values when the recommender changes — from the persisted tuning for that recommender, else
  // its defaults. Persisted so your tuning survives page navigation.
  const knobStoreKey = (rec: string) => `rec-knobs:${rec}`;
  useEffect(() => {
    if (!active) return;
    let savedKnobs: Record<string, number> | null = null;
    try { const s = localStorage.getItem(knobStoreKey(recommender)); if (s) savedKnobs = JSON.parse(s); } catch { /* ignore */ }
    const init: Record<string, number> = {};
    for (const k of active.knobs || []) init[k.key] = savedKnobs?.[k.key] ?? k.default;
    knobsRef.current = init;
    setKnobs(init);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [recommender, recommenders.length]);

  // Reset to page 1 whenever the filter set or content type changes (but not on first mount / URL-restore).
  const objectFilterKey = JSON.stringify(objectFilter);
  const firstFilter = useRef(true);
  useEffect(() => {
    if (firstFilter.current) { firstFilter.current = false; return; }
    setFilter({ ...filter, page: 1 });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [objectFilterKey, entityType]);

  // Page size 0 (the "Infinite" option) accumulates + loads more on scroll; otherwise it's true pagination.
  const infinite = (filter.perPage ?? 60) <= 0;
  const perPageEff = infinite ? 60 : (filter.perPage ?? 60);
  const loadedPagesRef = useRef(1);
  const [fetchingMore, setFetchingMore] = useState(false);

  const loadPage = useCallback(async (pageNum: number, append: boolean) => {
    const body = {
      recommender, context: "globalfeed", entityType,
      knobs: Object.keys(knobsRef.current).length ? knobsRef.current : undefined,
      steerTags: steerRef.current.tags.trim() || undefined,
      steerPerformers: steerRef.current.performers.trim() || undefined,
      // Advanced filters are video-only for now; other types use the recommender's own pool.
      objectFilter: isVideo ? objectFilter : {},
      findFilter: {
        q: isVideo ? ((filter as any).q ?? undefined) : undefined,
        page: pageNum,
        perPage: perPageEff,
        sort: filter.sort ?? "recommended",
        direction: filter.direction ?? "desc",
      },
    };
    const result = await api("/feed", { method: "POST", body: JSON.stringify(body) });
    const newRows = (result?.items || []).map(normalizeRow);
    setRows((prev) => (append ? [...prev, ...newRows] : newRows));
    setTotalCount(result?.totalCount ?? (result?.items?.length || 0));
    setDiag(result?.diagnostics || null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [recommender, entityType, objectFilterKey, isVideo, (filter as any).q, filter.sort, filter.direction, perPageEff]);

  // Fresh load whenever inputs change (paged: the requested page; infinite: reset to the first chunk).
  useEffect(() => {
    if (!recommender) return;
    let cancelled = false;
    loadedPagesRef.current = 1;
    setLoading(true); setError("");
    loadPage(infinite ? 1 : (filter.page ?? 1), false)
      .catch((e: any) => { if (!cancelled) { setError(e.message || "Failed to load feed."); setRows([]); setTotalCount(0); } })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [recommender, entityType, objectFilterKey, (filter as any).q, filter.page, filter.perPage, filter.sort, filter.direction, reload]);

  const loadMore = useCallback(async () => {
    if (fetchingMore) return;
    const next = loadedPagesRef.current + 1;
    setFetchingMore(true);
    try { await loadPage(next, true); loadedPagesRef.current = next; }
    catch { /* keep what's loaded */ }
    finally { setFetchingMore(false); }
  }, [fetchingMore, loadPage]);

  function setKnob(key: string, value: number) {
    setKnobs((prev) => {
      const next = { ...prev, [key]: value };
      knobsRef.current = next;
      try { localStorage.setItem(knobStoreKey(recommender), JSON.stringify(next)); } catch { /* ignore */ }
      return next;
    });
  }
  // Re-rank from page 1 when a tuning input (knob / steer) changes the model.
  const applyTuning = () => { setFilter({ ...filter, page: 1 }); setReload((n) => n + 1); };

  async function choose(id: string) {
    onRecommender(id);
    setFilter({ ...filter, page: 1 });
    await api("/preference", { method: "PUT", body: JSON.stringify({ context: "globalfeed", entityType, recommenderId: id }) }).catch(() => {});
  }

  // The content types at least one recommender can target, in a friendly order.
  const availableTypes = ENTITY_TYPES.filter((t) => t !== "any" && recommenders.some((r) => (r.targetEntityTypes || []).includes(t)));

  // Compact under-title bar: content-type + recommender pickers (kept small so the toolbar search stays its
  // normal width). Diagnostics render in the content area below.
  const byline = (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
      {availableTypes.length > 1 && (
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          <span>Show</span>
          <select className={`${selectCls} h-7 py-0.5 text-xs`} value={entityType} onChange={(e) => setEntityType(e.target.value)}>
            {availableTypes.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        </label>
      )}
      <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <span>Recommender</span>
        <select className={`${selectCls} h-7 py-0.5 text-xs`} value={recommender} onChange={(e) => choose(e.target.value)}>
          {supported.map((r) => <option key={r.id} value={r.id}>{r.label}</option>)}
        </select>
      </label>
    </div>
  );

  // "Recommended" (model ranking) plus the standard video sort fields (which just browse the filtered library
  // in that order — no scoring, so score badges hide). Non-video types only offer Recommended.
  const sortOptions = isVideo ? [{ value: "recommended", label: "Recommended" }, ...VIDEO_SORT_OPTIONS] : RECOMMENDED_SORT;
  const showScores = (filter.sort ?? "recommended") === "recommended";

  return (
    <ListPage
      title="Recommended"
      pageKey="rec-feed"
      filterMode={isVideo ? "videos" : entityType}
      filter={filter}
      onFilterChange={setFilter}
      totalCount={totalCount}
      isLoading={loading}
      sortOptions={sortOptions}
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={(isVideo ? ["grid", "list", "feed", "vertical"] : ["grid"]) as any}
      allowInfinitePageSize
      showPagingControls={!infinite}
      infiniteScroll={infinite ? { hasNextPage: rows.length < totalCount, isFetchingNextPage: fetchingMore, onLoadMore: loadMore, loadedCount: rows.length, totalCount } : undefined}
      searchPlaceholder="Search videos, tags, performers…"
      criteriaDefinitions={isVideo ? VIDEO_CRITERIA : []}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      metadataByline={byline}
      renderOperations={() => (
        <button
          type="button"
          onClick={() => setShowTuning((v) => !v)}
          className="inline-flex items-center gap-1.5 rounded-lg border border-border bg-card/70 px-3 py-1 text-xs text-secondary hover:border-accent hover:text-foreground"
        >
          Tune{showTuning ? " ▲" : " ▼"}
        </button>
      )}
    >
      {showTuning && (active?.knobs?.length ?? 0) > 0 && (() => {
        const knobsList: any[] = active.knobs;
        const groups: Record<string, any[]> = {};
        for (const k of knobsList) { const g = k.group || "Tuning"; (groups[g] ||= []).push(k); }
        const isAdvanced = (g: string) => /\(advanced\)/i.test(g);
        const primaryGroups = Object.keys(groups).filter((g) => !isAdvanced(g));
        const advancedGroups = Object.keys(groups).filter(isAdvanced);
        const renderKnob = (k: any) => (
          <label key={k.key} className="flex flex-col gap-1 text-xs text-muted-foreground" title={k.description}>
            <span>{k.label}: <span className="tabular-nums text-foreground">{(knobs[k.key] ?? k.default).toFixed(2)}</span></span>
            <input
              type="range"
              min={k.min}
              max={k.max}
              step={((k.max - k.min) / 100) || 0.01}
              value={knobs[k.key] ?? k.default}
              onChange={(e) => setKnob(k.key, Number(e.target.value))}
              onMouseUp={applyTuning}
              onTouchEnd={applyTuning}
              className="w-44 accent-[var(--color-accent)]"
            />
          </label>
        );
        const renderGroup = (g: string, faint?: boolean) => (
          <div key={g} className={`rounded-md border border-border p-3 ${faint ? "bg-surface/30" : "bg-surface/50"}`}>
            <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-muted">{g.replace(/\s*\(advanced\)/i, "")}</div>
            <div className="flex flex-wrap gap-x-6 gap-y-2">{groups[g].map(renderKnob)}</div>
          </div>
        );
        return (
          <div className="mb-3 space-y-2">
            <p className="text-[11px] text-muted-foreground/70">Weights are non-negative importances; each signal is centered so it can push a video up OR down. Diversity spreads the top across clusters/performers. “Advanced” sets what makes up each aspect. To inspect one signal, zero the others and reverse the sort direction.</p>
            {primaryGroups.map((g) => renderGroup(g))}
            {advancedGroups.length > 0 && (
              <div className="space-y-2">
                <button type="button" onClick={() => setShowAdvanced((v) => !v)} className="text-xs text-muted-foreground hover:text-foreground">
                  {showAdvanced ? "Hide" : "Show"} advanced signal mix {showAdvanced ? "▲" : "▼"}
                </button>
                {showAdvanced && advancedGroups.map((g) => renderGroup(g, true))}
              </div>
            )}
            {active?.description && <p className="text-sm text-muted-foreground">{active.description}</p>}
          </div>
        );
      })()}

      {diag && (
        <div className="mb-2 flex flex-wrap items-center gap-1.5">
          {Object.entries(diag).filter(([k]) => k !== "recommender").map(([k, v]) => (
            <span key={k} className="rounded bg-card px-1.5 py-0.5 text-xs text-muted-foreground"><span className="text-foreground/60">{k}</span> <span className="tabular-nums text-foreground">{String(v)}</span></span>
          ))}
        </div>
      )}
      {error && <p className="mb-3 rounded-md border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{error}</p>}
      <ResultsGrid rows={rows} onNavigate={onNavigate} displayMode={isVideo ? displayMode : "grid"} entityType={entityType} showScores={showScores} />
    </ListPage>
  );
}

function InspectorView({ onNavigate }: { onNavigate?: (t: any) => void }) {
  const [entityType, setEntityType] = useState("any");
  const [method, setMethod] = useState("m2");
  const [limit, setLimit] = useState(60);
  const [rows, setRows] = useState<any[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [debug, setDebug] = useState<any>(null);

  const load = useCallback(async () => {
    setLoading(true); setError("");
    try {
      const data = await api(`/inspector/top-liked?entityType=${entityType}&limit=${limit}&method=${method}`);
      setRows((Array.isArray(data) ? data : []).map(normalizeRow));
    } catch (e: any) { setError(e.message || "Failed to load."); }
    finally { setLoading(false); }
  }, [entityType, limit, method]);

  useEffect(() => { load(); }, [load]);

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        What the preference model thinks you like, and why. Score −1…+1; confidence reflects how much engagement backs it.
        Missing signals lower confidence but never bias the score.
      </p>
      <div className="flex flex-wrap items-end gap-3">
        <Labeled label="Entity type">
          <select className={selectCls} value={entityType} onChange={(e) => setEntityType(e.target.value)}>
            {ENTITY_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        </Labeled>
        <Labeled label="Fusion method">
          <select className={selectCls} value={method} onChange={(e) => setMethod(e.target.value)}>
            <option value="m1">M1 — weighted</option>
            <option value="m2">M2 — noisy-OR</option>
          </select>
        </Labeled>
        <Labeled label="Limit">
          <input type="number" min={1} max={500} className={`w-20 ${selectCls}`} value={limit}
            onChange={(e) => setLimit(Math.max(1, Math.min(500, Number(e.target.value) || 1)))} />
        </Labeled>
        <button className={btnCls} type="button" onClick={load} disabled={loading}>{loading ? "Loading…" : "Refresh"}</button>
        <button className="rounded-md border border-border bg-surface px-3 py-2 text-sm text-muted-foreground hover:bg-card-hover"
          type="button" onClick={async () => setDebug(await api("/inspector/debug").catch((e) => ({ error: e.message })))}>
          Diagnostics
        </button>
      </div>

      {debug && (
        <pre className="max-h-64 overflow-auto rounded-md border border-border bg-black/30 p-3 text-xs text-muted-foreground">
          {JSON.stringify(debug, null, 2)}
        </pre>
      )}
      {error && <p className="rounded-md border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{error}</p>}
      <ResultsGrid rows={rows} onNavigate={onNavigate} />
    </div>
  );
}

// ── Per-detail "Recommended" tab (SimilarToEntity) ──────────────────────────

function RecommendedTab({ entityId, onNavigate }: { entityId: number; onNavigate?: (t: any) => void }) {
  const [recommenders, setRecommenders] = useState<any[]>([]);
  const [recommender, setRecommender] = useState("");
  const [rows, setRows] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      try {
        const list = ((await api("/recommenders")) || []).filter((r: any) => (r.targetEntityTypes || []).includes("video"));
        setRecommenders(list);
        const pref = await api("/preference?context=similar&entityType=video").catch(() => null);
        const chosen = pref?.recommenderId && list.some((r: any) => r.id === pref.recommenderId) ? pref.recommenderId : (list[0]?.id || "");
        setRecommender(chosen);
      } catch (e: any) { setError(e.message || "Failed to load recommenders."); }
    })();
  }, []);

  const load = useCallback(async () => {
    if (!recommender) return;
    setLoading(true); setError("");
    try {
      const result = await api(`/feed?recommender=${encodeURIComponent(recommender)}&context=similar&seedType=video&seedId=${entityId}&entityType=video&limit=40`);
      setRows((result?.items || []).map(normalizeRow));
    } catch (e: any) { setError(e.message || "Failed to load."); setRows([]); }
    finally { setLoading(false); }
  }, [recommender, entityId]);

  useEffect(() => { load(); }, [load]);

  async function choose(id: string) {
    setRecommender(id);
    await api("/preference", { method: "PUT", body: JSON.stringify({ context: "similar", entityType: "video", recommenderId: id }) }).catch(() => {});
  }

  return (
    <div className="recommendations-page space-y-3 p-2">
      {recommenders.length > 1 && (
        <Labeled label="Recommender">
          <select className={selectCls} value={recommender} onChange={(e) => choose(e.target.value)}>
            {recommenders.map((r) => <option key={r.id} value={r.id}>{r.label}</option>)}
          </select>
        </Labeled>
      )}
      {error && <p className="rounded-md border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{error}</p>}
      {loading
        ? <p className="text-sm text-muted-foreground">Finding similar videos…</p>
        : <ResultsGrid rows={rows} onNavigate={onNavigate} />}
    </div>
  );
}

// ── Settings: per-entity-type rating "neutral" (the like/dislike boundary) ──
const RATING_NEUTRAL_TYPES: { key: string; label: string; hint: string }[] = [
  { key: "video", label: "Videos", hint: "Neutral for video ratings — the target the taste model is built from." },
  { key: "image", label: "Images", hint: "Neutral for image ratings." },
  { key: "tag", label: "Tags", hint: "Neutral for tag ratings — below it, a rated tag pushes matching content down." },
  { key: "performer", label: "Performers", hint: "Neutral for performer ratings." },
  { key: "studio", label: "Studios", hint: "Neutral for studio ratings." },
];

function RecommendationsSettings() {
  const [neutrals, setNeutrals] = useState<Record<string, number> | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    (async () => {
      try { setNeutrals(await api("/settings/rating-neutrals")); }
      catch (e: any) { setError(e?.message || "Failed to load settings."); }
    })();
  }, []);

  const update = useCallback((key: string, value: number | undefined) => {
    setNeutrals((cur) => {
      // Clearing (clicking the current value) keeps it rather than blanking a neutral that must exist.
      const v = value == null ? (cur?.[key] ?? 50) : Math.round(value);
      const next = { ...(cur ?? {}), [key]: v };
      setSaving(true); setError(null);
      api("/settings/rating-neutrals", { method: "PUT", body: JSON.stringify({ [key]: v }) })
        .catch((e: any) => setError(e?.message || "Failed to save."))
        .finally(() => setSaving(false));
      return next;
    });
  }, []);

  if (error && !neutrals) return <p className="text-sm text-red-400">{error}</p>;
  if (!neutrals) return <p className="text-sm text-muted-foreground">Loading…</p>;

  return (
    <div className="max-w-2xl space-y-6">
      <div>
        <h3 className="text-base font-semibold text-foreground">Rating neutral points</h3>
        <p className="mt-1 text-sm text-secondary">
          The rating that counts as “neutral” for each kind of thing. Ratings are read <em>absolutely</em>{" "}
          against this point — not relative to your average — so a rating above it pulls the recommender toward
          that content, below it pushes away, and a rating right at it does nothing. Set each wherever your “meh”
          is: if you tend to rate generously, raise the neutral so only your genuine favorites count as positive.
        </p>
      </div>
      <div className="space-y-3">
        {RATING_NEUTRAL_TYPES.map(({ key, label, hint }) => (
          <div key={key} className="flex items-center justify-between gap-4 rounded-lg border border-border bg-card px-4 py-3">
            <div className="min-w-0">
              <div className="text-sm font-medium text-foreground">{label}</div>
              <div className="text-xs text-secondary">{hint}</div>
            </div>
            <div className="shrink-0">
              <InteractiveRating value={neutrals[key] ?? 50} onChange={(v) => update(key, v)} />
            </div>
          </div>
        ))}
      </div>
      <p className="text-xs text-muted-foreground">{saving ? "Saving…" : "Changes save automatically and apply to future recommendation refreshes."}</p>
      {error ? <p className="text-xs text-red-400">{error}</p> : null}
    </div>
  );
}

// ── Recommender health: grade the recommender against the user's own ratings/engagement so quality is measured,
// not eyeballed — ranking alignment, per-signal calibration health, and the worst rating↔rank inversions.
function RecommenderHealthPage() {
  const [recommenders, setRecommenders] = useState<any[]>([]);
  const [recommender, setRecommender] = useState<string>("");
  const [report, setReport] = useState<any>(null);
  const [invVideos, setInvVideos] = useState<Record<number, any>>({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    (async () => {
      try {
        const list = ((await api("/recommenders")) || []).filter((r: any) => (r.targetEntityTypes || []).includes("video"));
        setRecommenders(list);
        const pick = list.find((r: any) => /niche/i.test(r.id)) || list[0];
        if (pick) setRecommender(pick.id);
      } catch (e: any) { setError(e.message); }
    })();
  }, []);

  const run = useCallback(async () => {
    if (!recommender) return;
    setLoading(true); setError(null); setReport(null); setInvVideos({});
    try {
      const rep = await api(`/eval?recommender=${encodeURIComponent(recommender)}`);
      setReport(rep);
      const ids = (rep.inversions || []).map((x: any) => x.videoId);
      if (ids.length) setInvVideos(await hydrate("video", ids).catch(() => ({})));
    } catch (e: any) { setError(e.message); }
    finally { setLoading(false); }
  }, [recommender]);

  const corr = (v: number | null) => (v == null ? "—" : v.toFixed(2));
  const corrCls = (v: number | null) => (v == null ? "text-neutral-500" : v >= 0.4 ? "text-green-400" : v >= 0.15 ? "text-yellow-400" : v < -0.02 ? "text-red-400" : "text-neutral-300");
  const pct = (v: number) => `${Math.round((v || 0) * 100)}%`;

  return (
    <div className="p-4 space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-lg font-semibold">Recommender health</h1>
        <select className="rounded bg-neutral-800 border border-neutral-700 px-2 py-1 text-sm"
          value={recommender} onChange={(e) => setRecommender(e.target.value)}>
          {recommenders.map((r) => <option key={r.id} value={r.id}>{r.label}</option>)}
        </select>
        <button type="button" onClick={run} disabled={loading || !recommender}
          className="rounded bg-blue-600 hover:bg-blue-500 disabled:opacity-50 px-3 py-1 text-sm text-white">
          {loading ? "Evaluating…" : "Run evaluation"}
        </button>
      </div>
      <p className="text-xs text-neutral-400 max-w-3xl">
        Grades the recommender against your own rated/engaged videos: do your favorites rank high, is each signal
        well-calibrated, and what's most mis-ranked. Uses default knobs; higher rank-correlation is better.
      </p>
      {error ? <p className="text-sm text-red-400">{error}</p> : null}
      {report ? (
        <div className="space-y-5">
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <div className="rounded border border-neutral-800 bg-neutral-900/50 p-3">
              <div className="text-xs text-neutral-400">Score vs ratings</div>
              <div className={`text-2xl font-semibold ${corrCls(report.scoreVsRating)}`}>{corr(report.scoreVsRating)}</div>
              <div className="text-[11px] text-neutral-500">{report.ratedCount} rated</div>
            </div>
            <div className="rounded border border-neutral-800 bg-neutral-900/50 p-3">
              <div className="text-xs text-neutral-400">Score vs engagement</div>
              <div className={`text-2xl font-semibold ${corrCls(report.scoreVsEngagement)}`}>{corr(report.scoreVsEngagement)}</div>
              <div className="text-[11px] text-neutral-500">{report.evaluatedCount} videos</div>
            </div>
            <div className="rounded border border-neutral-800 bg-neutral-900/50 p-3 col-span-2">
              <div className="text-xs text-neutral-400">Grading against</div>
              <div className="text-sm text-neutral-200">{report.target === "ratings" ? "your explicit ratings" : "engagement (sanity floor — rate more videos for a true grade)"}</div>
            </div>
          </div>

          {report.warnings?.length ? (
            <ul className="space-y-1">{report.warnings.map((w: string, i: number) => <li key={i} className="text-xs text-yellow-300">⚠ {w}</li>)}</ul>
          ) : null}

          <div>
            <h2 className="text-sm font-semibold mb-2">Signals</h2>
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead className="text-neutral-400"><tr className="text-left">
                  <th className="py-1 pr-3">signal</th><th className="pr-3">aspect</th><th className="pr-3">present</th>
                  <th className="pr-3">corr</th><th className="pr-3">spread</th><th className="pr-3">silent</th><th className="pr-3">curve</th><th></th>
                </tr></thead>
                <tbody>
                  {report.signals.map((s: any) => (
                    <tr key={s.key} className="border-t border-neutral-800">
                      <td className="py-1 pr-3 font-mono">{s.key}</td>
                      <td className="pr-3 text-neutral-400">{s.aspect}</td>
                      <td className="pr-3">{pct(s.presentFrac)}</td>
                      <td className={`pr-3 ${corrCls(s.corrWithTarget)}`}>{corr(s.corrWithTarget)}</td>
                      <td className="pr-3">{(s.std ?? 0).toFixed(2)}</td>
                      <td className="pr-3">{pct(s.silentFrac)}</td>
                      <td className="pr-3">{s.hasLearnedCurve ? "✓" : "—"}</td>
                      <td className="text-red-400">{s.degenerate ? "dead" : ""}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="text-[11px] text-neutral-500 mt-1">present = share of videos with the signal · corr = rank-correlation with your target · spread = how much it varies (low ⇒ weak) · silent = share inside the dead band · curve = learned calibration active</p>
          </div>

          <div>
            <h2 className="text-sm font-semibold mb-2">Aspects</h2>
            <div className="flex flex-wrap gap-3">
              {report.aspects.map((a: any) => (
                <div key={a.key} className="rounded border border-neutral-800 bg-neutral-900/50 p-2 min-w-[8rem]">
                  <div className="text-xs text-neutral-400">{a.key}</div>
                  <div className={`text-lg font-semibold ${corrCls(a.corrWithTarget)}`}>{corr(a.corrWithTarget)}</div>
                  <div className="text-[11px] text-neutral-500">coverage {pct(a.avgCoverage)}</div>
                </div>
              ))}
            </div>
          </div>

          <div>
            <h2 className="text-sm font-semibold mb-2">Biggest mis-rankings</h2>
            <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 gap-3">
              {report.inversions.map((inv: any) => {
                const v = invVideos[inv.videoId];
                return (
                  <div key={inv.videoId} className="space-y-1">
                    {v ? <VideoCard video={v} /> : <div className="aspect-video rounded bg-neutral-800 flex items-center justify-center text-xs text-neutral-500">#{inv.videoId}</div>}
                    <div className="text-[11px] text-neutral-400">{inv.note}</div>
                    <div className="text-[11px] text-neutral-500">you: {Math.round(inv.targetPct * 100)}% · ranked: {Math.round(inv.scorePct * 100)}%</div>
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      ) : (!loading && !error ? <p className="text-sm text-neutral-500">Pick a recommender and run an evaluation.</p> : null)}
    </div>
  );
}

export default {
  components: {
    RecommendedPage,
    RecommendedTab,
    RecommendationsSettings,
    RecommenderHealthPage,
  },
};
