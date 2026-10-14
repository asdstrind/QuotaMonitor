# 架构与数据来源

## 项目边界

`src/QuotaMonitor` 为 WPF WinExe：Views、ViewModels、Configuration、Security、Services 和 Assets。`src/QuotaMonitor.Core` 提供统一数据模型、模块 Adapter 和刷新协调器。`tests/QuotaMonitor.Tests` 为 xUnit 业务/协议测试。`tools/QuotaMonitor.Diagnostics` 运行只读真实 Adapter，不接受密钥参数、不输出认证或会话正文。

桌面程序只依赖 .NET 内置能力与 `ZstdSharp.Port 0.8.8`。未引入 Electron、MVVM 大框架、SQLite、云服务或遥测。第三方库许可见 `THIRD_PARTY_NOTICES.md`。

## 状态与生命周期

`ProviderSnapshot` 的适用指标使用可空值，状态分为 Loading、Ready、Partial、Unavailable、Disabled，另由 UI 根据 LastUpdated 派生 STALE。统一模型保留来源、估算标记、最后成功更新时间、余额、费用、Token、窗口和错误。失败保留上次成功快照；UI 清楚区分旧值与当前成功读取。Codex quota 与 API 费用分开建模。

RefreshCoordinator 为每个 Adapter 提供独立 single-flight 闸门、周期调度、手动强制刷新、退避及全局退出取消。HttpClient 请求与本地解析在线程/异步任务中执行，Dispatcher 仅更新 UI。退出等待请求和独立 app-server 子进程释放，随后保存不含凭证但仍属个人数据的快照并释放托盘和 HTTP 客户端。

Codex app-server 每次查询短暂启动，stdio JSONL 初始化完成后读取额度。stderr 持续排空但不保留正文；通知不当作响应；按 request id 对齐。stdin 关闭后给予两秒退出时间，再只终止所拥有子进程树。总查询超时 30 秒。没有线程/turn 创建和模型调用。

在早期无保护查询期间观察到 Codex 配置变化；现在 `CodexReadGuard` 仅打开文件读共享句柄，不读取凭证内容、不修改 ACL/属性，也不恢复覆盖文件。保护期间本应用子进程及其他进程无法写入或替换已存在的 config.toml/auth.json。结束立即释放；文件正在写入时返回明确不可用。它也会短暂限制其他进程更新配置或持久化认证，这是当前 MVP 的已知权衡。

## Codex 来源

Codex Adapter 使用 `account/rateLimits/read`。读取 rateLimitsByLimitId，为空时回退 legacy rateLimits；优先 codex bucket，服务端窗口动态呈现。生成 schema 的历史研究资料不随源码分发。

`remaining = clamp(100 - usedPercent, 0, 100)`；保留原始 usedPercent。缺失窗口时为 Unavailable，不填 100%。窗口时长来自 `windowDurationMins`，重置使用 Unix 秒并转为本地显示。

