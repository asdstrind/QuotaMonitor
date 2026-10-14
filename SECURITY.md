# 凭证与个人数据边界

- 本应用只管理自己的 Windows generic credential：`QuotaMonitor/ClaudeCN/SystemAccessToken`。不要在配置示例、聊天、Issue 或日志中提供 Token。
- ClaudeCN 请求仅发送到源码固定的 HTTPS origin，应用 HttpClient 禁用重定向；错误不回显认证头或响应正文。
- Codex CLI 自行完成认证；监控器不读取认证字节。短时文件共享保护可能妨碍 CLI 写入，冲突时在 CLI 自身恢复登录。
- DSH Bridge 只在官方 Host 中复用已有账号服务 getBalance() 后台查询；不读取 Token/Cookie、认证配置，不调用登录/退出，不复制认证服务或绕过官方登录生命周期。
- `%LOCALAPPDATA%/QuotaMonitor/` 的 settings、snapshots、history 及备份包含个人信息或财务/使用数据，应按私人数据处理。聚合数据不等于匿名数据。
- `evidence/`、`publish/`、`bin/`、`obj/`、历史日志、数据库及认证材料不得公开。诊断、截图、PDB 和异常路径也需单独检查；`.gitignore` 只是隔离规则，不能替代审查，强制添加会绕过它。
- 安全问题报告仅提供文件位置、问题类别、影响与脱敏步骤；不要公开原始响应、真实余额或会话。仓库建立后，维护者应另外决定私人安全报告渠道。

配置通过 Settings 完成。源码示例默认没有账号；测试里的固定 ID、钱包金额和 `test-only` 均为合成数据，不可用于真实认证。
