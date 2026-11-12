# 配置与运行边界 / Setup

所有账户登录都由你在对应的 Harness 或服务自身完成。源码不预置 Token、User ID、余额或使用历史。未安装、未配置或协议不兼容时，应用显示配置提示或不可用；可以在 Settings 中单独关闭平台。

## Settings 页面 / Settings pages

Settings 可从展开态右上角的齿轮按钮、托盘菜单，或未选择平台时出现的齿轮按钮打开。**Esc** 关闭，**Ctrl+S** 保存；所有更改在保存后生效。

| 页面 | 可配置内容 |
| --- | --- |
| 常规 / General | 「显示哪些 Harness」四个勾选框；「顶部居中」；「全屏时隐藏」；「开机启动」 |
| 平台 / Providers | 按平台切换页签：启用开关；Codex 自动发现说明；OpenCode Go Console 只读 API Key、测试连接、移除已存 Token；DSH 与 Antigravity 的连接说明 |
| 提醒 / Alerts | Codex 5 小时 / 每周；OpenCode Go 5 小时 / 每周 / 每月；DSH 充值余额与币种（CNY / USD）；Antigravity Gemini / Claude/GPT；阈值填 `0` 关闭对应提醒 |
| 高级 / Advanced | 各平台刷新频率（秒）；路径覆盖（Codex 原生 `.exe`、DSH Session 目录，留空自动发现）；DSH Estimated Cost 开关与可选的模型/价格覆盖 |

「常规」中的显示勾选只影响展示；「平台」中的启用开关决定是否读取该平台。隐藏平台不会删除连接配置；读取失败的状态会在展开态表头、收起态胶囊与 Tooltip 中如实标注。

## 各平台 / Providers

### Codex

安装并在 Codex CLI 中完成自己的登录。应用默认从 `PATH` 自动发现原生 `codex.exe`，只有自定义安装才需要在「高级 → 路径覆盖」中指定可执行文件。

Adapter 启动短生命周期 `app-server`，只发送初始化和 `account/rateLimits/read`，不发起模型请求。窗口长度、额度与重置时间由服务端响应决定，不保证任何账户都有 5 小时和 7 天两个窗口。收起态下，若所选额度池中确有未重置的耗尽窗口，会显示 `0%`；不会按时长或原始百分比猜测两个窗口的额度关系。

读取期间 `CodexReadGuard` 对已有 CLI 配置/认证文件持有短时只读共享句柄，不读取凭证字节，但可能阻止其他进程写入。若登录刷新或配置修改冲突，请在 Codex 自身恢复认证后重试。历史热力图只解析本机 rollout 的用量字段，不保留对话正文。

### DSH 与 Balance Bridge

DSH Adapter 默认读取当前用户 `.dsh/sessions`，可在「高级 → 路径覆盖」中更改；该目录暂不存在时仍可保存。统计只涵盖本机已记录的 DeepSeek 路由请求；历史不足不补零。

官方余额需要你自行安装兼容 DSH Desktop、完成登录并安装本仓库的 [Balance Bridge](../bridge/dsh-balance-bridge/README.md)。Host 声明范围是 **>=0.2.0-rc.2 <0.3.0-0 / Cordis ~4.0.4**，Bridge 为 **1.3.0**；实际 rc.5 与未来版本模拟测试的边界见 Bridge README。

Bridge 在 Host 中复用官方 `deepseekAccount.getBalance()`：启动后立即查询，成功后每 3 分钟后台刷新，经原有当前用户 Named Pipe 转发；不依赖余额页面、不读取认证文件、不调用登录/退出接口。充值钱包与赠金钱包按原始币种分别显示，不跨币种求和。查询失败保留最近有效余额并标记 Stale；账号变更清空旧余额；超过 4 分钟没有新余额也标记 Stale。彻底退出 DSH 后 Host 停止，不能继续查询。没有 Bridge 时本地 Token 统计仍可使用。

「高级」中可关闭 Estimated Cost，或覆盖模型与价格。费用只在模型与价格依据匹配时估算，始终标记 Estimated；DSH 的模型与价格估算不是官方账单，默认价格依据是源码中的固定日期快照。服务或客户端升级后须重新验证，失败不回退到私有认证接口。

### Antigravity

打开并登录官方 Antigravity 客户端。应用只连接当前 Windows 用户的本地 Language Server（本机回环端口），分别显示 Gemini 和 Claude/GPT 两项 Weekly 额度池；这是一个每周窗口，**不是**两周周期。接口不兼容、多会话无法确认归属或未登录时显示不可用，不推算剩余额度。

Token 历史由独立只读通道解析本机可识别的 SQLite / JSONL 用量字段，不读取对话正文，也不将额度变化换算成 Token。缺失或无法核验的记录保留未知。默认目录、环境变量和格式边界见 [ANTIGRAVITY-HISTORY.md](ANTIGRAVITY-HISTORY.md)。

### OpenCode Go

在 Settings 的 OpenCode Go 页保存自己的 OpenCode Console 只读 API Key。新凭据独立保存在 Windows 凭据管理器 `QuotaMonitor/OpenCodeGo/ConsoleReadOnlyApiKey`，不会读取或迁移旧平台的凭据。输入框留空保存即保留现有 Key；移除在保存后生效。「测试连接」会同时校验额度接口与 Token 导出。

