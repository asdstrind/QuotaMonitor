# 配置与运行边界 / Setup

所有账户登录由用户在对应 Harness 或服务自身完成。源码不预置 Token、User ID、余额或使用历史。未安装、未配置或协议不兼容时显示配置提示或不可用；可在 Settings 单独关闭平台。

### Codex

安装并在 Codex CLI 中完成自己的登录，将原生 `codex.exe` 放入 PATH，或在 Settings 指定自己的可执行文件。Adapter 启动短生命周期 `app-server`，只发送初始化和 `account/rateLimits/read`；不发起模型请求。窗口长度、额度与重置时间由服务端响应决定，不保证任何账户都有 5h/7d 两个窗口。

读取期间 `CodexReadGuard` 对已有 CLI 配置/认证文件持有短时只读共享句柄，不读取凭证字节，但可能阻止其他进程写入。若登录刷新或配置修改冲突，请在 Codex 自身恢复认证后重试。历史热力图只解析本机 rollout 的用量字段，不保留对话正文。

### ClaudeCN

ClaudeCN 是**第三方服务**，不是 Anthropic 官方账户接口。当前 Adapter 固定访问 `https://claudecn.top` 的 `/api/user/self`、`/api/log/self/stat` 与 `/api/log/self`；依赖该服务的可用性、接口和账户权限，不跟随重定向。

在该服务生成自己的 System Access Token，在 Settings 的 ClaudeCN 页输入 Token 与数字 User ID，测试连接后保存。Token 存入本应用 Windows 凭据项 `QuotaMonitor/ClaudeCN/SystemAccessToken`，不写入 JSON。空 Token 保留已存凭据；移除需保存才生效。默认 `Quota / USD` 为 500000，实际换算规则以账户服务为准。

### DSH 与 Balance Bridge

DSH Adapter 默认读取当前用户 `.dsh/sessions`，可在 Settings 更改自己的目录。统计只涵盖本机已记录的 DeepSeek 路由请求；历史不足不补零。

官方余额需要用户自行安装兼容 DSH Desktop、完成登录并安装本仓库的 [Balance Bridge](../bridge/dsh-balance-bridge/README.md)。当前适配目标是 **Desktop 0.2.0-rc.2 / Cordis 4.0.4**。Bridge 在 Host 中复用官方 deepseekAccount.getBalance()：启动后立即查询，成功后每 3 分钟后台刷新，经原有当前用户 Named Pipe 转发，不依赖余额页面、不读取认证文件、不调用登录/退出接口。查询失败保留最近有效余额并标记 Stale；账号变更清空旧余额。彻底退出 DSH 后 Host 停止，不能继续查询。没有 Bridge 时本地 Token 统计仍可使用；超过 4 分钟没有新余额时标记 Stale。

DSH 的模型与价格估算不是官方账单；默认价格依据是源码中的固定日期快照，可关闭或覆盖。服务或客户端升级后须重新验证，失败不回退到私有认证接口。

## 数据与凭证安全

本应用设置、余额快照与每日历史保存在 `%LOCALAPPDATA%/QuotaMonitor/`。这些文件虽然不含 Token，仍可能含 User ID、私人路径、余额、消费与 Token 聚合，属于个人数据；备份、报错或截图前需脱敏。详细边界见 [SECURITY.md](../SECURITY.md)。

不要把任何 Harness 认证文件、Cookie、Session、日志、数据库或真实截图复制到仓库。诊断与 `--smoke-test` 等真实数据验证会产生个人信息，只供本地使用。本仓库公开展示资源只使用明确标注的模拟数据，不包含真实截图。

## 限制与免责声明

本项目与 OpenAI、Anthropic、DeepSeek、ClaudeCN 无隶属或背书关系。额度依赖接口版本；本地历史不代表账户全历史，估算费用不代表账单，余额可能过期。请以服务商官方页面为准。后台使用系统默认网络配置，部署者应自行核对代理环境。

当前源码版本为 v1.5.3，Bridge 为 1.1.0。项目源码及原创资源采用 [MIT](../LICENSE)；第三方 Logo 不在 MIT 授权范围内，授权状态见 [TRADEMARKS.md](../TRADEMARKS.md)。
