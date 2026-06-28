# Cove Multi-Extension Template

Use this template when one repository owns multiple related Cove extensions. On
GitHub, create new extension sets with the **Use this template** button.

After creating a repository from the template, replace the example extension IDs,
namespaces, manifest fields, catalog entries, and workflow matrix values with
your real extension set.

## Extension metadata lives in `extension.json`

Each extension's `extension.json` is the single source of truth for its identity
and metadata (`id`, `name`, `version`, `description`, `author`, `url`,
`categories`, `minCoveVersion`, `entryDll`, `dependencies`). Do **not** redeclare
any of these in C# — the example extensions extend `CoveExtensionBase`, which the
host injects the parsed manifest into at load time and surfaces all of those
values from it.

Implement whatever capability interfaces the extension needs alongside
`CoveExtensionBase` and override only the methods you use:

```csharp
using Cove.Plugins;
using Cove.Sdk;

// Scraper: keep the capability interface, drop all metadata properties.
public sealed class ExampleScraperExtension : CoveExtensionBase, IScraperProvider { /* ... */ }

// Downloader: same pattern.
public sealed class ExampleDownloaderExtension : CoveExtensionBase, IDownloaderProvider { /* ... */ }

// UI: CoveExtensionBase already implements IUIExtension; override GetUIManifest() to contribute UI.
public sealed class ExampleUiExtension : CoveExtensionBase { /* ... */ }
```

`extensions/catalog.json` lists every extension (id, path, tag prefix);
`scripts/validate-extension-repo.mjs` checks the catalog and manifests stay
consistent (including that each manifest's `minCoveVersion` is at least the
repo's `CoveMinVersion`).

## Build

```powershell
dotnet build -c Release
```

## Cove host references and central build wiring

The repo-root `Directory.Build.props` and `Directory.Build.targets` centralize
all Cove host-contract wiring, so each `.csproj` stays minimal. Any project with
an `extension.json` automatically references the Cove host contracts
(`Cove.Sdk` + `Cove.Core`) **compile-only** — the host provides them (and the EF
Core / Npgsql / Pgvector infrastructure) at runtime, so they are never shipped in
the package.

- **Local dev:** if this repo is checked out beside `cove`
  (`..\cove\src\Cove.Sdk` / `..\cove\src\Cove.Core` exist), `UseLocalCoveSource`
  and `UseLocalCoveCore` auto-enable and the projects reference the local Cove
  source via `ProjectReference`, so contract changes flow without a NuGet bump.
- **CI / external authors:** otherwise the projects use `PackageReference` to the
  published `Cove.Sdk` / `Cove.Core` packages at `CoveSdkVersion` /
  `CoveCoreVersion` (both default to `CoveMinVersion`).

Force package mode even with a sibling `cove` checkout present:

```powershell
dotnet build -p:UseLocalCoveSource=false -p:UseLocalCoveCore=false
```

## Release tags

Each extension has its own tag prefix:

- `example-ui/v0.1.0`
- `example-downloader/v0.1.0`
- `example-scraper/v0.1.0`

The CI workflow only packages the extension that matches the pushed tag.
