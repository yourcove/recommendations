# Cove Recommendations

Personalised recommendations for [Cove](https://github.com/yourcove/cove). Learns what you like from the
ratings and engagement you already produce while using your library, and ranks your whole collection against it.

Two extensions:

| Extension | What it does |
| --- | --- |
| **Recommendations: Core** (`cove.community.recommendations.core`) | The Recommended page, the per-video "Recommended" tab, settings, the shared plumbing every recommender builds on, and the **Your engagement** baseline recommender. |
| **Recommendations: Tastes** (`cove.community.recommendations.tastes`) | The models. Ships the **full taste model** plus two simpler ones to compare against. |

## The models

**Full taste model** — the primary one. Scores every video on four independent aspects: *performers* (faces,
learned per-performer affinity, and an attribute prior so a performer you've never rated still gets a sensible
read), *content* (tags and actions, a learned look axis, content-cluster fit, studio), *video quality* (a learned
craft axis plus objective resolution/bitrate fidelity), and *audio* (a voice axis). Each aspect carries its own
confidence and is calibrated against **your** library, so a score reads as "vs your typical" rather than as any
absolute claim. Every result explains which aspect drove it.

**Taste clusters** — splits your taste into distinct visual + tag niches and recommends across them, or from one.
Simpler, and a clear way to see how your taste breaks up.

**Overall affinity** — one global tag/visual/performer/studio profile, no clusters.

**Your engagement** — ships with Core rather than Tastes, because it needs no AI data at all. It recommends
nothing new: it ranks what you have already watched and rated by how much your engagement says you liked it, and
explains each score. That makes it both the honest baseline the learning models are judged against and something
useful on day one, before any model has anything to learn from.

## Using it

The **Recommended** page behaves like any other Cove list: the same search, filters, sorting, display modes,
multi-select and bulk actions. On top of that, the recommender contributes its own score dimensions as ordinary
filter criteria and sort options — so "shuffle among videos whose performer score is above 0.3" is just Sort:
Random plus a *Performers score* filter.

Other tabs: **Training** (rate the items the model is least sure about — the fastest way to teach it) and
**Taste profile** (what it thinks you like, and dislike).

Recommendations are **precomputed**. Each user's model is built in the background and persisted, and the whole
library is scored against it at startup and after every model rebuild, so opening the page reads precomputed
results rather than scoring your library on the spot. A rating schedules a rebuild, debounced to at most one per
hour per user.

## Requirements

- Cove **1.5.0** or newer.
- **AI Visual** (`cove.community.ai.visual`) for the taste models, which are built on visual embeddings. AI Faces
  and AI Audio are optional and improve the performers and audio aspects respectively. Signals you don't have
  simply lower confidence — they never bias a score.

Recommendations improve with use. A brand-new library with no ratings has nothing to learn from yet; rate a
handful of videos (the Training tab is the quickest route) and the feed becomes meaningful.

## Building

```powershell
dotnet build -c Release      # extensions
npm run build:ui             # UI bundles
npm run typecheck            # UI type-checks against a sibling cove checkout
```

`scripts/stage-local-extensions.ps1` builds everything and copies it into a local Cove install for testing.

`extensions/catalog.json` lists every extension (id, path, tag prefix), and
`scripts/validate-extension-repo.mjs` checks the catalog and manifests stay consistent.

### Cove host references

The repo-root `Directory.Build.props` / `Directory.Build.targets` centralise the Cove host wiring, so each
`.csproj` stays minimal. Any project with an `extension.json` references the Cove host contracts (`Cove.Sdk` +
`Cove.Core`) compile-only — the host provides them at runtime, so they are never shipped in the package.

- **Local dev:** with `cove` checked out beside this repo, the projects reference it by `ProjectReference`, so
  contract changes flow through without a package bump.
- **CI / external authors:** otherwise the published `Cove.Sdk` / `Cove.Core` packages are used.

Force package mode even with a sibling checkout:

```powershell
dotnet build -p:UseLocalCoveSource=false -p:UseLocalCoveCore=false
```

## Releases

Each extension has its own tag prefix, and CI packages only the extension matching the pushed tag:

- `core/v1.0.0`
- `tastes/v1.0.0`
