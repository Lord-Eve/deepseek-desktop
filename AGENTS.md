# 项目约定

## 范围与结构

- 本仓库保持私有。项目是 Win11 x64 的非官方桌面客户端，原生 WPF/.NET 8 界面；DeepSeek 使用固定版本 Rust 引擎，清言与 Grok 使用独立 C# 网关。
- `src/DeepSeekDesktop.Core`：设置校验、TOML 配置、内部 JWT、日志脱敏；可在 Linux 执行回归。
- `src/DeepSeekDesktop`：窗口、托盘、Windows DPAPI/ACL、引擎进程托管。
- `src/DeepSeekDesktop.Gateway`：清言网页协议、Grok OAuth/Responses 协议及仅监听本机的兼容 API。
- `vendor/engine-lock.json` 固定上游版本与校验和；`vendor/*-source.tar.gz` 是对应完整源码，不要随意移除。
- `.github/workflows/windows.yml` 执行检查和打包；`release.yml` 由版本 tag 或手动指定既有版本 tag 触发发布。
- 只在 Git 工作区修改、构建和验证；开发代理不要直接修改已解压的安装目录。新版本通过 GitHub 发布包交付；0.4.0 起用户可在软件中点击下载、替换并重启更新。
- 用户自行维护常用启动目录 `D:\文档\Claude\Projects\DeepSeekDesktop`。不主动下载软件安装包，不自动复制或替换此目录；在 Git 工作区开发和验证，请求发布时通过 GitHub CI 构建与 Release 交付，不主动在本机 dist 准备压缩包或解压副本，由用户通过程序内更新入口自行更新。

## 开发与验证

- 面向用户的文档、更新记录和界面使用中文；提交说明使用英文。
- 修改设置、协议、鉴权或进程生命周期时，运行相关检查。不要将只编译通过表述为已经完成 Win11 桌面或真实账号验证。
- 核心回归：`dotnet run --project tests/DeepSeekDesktop.Checks -c Release`。
- Windows 原生回归：`dotnet run --project tests/DeepSeekDesktop.WindowsChecks -c Release`。
- Grok 契约回归：`dotnet run --project tests/DeepSeekDesktop.GrokChecks -c Release`，仅使用本地模拟 OAuth 和上游，不读真实登录态、不消耗额度。
- 更新回归：`dotnet run --project tests/DeepSeekDesktop.UpdateChecks -c Release`，仅创建临时软件目录，验证校验、路径限制、文件占用回滚和更新组件重启。不得用真实软件目录试装。
- 引擎契约回归：先用核心检查的 `--fixture fixture.toml` 生成假配置，再运行 `python tests/engine_contract.py --engine engine/ds-free-api.exe --config fixture.toml`。测试仅连接本地假上游，不需要真实账号。
- Windows 打包：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish-windows.ps1`。
- PowerShell 文件使用 UTF-8 BOM 和 CRLF，兼容 Windows PowerShell 5.1。

## 凭据与发布

- 不读取或复用其他项目里的凭据。不要把真实账号、密码、Token、device_id、运行配置或私人日志提交到 Git。
- API 仅监听 `127.0.0.1`；保持本地 API Key 与内部管理鉴权分离。
- 更新引擎时同时核对程序中的二进制 hash、版本锁文件、对应源码和许可证。
- 发版前同步 `.csproj`、CHANGELOG 和 `docs/releases/v版本.md`。用户要求发布时，完成检查后推送匹配版本的 tag；不覆盖已发布 tag 和资产。
- 实际网页登录能力、验证码和账号限制须单独验证，不用模拟上游结果承诺长期稳定性。
