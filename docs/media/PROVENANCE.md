# Public media provenance

Artwork for QuotaMonitor **1.7.0** (Windows 11 x64 release), generated on 2026-11-13.
Public assets live in [`v1.7.0/`](v1.7.0/). The machine-readable manifest with sizes,
SHA256 digests and per-asset methods is
[`v1.7.0/asset-manifest.json`](v1.7.0/asset-manifest.json).

## Methods

| Asset | Method |
| --- | --- |
| `v1.7.0/hero.png` | Composed stage around an offline render of the actual four-provider Expanded WPF view |
| `v1.7.0/expanded.png` | Actual four-provider Expanded production view, offline render |
| `v1.7.0/collapsed.png` | Actual Collapsed production view, offline render |
| `v1.7.0/minimal.png` | Actual Minimal 8 px strip, offline render at the app's 150% raster scale |
| `v1.7.0/alert-minimal.png` | Actual Alert Minimal capsules from the production alert evaluator, offline render |
| `v1.7.0/states.png` | Actual Minimal / Collapsed / Expanded / Alert Minimal production states, offline renders |
| `v1.7.0/opencode-go.png` | Actual OpenCode Go-only Expanded view, offline render |
| `v1.7.0/heatmap.png` | Actual 26-week token heatmap, heading, legend and provider totals, cropped from the offline Expanded render |
| `v1.7.0/tooltip.png` | Actual date tooltip from the production view, offline render (no clipping) |
| `v1.7.0/settings.png` | Actual four-tab production Settings window frames, offline renders; isolated empty store, no credentials |
| `v1.7.0/layouts.png` | Actual 1/2/3/4-visible-provider Expanded layouts, offline renders |
| `v1.7.0/heatmap.gif` | **Real screen capture** of the packaged `QuotaMonitor.exe --preview-v170` window: hover tooltips on the four-provider 26-week heatmap |
| `v1.7.0/quota.gif` | **Real screen capture** of the same preview window: four-provider quota, collapsed limiting values, and the low-quota Alert Minimal capsules |

The PNGs are produced by rendering the unchanged production WPF visual tree with the optional
[Showcase tool](../../tools/QuotaMonitor.Showcase/README.md), which loads production resources
and views but never starts QuotaMonitor.App or any adapter. It blocks HTTP, uses an empty
isolated Settings store, a random nonexistent credential target, and disables startup
inspection. [Composition](../../tools/compose-showcase.py) requires Pillow and installed Segoe UI
fonts; no font files are redistributed.

The GIFs are recorded from the real packaged 1.7.0 preview executable
(SHA256 `aaf02b1fb514b7d0a2b675d12ea75823ef1135b17cb9f389e5a735058203fc0f`) in its
`--preview-v170` mode, driven by the application's synthetic preview fixture (no adapters,
credentials or account data). Only the application window rectangle is captured — never the
desktop or other applications — via
[`tools/capture-v170/capture-preview.ps1`](../../tools/capture-v170/capture-preview.ps1).
The preview window's flat synthetic background is keyed out in post-composition and the captured
island is placed on the same dark stage as the stills by
[`encode-gifs.py`](../../tools/capture-v170/encode-gifs.py). Heatmap GIF: 1100×840, 50 frames,
4.84 s. Quota GIF: 1100×840, 118 frames, 6.88 s. Identical hold frames are coalesced; the
captured motion is not retimed or fabricated.

`morph.gif` is intentionally **not shipped**: the real collapse/expand transition includes a
transient empty-shell frame that reads as a host-window artifact in a public GIF, and the
four-state still covers the same ground.

## Synthetic data and privacy

Every public image includes a permanent **SYNTHETIC DEMO DATA** marker. All figures — quota
percentages, reset countdowns, balances, token totals and 26-week heatmap values — are invented
for demonstration. No real API keys, account IDs, balances, emails, private paths or personal
usage appear. There is no desktop capture, no account API call, no production configuration or
history read, and no user identity in any asset. Public PNG/GIF files contain no EXIF, embedded
textual metadata or GIF comments.

The app UI remains Chinese; English headlines and captions are presentation labels, not an
English UI translation. GIF playback is a visual demonstration, not a performance measurement.
Third-party logos appearing in the views remain outside MIT; see
[TRADEMARKS.md](../../TRADEMARKS.md).

The earlier 1.6.4 artwork (`overview.png`, `details.png`, `island.gif`) is retained at the top
level of this folder for the previous release notes.

## v1.7.1 media refresh

QuotaMonitor **1.7.1** is a patch release whose only product change is the OpenCode Go monthly
low-quota compact label: `月` → `1mo` (the Expanded view keeps its Chinese `5 小时 / 每周 / 每月`
labels, Settings stays Chinese, and thresholds, colours and all other behaviour are unchanged).
Only the three public assets that visibly showed the old label were refreshed, into
[`v1.7.1/`](v1.7.1/); the machine-readable manifest with sizes, SHA256 digests and per-asset
methods is [`v1.7.1/asset-manifest.json`](v1.7.1/asset-manifest.json).

| Asset | Method |
| --- | --- |
| `v1.7.1/alert-minimal.png` | Same offline render + composition pipeline as v1.7.0 (Showcase + compose-showcase.py, v1.7.1 footer stamp); the OpenCode Go capsule now reads `1mo 6%` |
| `v1.7.1/states.png` | Same offline render + composition pipeline; Alert Minimal row shows `1mo`, Expanded row keeps `5 小时 / 每周 / 每月` |
| `v1.7.1/quota.gif` | New **real screen capture** of the packaged 1.7.1 preview executable (ProductVersion 1.7.1, SHA256 `c7e01f9a5c71c551a9361adf9cfb5534ee1c4f79ad60afafe1174cf2f199c686`) in `--preview-v170` mode; 1100×840, 109 stored frames, 6.96 s, synthetic fixture only |

The capture and composition tooling is unchanged apart from the public v1.7.1 version strings.
**`docs/media/v1.7.0/` is preserved byte-identically**, and all other v1.7.1 artwork is unchanged
from v1.7.0. The synthetic-data, privacy and metadata statements above still apply: every refreshed
asset contains invented fixture values only, carries the permanent **SYNTHETIC DEMO DATA** marker,
and contains no EXIF, textual metadata or GIF comment blocks; only the application window rectangle
is recorded, never the desktop or other applications.
