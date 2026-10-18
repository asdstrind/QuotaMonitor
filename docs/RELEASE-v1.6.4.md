# QuotaMonitor v1.6.4

Windows 11 x64：下载完整 `QuotaMonitor-v1.6.4-win-x64.zip`，解压后运行 `QuotaMonitor.exe`；自带 .NET 运行时，请保留整个目录。更新前从托盘退出旧版。

- 四平台监控：Codex 配额、ClaudeCN 余额、DeepSeek Harness 余额与用量，以及 Antigravity Gemini / Claude-GPT 两项每周限额。
- Antigravity 从本地可识别记录读取真实 Token 历史；历史不由额度百分比推算，缺失记录保持未知。
- 历史热力图、平台显示筛选、聚焦查看与低额度预警。
- 优化数字层级、图例、Settings 配色与键盘焦点，保留稳定的三态 Morph 动画。

DSH 余额需要已登录的兼容官方 Desktop Host 和随包 Balance Bridge 1.2.0；退出官方 Host 后不能继续查询余额。配置见 [Setup](docs/CONFIGURATION.md)，历史边界见 [Antigravity history](docs/ANTIGRAVITY-HISTORY.md)。

这是非官方个人项目，与 OpenAI、Anthropic、DeepSeek、ClaudeCN、Google 无隶属或背书关系。额度和余额以服务商界面为准，本地历史可能不完整。第三方商标与 Logo 不属于项目 MIT 授权，见 [声明](TRADEMARKS.md)。
