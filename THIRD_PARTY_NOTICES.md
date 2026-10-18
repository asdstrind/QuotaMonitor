# Third-party licenses and notices

QuotaMonitor v1.6.4 and Balance Bridge 1.2.0 use the project [MIT license](LICENSE).
Third-party software keeps its own terms. Third-party logos, names and marks
are excluded from the project MIT grant; see [TRADEMARKS.md](TRADEMARKS.md).

## Desktop distribution

| Component | Version | License / retained texts |
| --- | --- | --- |
| ZstdSharp.Port | 0.8.8 | [MIT](licenses/ZstdSharp-MIT.txt), Copyright Oleg Stepanischev; includes ported Zstandard code |
| Zstandard-derived code | upstream 1.5.7 | [BSD-3-Clause](licenses/Zstandard-BSD-3-Clause.txt), Meta Platforms and affiliates |
| .NET Runtime (win-x64) | 10.0.12 | [MIT](licenses/.NET-Runtime-LICENSE.txt) and [full third-party notices](licenses/.NET-Runtime-THIRD-PARTY-NOTICES.txt) |
| .NET Windows Desktop Runtime (win-x64) | 10.0.12 | [MIT](licenses/.NET-WindowsDesktop-LICENSE.txt); retain runtime notices above plus [WPF](licenses/.NET-WPF-THIRD-PARTY-NOTICES.txt) and [WinForms](licenses/.NET-WinForms-THIRD-PARTY-NOTICES.txt) notices |

ZstdSharp.Port is the only product NuGet dependency; its net10-selected target
has no further NuGet dependencies. BSD retention and no-endorsement conditions
apply to the Zstandard-derived code. There is no product dependency requiring
the project's own source to be relicensed under copyleft. Runtime components
remain subject to every applicable term in their full notices. Preserve these
texts in binary redistributions; a link alone is not the binary notice bundle.

The Antigravity history reader uses Windows' inbox `winsqlite3.dll`; no SQLite
binary or new NuGet dependency is redistributed. Protocol research consulted
CodexBar, Tokscale and ccusage (MIT at the inspected revisions). The C# reader
is independently implemented; their source code is not bundled. See
[history boundaries and research sources](docs/ANTIGRAVITY-HISTORY.md).

## Bridge host (not bundled)

The Bridge is original project JavaScript; its peer packages are supplied by
the user's compatible DSH installation: `@deepseek-ai/cordis ~4.0.4`,
`@deepseek-ai/dsh-typert-protocol 0.2.0-rc.2`, and
`@deepseek-ai/dsh-credentials 0.2.0-rc.2`. Their installed metadata declares MIT.
Optional integration tests also use matching registry, gateway, app-boot and
deepseek-account-platform packages. These packages and the official account
service are not copied into the repository or the desktop release.
[DeepSeek Harness license](licenses/DeepSeek-Harness-MIT.txt) is retained for
reference; it does not grant logo/trademark permission.

## Build and tests (not desktop runtime)

| Packages | Version | License |
| --- | --- | --- |
| xunit, core, assert, extensibility.core, extensibility.execution | 2.9.3 | Apache-2.0, .NET Foundation and Contributors |
| xunit.abstractions | 2.0.3 | Apache-2.0 |
| xunit.analyzers | 1.18.0 | Apache-2.0; upstream test utility has a separate MIT attribution |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk, TestPlatform.TestHost, TestPlatform.ObjectModel, CodeCoverage | 17.14.1 | MIT, Microsoft |
| coverlet.collector | 6.0.4 | MIT, coverlet contributors |
| Newtonsoft.Json (transitive test dependency) | 13.0.3 | MIT, James Newton-King |
| .NET SDK | 10.0.401 | MIT and packaged third-party terms |

The [license directory](licenses) includes the complete [Apache-2.0 terms](licenses/Apache-2.0.txt),
upstream attribution texts, MIT texts, and exact SDK/runtime notices. Apache
redistribution requires preserving applicable notices and marking modified
vendor files; no vendor files are modified or shipped here. No separate NOTICE
file was found in the restored test packages. Keep required license/NOTICE files
if redistributing those packages. Original package URLs, commits and hashes of
retained license texts are recorded in [inventory.json](licenses/inventory.json).
The cached ASP.NET runtime inventory entry is not part of this WPF release.

Windows fonts are referenced by installed family name only. No font binaries,
SDK, DSH host, npm dependencies, test runner or NuGet package archive is vendored.
The optional image composition tool uses Pillow (not bundled).

`tools/package-release.ps1` includes this file, LICENSE, TRADEMARKS.md and the
license directory with the desktop package. If dependency/runtime versions change,
refresh the exact license inventory before redistributing the new binaries.
