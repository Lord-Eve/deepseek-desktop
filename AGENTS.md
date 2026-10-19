# 项目约定

## 范围与结构

- 本仓库保持私有。项目是 Win11 x64 的非官方 DeepSeek 桌面客户端，原生 WPF/.NET 8 界面，本地 API 由固定版本 Rust 引擎提供。
- `src/DeepSeekDesktop.Core`：设置校验、TOML 配置、内部 JWT、日志脱敏；可在 Linux 执行回归。
- `src/DeepSeekDesktop`：窗口、托盘、Windows DPAPI/ACL、引擎进程托管。
- `vendor/engine-lock.json` 固定上游版本与校验和；`vendor/*-source.tar.gz` 是对应完整源码，不要随意移除。
- `.github/workflows/windows.yml` 执行检查和打包；`release.yml` 仅由版本 tag 触发发布。

## 开发与验证

- 面向用户的文档、更新记录和界面使用中文；提交说明使用英文。
- 修改设置、协议、鉴权或进程生命周期时，运行相关检查。不要将只编译通过表述为已经完成 Win11 桌面或真实账号验证。
- 核心回归：`dotnet run --project tests/DeepSeekDesktop.Checks -c Release`。
- Windows 原生回归：`dotnet run --project tests/DeepSeekDesktop.WindowsChecks -c Release`。
- 引擎契约回归：先用核心检查的 `--fixture fixture.toml` 生成假配置，再运行 `python tests/engine_contract.py --engine engine/ds-free-api.exe --config fixture.toml`。测试仅连接本地假上游，不需要真实账号。
- Windows 打包：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish-windows.ps1`。
- PowerShell 文件使用 UTF-8 BOM 和 CRLF，兼容 Windows PowerShell 5.1。

## 凭据与发布

- 不读取或复用其他项目里的凭据。不要把真实账号、密码、Token、device_id、运行配置或私人日志提交到 Git。
- API 仅监听 `127.0.0.1`；保持本地 API Key 与内部管理鉴权分离。
- 更新引擎时同时核对程序中的二进制 hash、版本锁文件、对应源码和许可证。
- 发版前同步 `.csproj`、CHANGELOG 和 `docs/releases/v版本.md`。用户要求发布时，完成检查后推送匹配版本的 tag；不覆盖已发布 tag 和资产。
- 实际网页登录能力、验证码和账号限制须单独验证，不用模拟上游结果承诺长期稳定性。
