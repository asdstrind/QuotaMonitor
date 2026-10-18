# Antigravity 本地 Token 历史读取边界

v1.6.4 的历史通道独立于 Weekly 额度查询。实现入口为 `AntigravityHistorySource`，由 `HistoryCoordinator` 调度，仍只持久化既有 `UsageHistory` 每日汇总结构。源标识不按模型家族拆分：Antigravity 中的 Claude/GPT 模型仍属于 Antigravity；不会因此导入 ClaudeCN 或 Codex 的历史。现有 ClaudeCN 服务端账单与 Codex 本地日志各有独立来源；本实现不依据模型名、相同 Token 数或相近时间跨服务猜测合并，无法证明不同系统账单记录是否指向同一上游请求。

## 读取范围和隐私

仅枚举下列固定目录的直接文件：`%USERPROFILE%/.gemini/antigravity-cli/conversations`、`%USERPROFILE%/.gemini/antigravity`、`%USERPROFILE%/.gemini/antigravity/conversations` 的 SQLite，以及 `%USERPROFILE%/.config/tokscale/antigravity-cache/sessions` 的 JSONL。`GEMINI_CLI_HOME` 可覆盖 `.gemini` 根目录，`TOKSCALE_CONFIG_DIR` 可覆盖 Tokscale 根目录。不遍历聊天目录内容，不读取账号文件、Cookie、认证状态库或 `steps.step_payload`。SQLite 仅查询 schema、`gen_metadata.idx/data`、`steps.metadata`；protobuf 只解码选定时间、模型、Token 与临时去重/时间匹配标识，其他字段跳过，不解码文本。会话、请求和 Bot 标识只在扫描内存中使用，不写日志或缓存。诊断工具的 `--antigravity-history` 只输出每日统计，输出文件须留在忽略的本地 evidence 目录。不会上传本机数据。

## 字段与时间

已确认 SQLite 布局为普通 `gen_metadata(idx,data)` 表。usage 位于 `gen_metadata.#1.#4`。字段 #1 是模型枚举，#2 为新处理输入，#4 为缓存写入，#5 为缓存读取，#3 为总输出，#9 为 Thinking、#10 为可见输出。若同时存在总输出及分项，要求 #3 = #9 + #10；总计为 #2 + #4 + #5 + #3，不再加 #9/#10。没有总输出时可用确认的两个输出分项相加。protobuf 省略的计数按零默认值，但只有模型、没有任何 Token 计数字段的行不证明测量为零。

优先接受 `chatModel.#9.#4` 的标准 seconds/nanos 时间；否则以 generation 根 #4 的步骤 UUID 与 usage #7 Bot ID，严格匹配 `steps.metadata.#12`、`steps.metadata.#9.#7` 与 `steps.metadata.#1`。拒绝冲突或重复生成 Bot。仅在生成 UUID 唯一且步骤时间唯一时允许无 Bot 的 UUID 匹配。本版有意不实现依赖行序的时间恢复；不支持含义未确认的 opaque 时间编码，不用会话创建时间、文件时间或刷新时间代替事件时间。按系统本地时区分日，与现有 Harness 一致。

## 去重、格式变化与状态

会话文件名+idx 标识数据库副本中的同一行；response ID 标识跨会话的同一请求。关联标识合并后若时间、模型或计数冲突，排除整组，不选最大值。独立会话的不同请求保留。SQLite 存在时不混用 JSONL；即使数据库坏掉，也不以较小的 JSONL 缓存掩盖失败。

普通额外列及 WITHOUT ROWID 可读；视图、虚表、隐藏/生成列及不认识的 schema 不作为可用历史。没有 gen_metadata 的其他数据库跳过。元数据读取失败可保留其他已验证记录，所有相关日期使用既有 Partial 质量标记；未映射重试同样不能建立完整性。没有已知正数的部分结果不建立零日期。完整已识别历史仅在最早可核验时间记录到今天、并与显示窗口相交的范围内生成零日期；空表和覆盖前日期保持未知。

JSONL 仅接受 Tokscale session_meta/usage、毫秒事件时间和整数计数，必需 input/output；缓存字段别名冲突视为不完整。非零 reasoning 因与 output 的包含关系无法独立确认而排除；缓存始终是不完整的本机快照，不证明实时或完整。

图例与其他 Harness 使用相同的普通数字，不显示附加状态文字。日期面板只显示各已勾选平台数值与合计，未知显示「—」；不展示质量说明、圆点或估算边框。内部 UsageQuality、Error、Excluded 及过期状态继续保留。成功完整读取且有可核验时间记录时，仅从最早记录起到今天建立零用量日；覆盖前日期和空表的未知时间范围不补零。保留的旧完整统计改为过期且不完整，原成功时间保留；同一来源根/时区之外不复用缓存。

## 并发和资源控制

Windows 系统 SQLite 以 READONLY 打开，并启用 query_only、trusted_schema=OFF；schema 与数据在同一事务快照。WAL 按 SQLite 协议读取，不复制活动数据库、不做 checkpoint 或写入数据。SHM 可按正常读协议协调读取标记。仅无 WAL 的普通读取失败可 immutable 重试，要求文件头、大小、修改时间和 sidecar 状态前后一致，否则不接受结果。此检测不是对恶意并发篡改的安全证明。

500 数据库、200,000 元数据行、64 MiB 累计、单 BLOB/JSONL 行 1 MiB、单 JSONL 文件 16 MiB、总 10 秒；SQLite 100ms busy timeout 与 progress handler；schema 每次最多 128 项、64 列。达到全局预算不发布新截断统计。模型数、日期汇总使用 checked 计数，溢出保持未知。旧缓存保留规则、后台调度与取消均有合成测试。

## 参考和许可

- [CodexBar Antigravity 文档](https://github.com/steipete/CodexBar/blob/main/docs/antigravity.md)，及其 `AntigravityProtoReader`、`AntigravityLocalSQLite`、`AntigravityLocalJSONL`。研究了只读、字段与保守状态处理；本地研究副本不进入发布包。
- [Tokscale SQLite 解析器（固定版本）](https://github.com/junhoyeo/tokscale/blob/62ca1eb1677556972ba963fdfa3a41ab23c1eb4b/crates/tokscale-core/src/sessions/antigravity_cli.rs)。其对 #1 和 #9/#10 的解释与后续文档存在差异；不把 #1 计入 Token。
- [ccusage Antigravity 解析器（固定版本）](https://github.com/ccusage/ccusage/blob/d41bf3d48a911e9742793087142e323093ae6a4f/rust/adapters/antigravity/src/parser.rs)，核对 #1/#3/#9/#10 语义。
- [SQLite 只读连接](https://www.sqlite.org/c3ref/open.html)、[WAL](https://www.sqlite.org/wal.html)。

三个项目在所检查版本均声明 MIT；没有逐段移植或拷贝代码，独立实现 C# 读取与测试。产品没有新增 NuGet 依赖，也未打包第三方应用或解析器源码。商标与图标来源及既有权限声明保持独立。
