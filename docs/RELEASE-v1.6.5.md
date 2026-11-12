# QuotaMonitor v1.6.5

Windows x64：解压完整 `QuotaMonitor-v1.6.5-win-x64.zip` 后运行 `QuotaMonitor.exe`，自带 .NET 运行时，请保留整个目录。升级前从托盘退出旧版。v1.6.4 目录可以保留。

本版仅修复 DSH 兼容性，保留 v1.6.4 的全部视觉设计、Morph、四平台布局、Settings、热力图、Harness 筛选及其他平台功能。

- Bridge 1.3.0：Host 范围 `>=0.2.0-rc.2 <0.3.0-0`，按官方检查器处理预发布版本，不再列举 rc。
- 接收端按 Protocol 1 数据契约校验，不再把 sourceVersion 当 Host 白名单。
- 移除不必要的 Typert/页面余额转发，官方 Host 后台每三分钟查询；API 变化安全失败。
- 账户切换清空旧钱包；失败、断开及过期保留最近有效余额为 Stale，Host 重启自动恢复。
- 加强结构、时间戳、金额和状态验证；日志只记录固定诊断码，不新增 UI。

已链接的 Bridge 更新后正常重启 DSH 即可。复制安装通过官方插件管理器更新本插件。详见 [Bridge 安装与技术边界](bridge/dsh-balance-bridge/README.md) 及 [配置](docs/CONFIGURATION.md)。不能保证未来破坏性 Host API 变化无需更新；0.3 及之后版本需另行评估。

安全边界保持：官方账户服务、当前用户 Named Pipe、不读取认证文件或 Token、不绕过 Host。余额以官方页面为准，退出 Host 后无法查询。本项目非官方，许可证和第三方品牌声明见 LICENSE / THIRD_PARTY_NOTICES.md / TRADEMARKS.md。