应用直接请求 OpenCode 服务端，无需 DSH 运行：`GET /zen/go/v1/usage` 显示 5 小时、每周、每月的剩余额度（`100 - 已用百分比`）；`GET /console/api/v2/usage/export?range=90d` 只统计 `provider=opencode-go`。额度默认 180 秒刷新（可设 180–3600 秒），历史最多每 30 分钟导出一次；强制刷新也遵守冷却与 `Retry-After`。失败保留最近有效值并标记过期，未取得值显示未知。

Go 的历史保留服务端 UTC 日期；Tooltip 标有 UTC，不转换为精确本地日。只显示服务端实际返回的日期，90 天导出不是终身历史。Go 的额度和聚合历史仅保存在运行内存，重启后重新获取，避免凭据切换串用账户缓存。计数、去重、精度、收起态单值选择与已知边界见 [OPENCODE-GO-TOKENS.md](OPENCODE-GO-TOKENS.md)。

## 提醒与刷新 / Alerts and refresh

低额度提醒在以下条件全部满足时才出现：平台已在「常规」中显示、已在「平台」中启用，数据为当前成功读取的新鲜值，且没有已被服务端重置的窗口。剩余额度**严格低于**阈值才提醒，等于阈值不提醒；填 `0` 关闭对应提醒。

- Codex 使用 5 小时 / 每周两个独立阈值，提醒胶囊中只占一个槽位；OpenCode Go 三个窗口独立判断，提醒胶囊只显示最受限的一个；Antigravity 两个 Weekly 池独立判断，在同一个槽位内并列显示；DSH 按所选币种的充值钱包判断，赠金钱包不参与该阈值。
- 数据过期、部分或不可用时不提醒，也不把旧值当作新数据。

刷新按平台独立调度：默认 Codex 180 秒、DSH 90 秒、Antigravity 60 秒、OpenCode Go 180 秒。Codex / DSH / Antigravity 可设 30–3600 秒，OpenCode Go 为 180–3600 秒；手动刷新不会绕过 OpenCode Go 的额度/导出冷却与 `Retry-After`。历史读取与额度刷新相互独立：历史回填较慢不会延迟收起态和展开态，历史失败也不影响实时额度；Codex 历史约 3 分钟、Antigravity / DSH 约 2 分钟、OpenCode Go 30 分钟调度一次，仍有待回溯日期时以更短间隔继续。

热力图固定显示近 26 周（周一为一周起点），每天一个圆角方格。悬停日期可查看各平台当日数值与合计；悬停底部图例中的平台会聚焦该平台的色带。图例累计是当前已保存、可核实的历史，**不是**账户终身总量。

## 显示与桌面行为 / Display and desktop behavior

- 开启顶部居中（默认）时，灵动岛在拖动开始前锁定在顶部中央，点击展开、Hover 和 Settings 不受影响。关闭居中后恢复最后的自由位置；拖动收起胶囊或展开态仪表条即可移动，位置跨重启保存。Settings 窗口可独立拖动。
- 「全屏时隐藏」默认开启：检测到全屏应用时隐藏灵动岛，退出全屏后恢复。
- 关闭灵动岛窗口只会隐藏到通知区域。托盘右键提供刷新、设置、退出；双击托盘图标唤回收起胶囊。升级前请从托盘选择退出。单实例运行，重复启动会直接退出。
- 保存设置会核对并写回当前 EXE 的完整引号路径。已开启自启的应用在移动目录后首次手动运行时修复旧路径；移动后从未运行的新位置无法自行修复注册表。显式开启可恢复本应用的 Windows 启动禁用项；自动路径修复尊重任务管理器的禁用选择。失败直接显示在设置窗口，不保存成功假象。仅操作 QuotaMonitor 自己的注册值。
- 启动初始化、重复实例退出或初始化失败的脱敏状态保存在 `%LOCALAPPDATA%/QuotaMonitor/startup-health.json`，不含命令、账户或 Token。真实登录启动需结合登录后的进程和该回执验证，直接运行不算登录实测。

## 数据与凭证安全 / Data and credential security

本应用设置、余额快照与每日历史分别保存在 `%LOCALAPPDATA%/QuotaMonitor/` 的 `settings.json`、`snapshots.json`、`history.json`（保存时原子替换）。这些文件虽然不含 Token，仍可能含 User ID、私人路径、余额、消费与 Token 聚合，属于个人数据；备份、报错或截图前需脱敏。唯一的密钥是 Windows 凭据管理器中的 `QuotaMonitor/OpenCodeGo/ConsoleReadOnlyApiKey`。详细边界见 [SECURITY.md](../SECURITY.md)。

不要把任何 Harness 认证文件、Cookie、Session、日志、数据库或真实截图复制到仓库。诊断与 `--smoke-test` 等真实数据验证会产生个人信息，只供本地使用。本仓库公开展示资源只使用明确标注的模拟数据，不包含真实截图。

## 限制与免责声明 / Limits and disclaimer

本项目与 OpenAI、Anthropic、DeepSeek、OpenCode、Google 无隶属或背书关系。额度依赖接口版本；本地历史不代表账户全历史，估算费用不代表账单，余额可能过期。请以服务商官方页面为准。后台使用系统默认网络配置，部署者应自行核对代理环境。

当前源码版本为 v1.7.0，Bridge 为 1.3.0。项目源码及原创资源采用 [MIT](../LICENSE)；第三方 Logo 不在 MIT 授权范围内，授权状态见 [TRADEMARKS.md](../TRADEMARKS.md)。
