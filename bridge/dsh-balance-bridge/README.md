# DSH Balance Bridge 1.3.0

QuotaMonitor v1.6.5 的 Host 兼容修复，通信仍为 Protocol 1，UI 不变。

## 安装与升级

已链接到本目录的安装只需更新源码，并在无运行任务时正常退出、重启 DSH；不用反复改版本或重装整个 Profile。复制安装可通过官方插件管理器更新。首次安装使用官方 CLI：

```powershell
$bridgeSource = (Resolve-Path ./bridge/dsh-balance-bridge).Path
dsh plugin --profile desktop add $bridgeSource --ignore-scripts
```

官方 Host 提供 peers；保留官方的 `autoInstallPeers: false`。不要在插件目录额外 npm install Host 包，也不要为升级重置其他插件。修改 Profile 前备份，且只修改本插件项。

## Host 与协议的独立边界

`engines.dsh: >=0.2.0-rc.2 <0.3.0-0` 是 Host 加载边界，含同一 0.2 分支的 rc 和 patch，排除 0.3 的全部预发布及更高版本。rc.5 的实际 `evaluatePluginCompatibility()` 优先使用 engines.dsh，并以 `includePrerelease: true` 计算范围；没有 engines 才逐个检查 DSH peers。没有设置兼容豁免。

必需 peers 只有 Cordis `~4.0.4`、dsh-app-boot（获取真实 Host 版本）、dsh-credentials（只构造账户事件 key，不读取凭据）。后两者的范围为 `>=0.2.0-rc.2 <0.3.0-0 || >=0.2.1-0 <0.3.0-0`；第二个子范围为普通包管理器的预发布规则锚定 0.2.1，覆盖任意后续 rc，而非列举 rc。Host 自身 includePrerelease 覆盖未来 0.2 patch 的预发布；普通 npm 的默认规则并不相同，因此不应自动安装 Host peers。rc.5 的链接插件解析器还会根据 peer **名称**选择 Host 安装包，不能删掉实际导入的 peer。

不再依赖 Typert，也不再注册 renderer `publish` Remote 或客户端 Slot 监听：后台查询直接使用注入的官方账户服务。缺少服务或缺少 getState/getBalance/watch 时安全不可用，不能退回到页面提交任意余额。

Protocol 1 才是余额数据契约：protocol 必须为整数 1；sourceVersion 仅作有长度和格式约束的诊断元数据，接收端不再列举 Host 版本。未知/重复字段、矛盾状态、非法钱包、过大帧、无效/未来/倒序时间戳都会被拒绝。金额保持原始十进制字符串，最多 256 字符，指数绝对值最多 1000；不转浮点、不跨币种求和。失败不是零余额。协议若发生不兼容变更，需要明确迁移，不能靠放宽 Host 范围接收。

## 后台与安全

启动立即调用 deepseekAccount.getState()/getBalance(client)，成功后每 180000ms 查询，跟页面及窗口显示无关。失败退避 30、60、120、240、480、900 秒。125 秒 watchdog 覆盖状态及余额调用；仍保持单请求，无法取消的官方 Promise 永久不结束时必须等待 Host 重启。

账户事件只比较官方 credentialKey，不读取记录；换号/退出立即清空旧钱包并使在途请求失效。Host 启动先清除无法归属的缓存；官方服务卸载/查询失败保留最近有效钱包为 Stale。watch API 失败时清除无法再验证账户归属的钱包并停止该 poller。Host 完全退出后管道断开，接收端立即 Stale；Host 重启后管道 1–30 秒退避自动重连。四分钟无新快照也为 Stale。

Named Pipe 保持 CurrentUserOnly、FirstPipeInstance、16 KiB 上限、三秒半帧超时。该边界拒绝其他/匿名 Windows 身份，**不认证同一用户下的任意进程是官方 DSH**；同用户恶意软件和管道抢占不在当前信任模型之外另作承诺。

不读取私有认证文件、Cookie 或 Token，不直连私有账户 API，不修改 Host 源码。日志只有固定事件码：Host 日志的 `[QuotaMonitor Bridge] plugin-loaded / host-api-* / ipc-*` 和 `%LOCALAPPDATA%/QuotaMonitor/dsh-bridge.log` 的 `receiver-protocol-rejected / receiver-frame-rejected / ipc-*`，不包含钱包、账户标识、原始帧或异常文本。接收日志上限约 64 KiB；不新增 UI 面板。

## 测试边界

```powershell
node --test tools/test-dsh-bridge.cjs tools/test-dsh-background.cjs
$env:DSH_TEST_RUNTIME = '<官方运行时 node_modules>'
node --test tools/test-dsh-compatibility.mjs
node tools/test-dsh-cordis.mjs
node tools/test-dsh-official-outcomes.mjs
```

compatibility 测试使用真实 Host 检查器，但替换版本参数只证明范围不拦截，不能证明历史或未来 Host 的 API 正常。Cordis/官方 outcomes 测试使用真实安装代码与合成服务/请求，不加载生产 Profile。rc.6 等尚未发布版本仅为合成版本场景；0.2 分支若破坏 API，仍可能需要适配。详细本次实际验证见本地验收报告。

官方参考：[账户服务](https://deepseek-harness.github.io/deepseek-harness/en/reference/subsystems/credentials)。
