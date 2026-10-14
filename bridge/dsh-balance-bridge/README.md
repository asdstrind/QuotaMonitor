# DSH Balance Bridge 1.1

目标宿主：官方 Windows Desktop 0.2.0-rc.2 / Cordis 4.0.4。兼容宿主提供 peerDependencies；源码位于 lib/ 与 cordis.patch.yml，无需编译或安装脚本。

## 安装与更新

先在 DSH 自身完成官方账号登录，再从本仓库根目录使用兼容的官方 CLI：

```powershell
$bridgeSource = (Resolve-Path ./bridge/dsh-balance-bridge).Path
dsh plugin --profile desktop add $bridgeSource --ignore-scripts
```

已有源码链接安装只需更新源码，在没有运行任务时正常退出并重启 DSH。复制安装应通过官方插件管理器更新至 1.1.0。CLI 不在 PATH 时使用自己的官方安装位置。不要修改认证配置。

## 后台查询

Host 中的现有 Bridge 注入 deepseekAccount，启动后立即调用官方 getState() / getBalance(client)，成功后每 3 分钟查询一次。不依赖 Slot、客户端挂载、余额页面或 UI 刷新，不建立第二套认证或 HTTP 查询。client 仅含兼容宿主版本、系统 locale 与时区偏移。

插件不读取凭据存储、不获取 Token、不调用登录/退出。官方服务负责钱包解析、请求超时和账号生命周期。HTTP 202 只有在官方服务返回完整 ready 钱包时才接受；空响应、未完成或错误响应按失败处理，不当作零余额。官方服务遇到真实 401/40003 时仍按既有逻辑使登录过期；插件不绕过该逻辑，也不反复查询退出账号。

每个插件实例只有一个在途请求。插件 watchdog 为 125 秒，覆盖官方允许的最长 120 秒查询。watchdog 后仍等待官方 promise 结束，避免启动重叠且无法取消的请求；若服务永久不结束，维持 Stale，直到 Host 重启。失败退避为 30、60、120、240、480、900 秒，成功后恢复 3 分钟。

监听官方账号状态及对应 credentials/record-updated 事件，事件仅携带记录 key，不读取记录。变更时立即清空旧余额并递增内存代次，忽略旧请求结果。Host 启动也清空无法归属的旧账号缓存。Host/账号服务卸载时释放定时器和订阅，迟到结果不能发布。

## IPC 与余额保留

官方 Host → 现有当前用户 Named Pipe → QuotaMonitor。协议 1 白名单只包含版本、状态、queryStatus、观察时间、普通/赠送钱包币种与金额字符串；不传账号身份、Token、认证头或配置。新增 stale 消息带最近成功钱包，监控器在查询失败期间重启也能收到旧值及 Stale。旧客户端 Remote 保留兼容，但 Host 启用后台查询后拒绝客户端覆盖。

接收端继续使用 CurrentUserOnly、FirstPipeInstance、16 KiB 帧上限、3 秒半帧超时。管道重连退避 1–30 秒。超过 4 分钟为 Stale。失败不更新时间、不伪造零；退出或换号清空钱包。充值与赠金分别显示，原金额字符串保留，不跨币种求和。

个人快照文件只留在本机应用数据目录，不能提交仓库。

## GUI 与 Host 生命周期

本插件不另行启动 Host。只要官方 Host 运行，窗口隐藏和停留聊天页都不影响查询。Desktop Host 由 GUI 主进程父子 IPC 管理，父进程断开会 shutdown。兼容版本 CLI 明确拒绝启动 desktop profile（由 Electron 独占管理）；彻底退出 DSH 后不能继续查询，QuotaMonitor 保留最近有效余额并显示 Stale（从未成功则 Unavailable）。不通过复制认证服务、抓取凭据或私有接口绕过。重启后立即查询。

## 验证

```powershell
node --test tools/test-dsh-bridge.cjs tools/test-dsh-background.cjs
```

离线测试使用合成钱包。test-dsh-cordis.mjs 使用兼容安装运行时验证隔离服务，不加载生产账号；运行条件见 [BUILDING.md](../../BUILDING.md)。隔离测试覆盖调度、失败退避、超时、账号切换及陈旧状态；不把模拟测试当作真实账号或真实断网验证。

官方接口说明：[DeepSeek Harness account service](https://deepseek-harness.github.io/deepseek-harness/en/reference/subsystems/credentials)。
