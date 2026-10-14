# QuotaMonitor v1.5.3

A little Windows island for Codex quota, ClaudeCN balance and usage, and
DeepSeek Harness balance and usage. Includes daily heatmaps, low-balance alerts
and smooth Minimal → Collapsed → Expanded transitions.

DSH Balance Bridge **1.1.0** refreshes through the official Host account service:
immediately on startup and every three minutes after success. Failed queries
retain the last balance as Stale; account changes clear it. The Host must remain
running. Compatible target: Desktop 0.2.0-rc.2 / Cordis 4.0.4.

Download `QuotaMonitor-v1.5.3-win-x64.zip`, extract, and run `QuotaMonitor.exe`.
Windows 11 x64 is the verified target; the bundled runtime needs no separate
.NET installation. Configure your own accounts in Settings; DSH balance also
requires installing the included Bridge as described in its README.

Source and original project assets: MIT. Third-party runtime/library licenses
are bundled. Third-party logos remain subject to their owners' rights and are
outside the project MIT grant; see TRADEMARKS.md. All homepage imagery uses
invented demo data. This is a personal project without planned active maintenance.
