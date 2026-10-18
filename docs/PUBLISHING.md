# Publishing

Repository: [asdstrind/QuotaMonitor](https://github.com/asdstrind/QuotaMonitor).
Current release: **v1.6.4**, with **DSH Balance Bridge 1.2.0**.

1. Fetch the remote and integrate updates without overwriting local work or rewriting history.
2. Review the exact source manifest and diff. Keep credentials, account stores, real usage history, local reports, evidence, SDK and build outputs out of Git.
3. Build and run the .NET and Bridge regressions from an isolated source copy. Check the application binaries against the accepted build.
4. Package the Windows x64 runtime, Bridge, public instructions and full licenses with `tools/package-release.ps1`. Verify every ZIP entry and its SHA-256 sidecar.
5. Use repository-local privacy-safe Git identity. Push the reviewed commit without force, then an annotated version tag on that commit.
6. Create a non-draft, non-prerelease GitHub Release with the Windows ZIP. Preserve existing releases; GitHub source archives do not replace the runnable ZIP.
7. Verify public visibility, main/tag targets, latest redirect, README links/media, downloaded asset hash and final working-tree status.

The maintainer must authorize publication. See [third-party marks](../TRADEMARKS.md) for the retained logo permission boundaries; a disclaimer does not grant redistribution rights.
