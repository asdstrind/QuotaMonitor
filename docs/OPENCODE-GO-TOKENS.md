# OpenCode Go Token 计数规则（v1.7.0）

数据直接来自 OpenCode Console 的只读 `GET https://opencode.ai/console/api/v2/usage/export?range=90d`，与额度接口、DSH、OpenCode 本地日志及其他 Harness 数据完全分开。只接收精确的 `provider=opencode-go`；模型名不决定计费 Provider。

## 字段关系

采用普通输入、输出和三种缓存桶相加：

```
total = input_tokens + output_tokens + cache_read_tokens
      + cache_write_5m_tokens + cache_write_1h_tokens
```

OpenCode 的[官方 Inference 文档](https://github.com/anomalyco/opencode/blob/e56eff2bc01649b3aa21224876b8874e77dea4b0/services/site/src/docs/content/console/api/inference.mdx#L117)明确：input 排除缓存读取和写入，output 已包括 reasoning。[Token schema](https://github.com/anomalyco/opencode/blob/e56eff2bc01649b3aa21224876b8874e77dea4b0/packages/schema/token-usage.ts)也使用独立 input、output、cache.read、cache.write、cache.write1h 桶。因此不从 input 再减缓存，也不另加 reasoning。

本轮真实导出的字段名已核实；观察到 Go 行的 cache_read_tokens 大于 input_tokens，排除了“缓存读取是该输入字段子集”的解释。生产解析与独立 CSV 解析的逐日结果一致。没有把真实账户计数放入文档或测试。

边界：导出服务端实现并未公开，也没有长期稳定的正式 CSV 契约。CSV 列到上述官方 Token 桶的映射由字段名、官方 Console 展示及真实字段关系支持；本轮未观察到可用于单独核对的非零缓存写入，不能声称已用真实非零写入记录实测两个写入桶的关系。Adapter 和解析器独立封装，字段缺失保持未知，后续契约变化可单独替换。

## 日期、覆盖和累计

官方 Console 公开前端的导出操作说明（2026-11-11，`/console/assets/queries-WWlbD9Jy.js`）明确按 UTC 日 × member/service account × Provider × model 导出，范围覆盖包含当天的完整 UTC 日。保留 `day` 原始 ISO 日期，Tooltip 的 Go 行标注 UTC；不将 UTC 日汇总移动到本地日期并声称是精确本地日。

- 只创建 CSV 实际返回的 Go 日期，不为缺失的日期补零；显式数值零可保留为已知零。空 CSV／仅表头的累计显示 `—`。
- 26 周热力图内查询不到的早期日期保持未知。底部累计是当前取得的服务端范围内已核实计数，不是终身总量。近期 rollup 可能延迟。
- 防御范围为获取时 UTC 今天到今天减 90 日；超出、未来或不可解析的日期不计入，并报告不完整。公开说明未界定包含端点后究竟为 90 还是 91 个日期，因此不凭范围标签伪造覆盖天数。
- 每次导出替换上一份内存聚合，不追加快照。Go 不落盘保存原始或聚合用量，重启重新获取。密钥改变／移除会清空本 Adapter 的内存数据。

## 去重、部分数据和精度

行身份使用 day、user_type、user_id、provider、model，在内存中暂时散列；忽略 user_name。完全一致的重复行计一次，同身份但计数矛盾的行变为未知。不同模型、日期和主体分别累计。返回的生产对象只含每日聚合，不包含用户、用户名、模型明细或散列身份。

所有计数以非负 Int64 严格解析，使用 checked 累计。缺失、负值、小数、无效数字、单行或每日溢出使该日总量未知；跨日累计溢出显示 `—`，不回绕、饱和或补零。无效日期无法归属时，保留整体不完整标记。费用列不参与 Token 计算，也不代表订阅钱包扣款。

额度独立读取 `/zen/go/v1/usage` 的 rolling、weekly、monthly，剩余百分比为 `100-percent`，绝不从 Token 或请求数推算。默认额度 3 分钟、导出 30 分钟；本地倒计时不触发 HTTP。401／403／429／超时／格式变化／网络失败保留旧值并标记过期或不可用，尊重 Retry-After，手动刷新也不绕过冷却。

## Collapsed 单值摘要

按[官方 Go 窗口额度基数](https://opencode.ai/docs/console/go#usage-limits)比较三个窗口：5 小时剩余百分比 × 0.2、每周 × 0.5、每月 × 1。选择折算后剩余量最少的窗口，显示该窗口自身的剩余百分比。比如 5 小时剩余 15%、每周剩余 5%，月基准下分别剩余 3% 和 2.5%，因此显示每周的 `5%`。同额时优先短窗口。无效或缺失字段不补零；Expanded 仍显示三个原始窗口。

Codex 未提供可核实的窗口额度基数，本轮不按窗口时长或原始百分比猜测其额度关系；只在同一选定额度池确有未重置的耗尽窗口时，优先显示 `0%`。
