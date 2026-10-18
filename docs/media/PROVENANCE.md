# Public showcase provenance

Created on 2026-10-18 from QuotaMonitor **1.6.4** views, DSH Balance Bridge **1.2.0**.

| File | Contents |
| --- | --- |
| overview.png | Actual four-Harness Expanded WPF view; invented quota, balances and 26-week history |
| island.gif | 150 sampled frames of the production Minimal → Collapsed → Expanded → Minimal morph |
| details.png | Actual low-balance Minimal view and complete General Settings view with Harness filters and current version |

Every public frame includes **SYNTHETIC DEMO DATA**. The figures (72%, $18.50,
¥42.80, Antigravity 68% / 46% and alert values) and all daily aggregates are deliberately invented.
No internal screenshots were reused. All images come from rendering the WPF
visual tree, with a decorative background added afterward; there is no desktop
capture, account API, production configuration/history read, or user identity.

The optional [Showcase tool](../../tools/QuotaMonitor.Showcase/README.md) loads
production resources and views without starting QuotaMonitor.App or adapters.
It disables HTTP, supplies an empty Settings store and a random nonexistent
credential target, and disables startup inspection. [Composition script](../../tools/compose-showcase.py)
requires Pillow and installed Segoe UI fonts; no font files are redistributed.
The complete Settings panel is resized for presentation, without changing the view.

The GIF samples the real morph with 40 ms requested delays. Playback is a visual
demonstration, not a performance measurement. Identical hold frames are coalesced
when encoding while retaining their duration. The app UI remains Chinese;
English captions are presentation labels. PNG/GIF files contain no EXIF,
embedded textual metadata or GIF comments. Third-party logos appearing in the
views remain outside MIT; see [TRADEMARKS.md](../../TRADEMARKS.md).
