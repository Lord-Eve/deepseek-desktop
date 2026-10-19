# 第三方组件

本项目是非官方 DeepSeek 桌面客户端，与 DeepSeek 公司没有隶属关系。

- 协议引擎：[NIyueeE/ds-free-api](https://github.com/NIyueeE/ds-free-api)，v0.5.1，commit `2cb3b90ff3f6d3056d56db2598acb10e44823bb4`，GPL-3.0。使用上游官方 Release 的 Windows x86_64 二进制，未修改。完整对应源码位于 `source/vendor/ds-free-api-v0.5.1-source.tar.gz`（仓库中为 `vendor/`），保留原始版权和许可证。上游关于账号限制的更正说明见其 [v0.5.1 发布页](https://github.com/NIyueeE/ds-free-api/releases/tag/v0.5.1)。
- 桌面外壳：本项目新增的 C# / WPF / .NET 实现，按根目录 GPL-3.0 许可证发布，源码随包提供。
- .NET / Windows Desktop Runtime：Microsoft 与贡献者，主要采用 MIT 许可证。自包含发布包附带其许可证和第三方声明（`runtime-notices/`）。WPF 使用 Windows 系统组件。
- 产品交互参考：[lbjlaq/Antigravity-Manager](https://github.com/lbjlaq/Antigravity-Manager)。本项目未复制其源代码、图标或其他资源。

校验信息见 `engine-lock.json`。发布包中的 `.exe` 尚未进行代码签名。
