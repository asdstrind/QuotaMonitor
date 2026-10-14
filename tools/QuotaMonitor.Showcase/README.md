# Offline README artwork

This optional tool renders the unchanged production WPF views with invented
quota, wallets and daily history. It never instantiates the application startup
or adapters. HTTP is blocked. Settings uses an empty output-local store and a
random nonexistent credential target; startup inspection is disabled. It
renders the window visual tree, never the desktop or other applications.

From the repository root (Windows, .NET SDK):

```powershell
dotnet run --project tools/QuotaMonitor.Showcase -c Release -- . publish/showcase
```

Output includes transparent PNGs and numbered frames of the real WPF morph.
The public artwork in `docs/media` adds a decorative backdrop, English labels
and a permanent **SYNTHETIC DEMO DATA** marker. It is a presentation of the
existing Chinese application UI, not an English UI translation or a desktop
performance benchmark. See [media provenance](../../docs/media/PROVENANCE.md).