官方文档：[Codex App Server](https://learn.chatgpt.com/docs/app-server)，已读取其初始化及 account/rateLimits/read 定义。

## ClaudeCN 来源和金额口径

ClaudeCN 是第三方服务；只向固定 HTTPS origin `https://claudecn.top` 发送 Bearer 与 New-Api-User，不跟随重定向。可用性和协议由该服务决定。

| 接口 | 数据 | 处理 |
| --- | --- | --- |
| `/api/user/self` | quota、used_quota、billing_preference | 剩余余额和累计使用费；缺失字段不造零 |
| `/api/log/self/stat` | quota、total_requests、total_tokens | type=2 消费日志，当地午夜至现在的 Unix 秒范围 |
| `/api/log/self?p=1&page_size=5&type=2` | model_name、quota、prompt_tokens、completion_tokens、created_at | 支持 data.items / data.logs / data array；排除非消费 type；未知结构诊断 |

金额用 decimal 按可配置 ratio 换算。账户余额读取 3 分钟、今日统计及最近日志 5 分钟缓存；跨日、凭据/UserID/ratio 变化和手动刷新强制重读。余额成功但统计/日志失败为 Partial，错误包含 HTTP 状态，不包含响应正文。认证失败不会回显服务端 message 或 Header。

使用额度换算消费与钱包净扣款不是同一指标。`subscription_first` 会显示结算偏好，日志 `other.billing_source` 存在时显示来源。不将历史金额或余额差额推断为实际账单。

## DSH 来源和统计范围

DSH 当前适配目标为官方 Desktop 0.2.0-rc.2。实现参考公开协议与宿主行为，内部研究提取文件不参与构建，也不随仓库分发。

选择每个 Session 目录最高数字 generation，排除旧格式并列文件；相同 header.id 只计一次。每帧使用 RFC 8878 framing 获取完整边界，由 ZstdSharp 解压并校验；尾部未完成帧排除并警告。单文件最多 256 MiB、单帧最多 64 MiB 解码字符，超限明确不可用，避免无界内存。

解析时立即释放对话/工具内容，仅保留路线、时间和 numeric usage。v2+ 继承边界用最后 `session/end-seed` 或已知 seedLength；继承数据只用于恢复路由，不累计。带无法验证历史 seedLength 的 v0/v1 Session 保守排除并标 Partial。相同 turn/step 结算替换，`llm/retry-started` 分隔新的计费尝试；不累加流中每个 usage delta。只统计 DeepSeek route。

总量为 `inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens`，reasoningTokens 已包含在输出中，不再加一次。保留服务端用量，不用 Token Meter 的 contextPressure 或 heuristic surface estimate 当账单。负数、非整数、总量矛盾和缺失报告不计且警告。文件长度/mtime 未变时使用小型内存统计缓存，重复刷新不累加历史。

本地总量不是账户全历史，也不覆盖未记录的搜索/标题模型调用等辅助请求。今日按 Windows 本地日期分桶。UI 显示最近已记录模型，不声称已读当前运行中的模型设置。

余额由现有官方 Host 插件 Bridge 接入。注入 deepseekAccount，启动后立即调用 getState()/getBalance(client)，成功后每 3 分钟查询；client 仅包含兼容宿主 version、系统 locale 与 timezoneOffsetSeconds。官方服务返回 null、failed 或 ready（value 为充值钱包、bonusWallets 为赠金）。插件不读取认证文件、不获取 Token、不调用登录/退出，不建立第二套认证或 HTTP 查询。官方 401/40003 登录过期行为仍由官方服务管理。

BalancePoller 在 Host 内调度，不依赖客户端 Slot 或页面挂载。单实例单请求，官方最大 120 秒超时外再设 125 秒 watchdog；watchdog 后仍等待官方 promise 结束，避免无法取消的重叠请求，迟到成功不刷新余额。失败退避 30 秒至 15 分钟，成功恢复 3 分钟。HTTP 202 的空/未完成数据由官方服务映射成 failed，不当作零余额。账号记录变更只观察公共事件的记录 key，不读取记录；立即清空旧钱包并递增内存代次，旧请求不能跨账号发布。Host 初次启动也清空无法归属的缓存。

监控器继续持有 CurrentUserOnly + FirstPipeInstance 服务端，DSH 只连接本地 Named Pipe。协议 1 白名单新增 queryStatus 与 stale 状态；stale 包含最近有效钱包，接收器重启也能恢复旧值及 Stale。金额保持原始字符串，普通和赠金分别显示、不跨币种求和。16 KiB 帧上限、3 秒半帧超时、Pipe 连接 3 秒与 1–30 秒重连退避保持不变。旧客户端 Remote 保留兼容但后台服务挂载后拒绝覆盖；客户端模块已从插件 manifest 移除。

余额时间为官方 getBalance 成功完成时间。失败、断线和超过 4 分钟标 Stale，保留最近有效金额；退出或换号清空。Desktop Host 的父子 IPC 随 GUI 主进程退出而 shutdown；兼容安装版本 CLI 拒绝启动 desktop profile，要求 Electron 管理；插件不绕过该限制、不另开 Host、不复制凭据。隐藏窗口而 Host 仍运行时可继续查询，彻底退出后只能 Stale/Unavailable，重启后立即查询。余额新鲜度独立于本地 Session 用量、热力图和监控器 UI 刷新。

默认估算是 2026-10-10 [官方价格](https://api-docs.deepseek.com/quick_start/pricing/)快照：deepseek-flash 每百万 Token 高峰输入未命中 USD 0.30、缓存读取 USD 0.006、输出 USD 1.20。固定高峰单价只用于当前日期已记录请求的情景估算上限；没有低峰/节假日切换、账单确认或币种兑换。缓存写入价格默认未知，非零写入或模型不匹配时不估算；单价及依据均可改。

官方资料：[Token Meter](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/llm/token-meter/README.md)、[Session persistence](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/core/session-persistence-jsonl/README.md)、[账户平台](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/credentials/deepseek-account-platform/README.md)。具体统计口径同时核对本机已安装版本，线上 master 可能继续变化。

## 每日用量历史与热力图

`UsageHistory` / `UsageDay` 是每日聚合模型：日期、Token、请求数、费用、覆盖质量（Exact / Estimated / Partial）、抓取时间、缓存命中 Token、被排除的记录数。`IUsageHistorySource` 与 Adapter 平行存在，`HistoryCoordinator` 独立于 `RefreshCoordinator` 调度，因此历史读取再慢也不会延迟收起态与仪表板，历史失败也不影响实时的额度/余额。

- Codex：`CodexUsageHistory` 只读 `sessions` 与 `archived_sessions` 下的 rollout（明文或多帧 Zstandard）。按行结构性标记（`token_usage_record` / `token_count` / `session_meta`）预筛后才解析 JSON，不保留对话正文。新格式以 `response_id` 去重（同一会话被明文与压缩各存一份时由事件键收敛）；早期格式只有累计计数，首次事件不当作一次结算，之后仅累计值变化才计一次，增量与单次用量不一致时以增量入账并标为推算。明文文件按长度增量续读，未完成的行留待下次；压缩文件视为完整。
- DSH：与收起态共用 `DshAdapter.ScanSessions` 的同一次缓存扫描（锁内），按本地自然日分桶，当日费用沿用同一套估算单价。被排除的记录按其 `time` 归日并计入该日"不完整"，因此缺口是可见的而不是被悄悄抹平。
- Claude：`ClaudeCNAdapter` 实现同一接口，对 `/api/log/self/stat`（`type=2`）逐本地自然日查询，每批上限 24 天。缓存按账户标识（user id 与换算比例）与抓取时间判定：已结束且在该日结束之后抓取过的日期不再重读，当天与未结束的日期会重取；账户或比例变化时整段历史作废，绝不与另一账户的记录混合。

`UsageGrid` 是纯投影：把历史对齐到周一起始的周列，计算两个平台的参考分位（合并的共享刻度、各自的相对刻度）与每格的 `Level = clamp(1 + log10(tokens/reference)/decades, MinLevel, 1)`。视图模型只做格式化（中文万/亿计数、金额拆分、倒计时）与读数卡文本，不含任何 WPF 类型，因此测试项目直接编译它。

`UsageHeatmap` 是自绘 `FrameworkElement`：一天 = 三根胶囊柱，高度为对数刻度，零用量为圆点、无记录为空位、推算为空心胶囊、不完整为角标小点；命中区域比柱体大，悬停/离开事件带出该日数据。展开时用 `Reveal` 属性做一次自左向右的升起动效，且受系统动画设置约束。读数卡是岛内 `Canvas` 上的浮层，不占布局、不引入滚动，刷新后按日期重新关联。

## Windows、存储与安全

透明、无边框、Topmost、ShowInTaskbar=false 的 WPF 工具窗口。视觉为纯黑玻璃、三档墨色与固定平台色（三色经色觉模拟校验）。字体：界面文本 Segoe UI Variable Text、小号 Segoe UI Variable Small（中文回退 Microsoft YaHei UI），数据与数值 Bahnschrift SemiBold，图标 Segoe Fluent Icons / Segoe MDL2 Assets；图标按钮模板内显式指定字体，避免隐式 TextBlock 样式覆盖字体继承。展开高度由内容 `Measure` 得出，超出工作区时整体 `LayoutTransform` 缩放而不产生滚动条。

SettingsStore 原子替换本应用 JSON（settings / snapshots / history）；CredentialStore 使用 Windows generic credential，唯一 Target。`StoreMigration` 只在本应用自己的目录（含被重定向的包 LocalCache）内复制这三个不含凭证但包含个人数据的文件，每文件一次、不覆盖、不读取凭据。不会保存 Authorization、Cookie、OAuth token、原始 HTTP 错误正文或 Session 文本；历史文件只含每日聚合计数。应用只有本地操作和明确平台 origin 网络请求。开机自启只管理本应用 HKCU Run 值，默认不启用。 settings 可含 User ID/私人路径，snapshots 与 history 可含余额、消费和使用统计；不得公开。
