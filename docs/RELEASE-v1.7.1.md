# QuotaMonitor v1.7.1 — Windows 11 x64

**A lightweight Windows dynamic island for monitoring AI coding quotas and token usage.**

QuotaMonitor v1.7.1 is a stable patch release for the v1.7.0 feature set. The only product change is one compact UI label in the low-quota alert capsule; providers, quota logic, token history, data sources and privacy behaviour are unchanged.

**Download:** [`QuotaMonitor-v1.7.1-win-x64.zip`](https://github.com/asdstrind/QuotaMonitor/releases/tag/v1.7.1) · **Setup:** [configuration guide](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/docs/CONFIGURATION.md) · **Project:** [README](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/README.md)

## Fixed

- **OpenCode Go monthly alert label.** Fixed the OpenCode Go monthly quota alert label from “月” to “1mo” for consistency with other compact quota indicators. In the Alert Minimal capsule the OpenCode Go monthly slot now reads `1mo 6%` instead of `月 6%`, matching the `5h` and `7d` compact labels used by the other OpenCode Go windows.

## Unchanged

- The OpenCode Go `5h` and `7d` compact labels, the Codex labels and the DSH wallet currency display (`CNY` / `USD`) are unchanged.
- Thresholds, colours, trigger conditions, freshness rules and alert behaviour are unchanged.
- The Expanded view keeps its Chinese window names `5 小时` / `每周` / `每月`; Settings and all other application copy are unchanged.
- Everything else from v1.7.0 — the four providers, quota and balance monitoring, the 26-week token analytics, the island states and the local-first privacy model — is unchanged. The public artwork shows the same UI with the corrected label.

## Installation

1. Download `QuotaMonitor-v1.7.1-win-x64.zip` from [Releases](https://github.com/asdstrind/QuotaMonitor/releases/latest).
2. Unzip the **whole folder** and keep the files together. Do not run the executable from inside the archive.
3. Run `QuotaMonitor.exe`. The .NET runtime is bundled — no installer and no administrator rights are required.
4. If SmartScreen warns about an unsigned app, choose **More info → Run anyway**. Release assets include `QuotaMonitor-v1.7.1-win-x64.zip` and `QuotaMonitor-v1.7.1-win-x64.zip.sha256`; verify the hash before running:

   ```powershell
   Get-FileHash .\QuotaMonitor-v1.7.1-win-x64.zip -Algorithm SHA256
   ```

   The printed value must equal the single SHA-256 digest in the `.sha256` sidecar.
5. **Upgrading from v1.7.0 or earlier:** right-click the tray icon and choose **Exit** before replacing the folder. Existing settings and history are preserved.

Requires **Windows 11 x64**. The app uses the system default network configuration.

## Included in the archive

- `README.md` — this release note
- `LICENSE`, `THIRD_PARTY_NOTICES.md`, `TRADEMARKS.md`, `licenses/`
- `docs/CONFIGURATION.md`, `docs/OPENCODE-GO-TOKENS.md`, `docs/ANTIGRAVITY-HISTORY.md`
- `bridge/dsh-balance-bridge/` — optional DSH Balance Bridge 1.3.0 source and license

The archive contains no accounts, API keys, settings, real usage data or local evidence.

## Verification scope

Built as a self-contained Windows x64 release from the tagged source. The complete automated xUnit test suite passed (246 tests, 0 failures), and the packaged build was checked with the project's existing production UI verification command (`QuotaMonitor.exe --verify-v170-ui`): 891 checks passed, 0 failed. Real Windows sign-out/sign-in for start-with-Windows, mixed-DPI multi-monitor setups, display hot-plugging and exclusive-fullscreen games are not covered by this release's checks.

## License and credits

QuotaMonitor is released under the [MIT License](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/LICENSE). Third-party names, logos and brand assets are excluded from that grant and remain with their respective rights holders — see [TRADEMARKS.md](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/TRADEMARKS.md). Third-party components and retained license texts are listed in [THIRD_PARTY_NOTICES.md](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/THIRD_PARTY_NOTICES.md). QuotaMonitor is an independent personal project and has no official sponsorship, endorsement or affiliation with OpenAI, DeepSeek, Google or OpenCode.

---

# QuotaMonitor v1.7.1 — 中文发布说明

**一款轻量的 Windows 灵动岛，实时查看 AI 编程工具的额度与 Token 用量。**

QuotaMonitor v1.7.1 是 v1.7.0 功能集上的稳定补丁版。唯一的产品变更是低额度预警胶囊中的一处紧凑标签；平台、额度逻辑、Token 历史、数据来源与隐私行为均未变化。

**下载：** [`QuotaMonitor-v1.7.1-win-x64.zip`](https://github.com/asdstrind/QuotaMonitor/releases/tag/v1.7.1) · **配置：** [配置指南](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/docs/CONFIGURATION.md) · **项目：** [README](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/README.zh-CN.md)

## 修复

- **OpenCode Go 月额度提醒标签。** 修复 OpenCode Go 月额度预警标签：「月」改为「1mo」，与其他紧凑额度指示风格一致。提醒极简胶囊中的 OpenCode Go 月额度槽位现在显示 `1mo 6%`，而不是 `月 6%`，与 OpenCode Go 另外两个窗口使用的 `5h`、`7d` 紧凑标签保持一致。

## 未变化

- OpenCode Go 的 `5h`、`7d` 紧凑标签、Codex 标签以及 DSH 钱包币种显示（`CNY` / `USD`）均未变化。
- 阈值、颜色、触发条件、新鲜度规则与提醒行为均未变化。
- 展开态仍保留中文窗口名 `5 小时` / `每周` / `每月`；设置页与其他应用文案均未变化。
- v1.7.0 的其余内容——四个平台、额度与余额监控、26 周 Token 分析、灵动岛各状态与本地优先的隐私模型——全部不变。公开界面素材展示的是同一界面，只是标签已修正。

## 安装

1. 从 [Releases](https://github.com/asdstrind/QuotaMonitor/releases/latest) 下载 `QuotaMonitor-v1.7.1-win-x64.zip`。
2. 解压**完整目录**并保持文件在一起，不要压缩包内直接运行。
3. 运行 `QuotaMonitor.exe`。.NET 运行时已包含，无需安装器，也不需要管理员权限。
4. 若 SmartScreen 提示未签名的应用，选择**更多信息 → 仍要运行**。Release 资产包含 `QuotaMonitor-v1.7.1-win-x64.zip` 与 `QuotaMonitor-v1.7.1-win-x64.zip.sha256`，运行前请先核对哈希：

   ```powershell
   Get-FileHash .\QuotaMonitor-v1.7.1-win-x64.zip -Algorithm SHA256
   ```

   输出值必须与 `.sha256` 校验文件中的唯一 SHA-256 摘要一致。
5. **从 v1.7.0 或更早版本升级：** 先右键托盘图标选择**退出**，再替换目录。既有设置与历史会保留。

系统要求：**Windows 11 x64**。应用使用系统默认网络配置。

## 压缩包内容

- `README.md` —— 本发布说明
- `LICENSE`、`THIRD_PARTY_NOTICES.md`、`TRADEMARKS.md`、`licenses/`
- `docs/CONFIGURATION.md`、`docs/OPENCODE-GO-TOKENS.md`、`docs/ANTIGRAVITY-HISTORY.md`
- `bridge/dsh-balance-bridge/` —— 可选的 DSH Balance Bridge 1.3.0 源码与许可证

压缩包不包含任何账户、API Key、设置、真实用量数据或本机证据。

## 验证范围

以标签对应的源码构建为 Windows x64 自包含发布版。完整的 xUnit 自动化测试全部通过（246 个测试，0 失败）；打包后的应用还通过了项目既有的生产界面验证命令（`QuotaMonitor.exe --verify-v170-ui`）：891 项检查通过，0 失败。真实 Windows 注销/登录后的开机启动、混合 DPI 多显示器、显示器热插拔与独占全屏游戏不在本次发布的检查范围内。

## 许可证与致谢

QuotaMonitor 以 [MIT 许可证](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/LICENSE)发布。第三方名称、Logo 与品牌素材不在该授权范围内，权利归各自持有人所有——见 [TRADEMARKS.md](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/TRADEMARKS.md)。第三方组件与保留的许可证文本见 [THIRD_PARTY_NOTICES.md](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.1/THIRD_PARTY_NOTICES.md)。QuotaMonitor 是独立个人项目，与 OpenAI、DeepSeek、Google、OpenCode 均无官方赞助、背书或隶属关系。
