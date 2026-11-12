# QuotaMonitor v1.7.0 — Windows 11 x64

**A lightweight Windows dynamic island for monitoring AI coding quotas and token usage.**

QuotaMonitor v1.7.0 is a stable desktop release. It monitors four providers — **Codex**, **DSH (DeepSeek Harness)**, **Antigravity** and **OpenCode Go** — from one always-on-top island with quota windows, balances, reset countdowns and a 26-week token heatmap.

**Download:** [`QuotaMonitor-v1.7.0-win-x64.zip`](https://github.com/asdstrind/QuotaMonitor/releases/latest) · **Setup:** [configuration guide](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/docs/CONFIGURATION.md) · **Project:** [README](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/README.md)

## Highlights

- **Four providers in one island.** Codex, DSH, Antigravity and OpenCode Go appear side by side, and each can be hidden without losing its connection or history.
- **OpenCode Go support.** Server-provided rolling 5-hour, weekly and monthly quota windows with reset times, a collapsed view that shows the limiting window using the official allowance proportions, and token history from the Console's 90-day export, filtered to `provider=opencode-go`. Its read-only API key is stored separately in Windows Credential Manager.
- **Antigravity keeps both weekly pools.** Gemini and Claude/GPT quotas remain available. The former ClaudeCN integration was removed; Antigravity's Claude/GPT pool is unrelated to it.
- **Token analytics for four sources.** The 26-week heatmap, date tooltip and per-provider cumulative totals work across the new provider set. Totals are the verifiable history actually saved for each source — not account lifetime usage.
- **Calmer alerts.** Independent low-quota thresholds for each provider and window; the minimal strip becomes a small alert capsule only when fresh data is verifiably below a threshold. A single icon-and-value slot is used per affected provider.
- **Windows desktop polish.** Top-center mode now locks the island before a drag begins; free mode persists the exact dragged position and is restored after re-centering. Start-with-Windows verifies the current executable path when saving and repairs a stale path after the folder is moved.
- **Cleaner tooltips.** Date tooltips now wait until the island's morph settles before appearing, so they never flash clipped during expansion.
- **Preserved fixes.** The date-tooltip clipping work from v1.6.6 and the DSH Balance Bridge 1.3.0 compatibility work from v1.6.5 are included.

## Installation

1. Download `QuotaMonitor-v1.7.0-win-x64.zip` from [Releases](https://github.com/asdstrind/QuotaMonitor/releases/latest).
2. Unzip the **whole folder** and keep the files together. Do not run the executable from inside the archive.
3. Run `QuotaMonitor.exe`. The .NET runtime is bundled — no installer and no administrator rights are required.
4. If SmartScreen warns about an unsigned app, choose **More info → Run anyway**. Release assets include `QuotaMonitor-v1.7.0-win-x64.zip` and `QuotaMonitor-v1.7.0-win-x64.zip.sha256`; verify the hash before running.
5. **Upgrading from an earlier version:** right-click the tray icon and choose **Exit** before replacing the folder. Existing settings and history are preserved; data from removed platforms is left in place but is not displayed.

Requires **Windows 11 x64**. The app uses the system default network configuration.

## Included in the archive

- `README.md` — this release note
- `LICENSE`, `THIRD_PARTY_NOTICES.md`, `TRADEMARKS.md`, `licenses/`
- `docs/CONFIGURATION.md`, `docs/OPENCODE-GO-TOKENS.md`, `docs/ANTIGRAVITY-HISTORY.md`
- `bridge/dsh-balance-bridge/` — optional DSH Balance Bridge 1.3.0 source and license

The archive contains no accounts, API keys, settings, real usage data or local evidence.

## Data sources and limits

- **Quota and balance values** come from each service's own response: Codex app-server, the official DeepSeek account service via the Balance Bridge, Antigravity's local Language Server, and the OpenCode Go quota API. A failing or outdated read is shown as *stale* or *unavailable*; nothing is filled in as `0%`.
- **Token history** is local (Codex rollout files, DSH sessions, Antigravity's read-only local history) or exported by the provider (OpenCode Go Console CSV, 90 days). It is bounded, gaps stay unknown, and it is never used to infer quota.
- **Billing.** The DSH DeepSeek balance and the OpenCode Go subscription are independent billing sources and are displayed separately. DSH cost figures are estimates from a dated price snapshot, not a bill.

## Verification scope

Tested on **Windows 11 x64** with a self-contained build. Desktop checks ran natively at 150% scaling; 100% and 200% scaling were validated as geometry and WPF raster checks only. Real Windows sign-out/sign-in for start-with-Windows, mixed-DPI multi-monitor setups, display hot-plugging and exclusive-fullscreen games are not covered by this release's checks.

## License and credits

QuotaMonitor is released under the [MIT License](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/LICENSE). Third-party names, logos and brand assets are excluded from that grant and remain with their respective rights holders — see [TRADEMARKS.md](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/TRADEMARKS.md). Third-party components and retained license texts are listed in [THIRD_PARTY_NOTICES.md](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/THIRD_PARTY_NOTICES.md). QuotaMonitor is an independent personal project and has no official sponsorship, endorsement or affiliation with OpenAI, DeepSeek, Google or OpenCode.

---

# QuotaMonitor v1.7.0 — 中文发布说明

**一款轻量的 Windows 灵动岛，实时查看 AI 编程工具的额度与 Token 用量。**

QuotaMonitor v1.7.0 为稳定版桌面发布。它在一枚始终置顶的灵动岛中监控四个平台——**Codex**、**DSH（DeepSeek Harness）**、**Antigravity** 与 **OpenCode Go**——提供额度窗口、余额、重置倒计时与 26 周 Token 热力图。

**下载：** [`QuotaMonitor-v1.7.0-win-x64.zip`](https://github.com/asdstrind/QuotaMonitor/releases/latest) · **配置：** [配置指南](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/docs/CONFIGURATION.md) · **项目：** [README](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/README.md)

## 更新内容

- **四个平台，一枚灵动岛。** Codex、DSH、Antigravity 与 OpenCode Go 并排显示；隐藏某个平台只影响展示，不会删除连接配置或历史。
- **新增 OpenCode Go 支持。** 展示服务端返回的滚动 5 小时、每周、每月额度窗口及重置时间；收起态按官方额度比例选出最受限窗口并显示其自身百分比；Token 历史来自 Console 的 90 天导出，仅筛选 `provider=opencode-go`。只读 API Key 独立保存在 Windows 凭据管理器。
- **Antigravity 保留两个每周额度池。** Gemini 与 Claude/GPT 额度继续可用；旧版的 ClaudeCN 集成已移除，Antigravity 的 Claude/GPT 池与它无关。
- **四个来源的 Token 分析。** 26 周热力图、日期 Tooltip 与各平台累计均已适配新的平台组合。累计值是各来源实际保存、可核实的历史，不是账户终身用量。
- **更克制的提醒。** 每个平台、每个窗口可独立设置低额度阈值；只有在数据足够新鲜且确实低于阈值时，极简条才会变为小胶囊，每个受影响平台只占一个「图标 + 数值」槽位。
- **Windows 桌面体验改进。** 顶部居中模式在拖动开始前即锁定位置；自由模式会保存拖动后的精确位置，重新居中后仍可恢复。开机启动在保存时核对当前可执行文件路径，并在目录移动后修复旧路径。
- **更干净的 Tooltip。** 日期 Tooltip 会等待灵动岛形变结束后再出现，展开过程中不再出现被裁切的瞬帧。
- **保留既有修复。** 包含 v1.6.6 的日期 Tooltip 裁剪修复与 v1.6.5 的 DSH Balance Bridge 1.3.0 兼容性修复。

## 安装

1. 从 [Releases](https://github.com/asdstrind/QuotaMonitor/releases/latest) 下载 `QuotaMonitor-v1.7.0-win-x64.zip`。
2. 解压**完整目录**并保持文件在一起，不要压缩包内直接运行。
3. 运行 `QuotaMonitor.exe`。.NET 运行时已包含，无需安装器，也不需要管理员权限。
4. 若 SmartScreen 提示未签名的应用，选择**更多信息 → 仍要运行**。Release 资产包含 `QuotaMonitor-v1.7.0-win-x64.zip` 与 `QuotaMonitor-v1.7.0-win-x64.zip.sha256`，运行前请先核对哈希。
5. **从旧版本升级：** 先右键托盘图标选择**退出**，再替换目录。既有设置与历史会保留；已移除平台的数据原样留存，但不再展示。

系统要求：**Windows 11 x64**。应用使用系统默认网络配置。

## 压缩包内容

- `README.md` —— 本发布说明
- `LICENSE`、`THIRD_PARTY_NOTICES.md`、`TRADEMARKS.md`、`licenses/`
- `docs/CONFIGURATION.md`、`docs/OPENCODE-GO-TOKENS.md`、`docs/ANTIGRAVITY-HISTORY.md`
- `bridge/dsh-balance-bridge/` —— 可选的 DSH Balance Bridge 1.3.0 源码与许可证

压缩包不包含任何账户、API Key、设置、真实用量数据或本机证据。

## 数据来源与边界

- **额度与余额**来自各服务自身的返回：Codex app-server、经 Balance Bridge 转发的官方 DeepSeek 账户服务、Antigravity 本地 Language Server，以及 OpenCode Go 额度接口。读取失败或过期时显示**过期**或**不可用**，绝不补成 `0%`。
- **Token 历史**来自本机记录（Codex rollout、DSH Session、Antigravity 只读本地历史）或服务端导出（OpenCode Go Console CSV，90 天）。它是有边界的，缺口保持未知，也绝不用于反推额度。
- **计费。** DSH 的 DeepSeek 余额与 OpenCode Go 订阅是两个独立的计费来源，分开展示。DSH 的费用数字是固定日期价格快照的估算，不是账单。

## 验证范围

在 **Windows 11 x64** 的自包含构建上验证。桌面实测使用 150% 缩放；100% 与 200% 缩放仅完成几何与 WPF 光栅验证。真实 Windows 注销/登录后的开机启动、混合 DPI 多显示器、显示器热插拔与独占全屏游戏不在本次发布的检查范围内。

## 许可证与致谢

QuotaMonitor 以 [MIT 许可证](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/LICENSE)发布。第三方名称、Logo 与品牌素材不在该授权范围内，权利归各自持有人所有——见 [TRADEMARKS.md](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/TRADEMARKS.md)。第三方组件与保留的许可证文本见 [THIRD_PARTY_NOTICES.md](https://github.com/asdstrind/QuotaMonitor/blob/v1.7.0/THIRD_PARTY_NOTICES.md)。QuotaMonitor 是独立个人项目，与 OpenAI、DeepSeek、Google、OpenCode 均无官方赞助、背书或隶属关系。
