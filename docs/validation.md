# 首版验证记录

2026-10-20，基线 `b72864a` 已在 GitHub 的托管 Windows runner 上完成检查和自包含打包。

[Windows CI 完整记录](https://github.com/Lord-Eve/deepseek-desktop/actions/runs/39366023725)

| 检查 | 实际结果 |
|---|---|
| 设置校验、TOML 转义、JWT 与日志脱敏 | 18 项通过 |
| Windows DPAPI 往返、目录 ACL、损坏配置处理、Job 进程结束 | 6 项通过 |
| 真实固定引擎 + 本地假上游的接口契约 | 18 项通过 |
| Win-x64 自包含构建及 ZIP 打包 | 通过 |

协议检查仅连接本地假上游，没有使用真实 DeepSeek 账号。此记录也不代表完成了 Win11 窗口、托盘、多显示器 DPI、账号跨时段可用性或四个目标客户端的人工验收。

每次 tag 发布会针对该 tag 重新执行相同检查与构建；最终发布结果以 [Release workflow](https://github.com/Lord-Eve/deepseek-desktop/actions/workflows/release.yml) 对应 tag 的执行记录为准。
