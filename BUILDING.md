# 构建与验证

## 源码组成

`QuotaEstimation.sln` 包含 WPF 主程序、Core、xUnit 测试和可选 Diagnostics。DSH Balance Bridge 是 `bridge/dsh-balance-bridge` 中的 JavaScript 插件，不参与 .NET 编译。旧版图标截图工具属于本地开发记录，不随源码分发。

主程序所需 ICO、Harness SVG、XAML 和所有 ProjectReference 都在源码树内。PNG/SVG 品牌源文件用于维护图标。没有对 `publish/`、`evidence/` 或私人源码项目的构建引用。

## 环境与命令

安装 `global.json` 指定的 .NET SDK 10.0.401、Windows Desktop 构建组件，以及用于 Bridge 测试的 Node.js 24。NuGet 采用公开源，遇到企业或本地源配置时需自行核对；不要提交带凭据的 NuGet 配置。

```powershell
dotnet restore QuotaEstimation.sln
dotnet build QuotaEstimation.sln -c Release --no-restore
dotnet test tests/QuotaMonitor.Tests/QuotaMonitor.Tests.csproj -c Release --no-build --results-directory TestResults
node --test tools/test-dsh-bridge.cjs tools/test-dsh-background.cjs
dotnet publish src/QuotaMonitor/QuotaMonitor.csproj -c Release -r win-x64 --self-contained true -o publish/local-win-x64
```

`build.ps1` 优先使用本地 `.dotnet/dotnet.exe`，没有时使用 PATH 中的 SDK。SDK 本身不纳入仓库。`-Publish` 默认写入版本化输出目录，已有发布 EXE 时拒绝覆盖；可用 `-PublishDirectory publish/local-win-x64` 指定新的输出位置。

## Bridge 依赖

插件 peerDependencies 为 `@deepseek-ai/cordis ~4.0.4`、`@deepseek-ai/dsh-typert-protocol 0.2.0-rc.2`，由兼容 DSH 宿主提供；没有编译或 npm 安装脚本。单元测试只用 Node 内置模块。

`tools/test-dsh-cordis.mjs` 是额外的宿主集成测试，需要兼容官方运行时的 `node_modules`，其中还须有 `dsh-typert-registry`、`dsh-api-gateway`、`dsh-app-boot`。设置 `DSH_TEST_RUNTIME` 为该目录，再执行 `node tools/test-dsh-cordis.mjs`。它使用合成钱包和随机测试 Pipe，不加载账户或 Profile；这些宿主包不随本仓库复制分发。`tools/test-dsh-official-outcomes.mjs` 另需同版本 dsh-deepseek-account-platform，使用替换 fetch 和合成 grant 验证官方 HTTP 202、网络异常、超时及换号结果；不构造生产账号服务或读取凭据。这些测试不保证未来宿主版本兼容。

## 验证边界

`.NET` 与 Bridge 单元测试使用临时目录、假的 HTTP Handler 和合成协议数据，不需要真实认证。发布前审计的净源码重建和结果见本地审计报告；源码本身不依赖该报告。

`QuotaMonitor.exe --verify-morph` 与 `--verify-settings` 是内置验证入口。部分 GUI 验证仍读取本机历史/路径，Settings 验证会使用本应用随机测试凭据项；因此任何输出默认视为私人数据。普通启动、Diagnostics、`--smoke-test`、`--verify-dsh-balance` 可访问账户/真实历史，不属于无账户测试，不应在公开 CI 中执行。

所有临时文件留在忽略目录或系统临时目录。只发布当前源码可以重新生成完整应用，无须旧版发布包或既有 Git 历史。首次 GitHub Release 仍应重新核对运行时授权、品牌资产、二进制/PDB 隐私及实际账号兼容性。

## 完整 GitHub 发布包

```powershell
./tools/package-release.ps1
```

这个本地脚本生成 v1.5.3 Windows x64 自包含 ZIP、SHA-256 文件，以及 Bridge 1.1.0 源码和完整许可证目录。不提交或上传任何内容。它关闭调试符号，并将编译路径映射到中性路径；已有输出时拒绝覆盖。第三方 Logo 的独立授权风险仍见 [TRADEMARKS.md](TRADEMARKS.md)。

`NuGet.Config` 只使用公开 nuget.org 源，不依赖本机私有包源。如果出现 NU1900，请先恢复漏洞源网络连接。只为了检查编译时，可以临时加 `-p:NuGetAudit=false`；这不代表在线漏洞审计通过，也不应修改仓库默认 NuGetAudit=true。

打包脚本的对应临时选项为 `-SkipOnlineAudit`，只在明确了解这项未验证限制时使用。受限 Windows 沙箱可能拒绝 CurrentUserOnly 测试管道；在正常用户环境复测，不要为通过测试放宽管道 ACL。还原阶段可使用 `--disable-parallel -m:1 /nr:false` 避免受限环境的并行 MSBuild 进程问题。

可选的 [Showcase](tools/QuotaMonitor.Showcase/README.md) 工具不参与主解决方案，它使用模拟数据生成公开展示资源，不启动真实监控。主解决方案中的 Diagnostics 仅供本地使用，默认不会随发布包分发。
