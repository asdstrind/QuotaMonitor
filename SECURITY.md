# 凭证与个人数据边界 / Security & Privacy

QuotaMonitor v1.7.1 只管理自己的数据：唯一的密钥是 OpenCode Go 的 Console 只读 API Key，保存在 Windows 凭据管理器；其余连接复用官方客户端的登录或本地服务。所有个人数据保存在本机，不上传，也没有埋点。

**English summary:** QuotaMonitor stores exactly one secret — your OpenCode Go Console read-only API key — as the Windows Credential Manager generic credential `QuotaMonitor/OpenCodeGo/ConsoleReadOnlyApiKey`. No credentials are bundled. Settings, balance snapshots and daily token aggregates live under `%LOCALAPPDATA%\QuotaMonitor`; they contain no tokens but are still personal data. There is no telemetry, no account and no cloud relay, and the public screenshots use synthetic demo data only.

## 凭证 / Credentials

- 本应用只管理自己的 Windows generic credential：`QuotaMonitor/OpenCodeGo/ConsoleReadOnlyApiKey`。新 Key 只在保存设置时写入，移除也在保存后生效；留空保存表示保留现有 Key。
- OpenCode Go 请求仅发送到源码固定的 HTTPS origin（`opencode.ai`），应用 HttpClient 禁用重定向；错误不回显认证头或响应正文，原始 CSV、身份字段、用户名与散列行身份均不持久化。
- Codex CLI 自行完成认证；监控器不读取认证字节。读取额度期间只对既有配置/认证文件持有短时只读共享句柄，结束后立即释放；这短暂限制其他进程写入是已知权衡，冲突时请在 Codex 自身恢复登录。
- DSH Balance Bridge 只在官方 Host 中复用已有账号服务 `getBalance()` 后台查询；不读取 Token/Cookie、认证配置，不调用登录/退出，不复制认证服务或绕过官方登录生命周期。命名管道为 CurrentUserOnly，仅当前用户可连接，但它不认证同一用户下的任意进程就是官方 DSH。
- Antigravity 只连接当前用户本机回环的 Language Server，并以 READONLY + `query_only` 打开 SQLite；不读取账号文件、Cookie、认证状态库或对话内容。

## 个人数据 / Personal data

- `%LOCALAPPDATA%/QuotaMonitor/` 下的 `settings.json`、`snapshots.json`、`history.json` 及 `settings.json.bak`、`startup-health.json` 可能包含 User ID、私人路径、余额、消费与 Token 聚合，应按私人数据处理。聚合数据不等于匿名数据。应用不保存 Authorization、Cookie、OAuth token、原始 HTTP 错误正文或 Session 文本；历史文件只含每日聚合计数。
- 删除上述目录并移除凭据项即可清除本机数据；卸载前请先在设置中关闭开机启动并从托盘退出。
- `evidence/`、`publish/`、`bin/`、`obj/`、历史日志、数据库及认证材料不得公开。诊断、截图、PDB 和异常路径也需单独检查；`.gitignore` 只是隔离规则，不能替代审查，强制添加会绕过它。
- 本仓库 `docs/media/` 中的公开截图与动画全部由生产界面配合合成数据渲染，不包含真实账户、余额、Key、路径或本机历史。

## 报告安全问题 / Reporting

- 安全问题报告仅提供文件位置、问题类别、影响与脱敏步骤；不要公开原始响应、真实余额、API Key、Session 文件或账户标识。
- 请勿在 Issue、聊天、日志或配置示例中提供任何 Token。仓库建立后，维护者应另外决定私人安全报告渠道。

配置通过 Settings 完成。源码示例默认没有账号；测试里的固定 ID、钱包金额和 `test-only` 均为合成数据，不可用于真实认证。完整配置与运行边界见 [docs/CONFIGURATION.md](docs/CONFIGURATION.md)。
