# 第三方组件

本项目是非官方桌面客户端，与 DeepSeek、智谱没有隶属关系。

- 协议引擎：[NIyueeE/ds-free-api](https://github.com/NIyueeE/ds-free-api)，v0.5.1，commit `2cb3b90ff3f6d3056d56db2598acb10e44823bb4`，GPL-3.0。使用上游官方 Release 的 Windows x86_64 二进制，未修改。完整对应源码位于 `source/vendor/ds-free-api-v0.5.1-source.tar.gz`（仓库中为 `vendor/`），保留原始版权和许可证。上游关于账号限制的更正说明见其 [v0.5.1 发布页](https://github.com/NIyueeE/ds-free-api/releases/tag/v0.5.1)。
- 桌面外壳：本项目新增的 C# / WPF / .NET 实现，按根目录 GPL-3.0 许可证发布，源码随包提供。
- .NET / Windows Desktop Runtime：Microsoft 与贡献者，主要采用 MIT 许可证。自包含发布包附带其许可证和第三方声明（`runtime-notices/`）。WPF 使用 Windows 系统组件。
- 产品交互参考：[lbjlaq/Antigravity-Manager](https://github.com/lbjlaq/Antigravity-Manager)。本项目未复制其源代码、图标或其他资源。
- 清言协议参考：[Lequait/dsh-glm-web](https://github.com/Lequait/dsh-glm-web)，commit `a774779de6d06017ec606b82dde1e51f26e4e3fe`，MIT。参考 `lib/chatglm.js` 中的 HTTP 端点、签名、请求体和事件流事实，在本项目独立实现 C# 适配，未捆绑或执行其 JavaScript。保留其版权与许可证如下。
- 清言本地 HTTP 服务使用 Microsoft ASP.NET Core/Kestrel，MIT；自包含包附带运行时许可证。
- CodeBuddy / WorkBuddy 协议参考 [iJetLi/deepseek-harness-codearts](https://gitee.com/iJetLi/deepseek-harness-codearts)，commit `6fa74723d42ff605d1de349589c21a1ac4eb7ba2`，MIT，Copyright (c) 2026 Jet。根据其认证、模型目录、积分查询与 Chat Completions 协议独立实现 C#，不捆绑或执行该项目的 TypeScript / Node 插件。该项目的 MIT 许可证与下方 dsh-glm-web MIT License 条款相同，版权声明为 `Copyright (c) 2026 Jet`。
- Grok OAuth/Responses 协议事实参考 [Cherry Studio v2.1.6](https://github.com/CherryHQ/cherry-studio/tree/v2.1.6) 的 Grok 供应商、PKCE 和请求适配。上游采用 AGPL-3.0；本项目未复制或嵌入其 TypeScript 源码，按公开协议事实独立实现 C#。同时参考 [xAI OIDC 元数据](https://auth.x.ai/.well-known/openid-configuration) 与 [官方集成文档](https://docs.x.ai/build/cli/headless-scripting)。未捆绑 Grok CLI 或其认证文件，不代表获得 xAI 官方背书。

校验信息见 `engine-lock.json`。发布包中的 `.exe` 尚未进行代码签名。

## dsh-glm-web MIT License

Copyright (c) 2026 Lequait

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
