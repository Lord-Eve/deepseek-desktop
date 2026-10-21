# DeepSeek Desktop

[私有仓库](https://github.com/Lord-Eve/deepseek-desktop) · [预发布下载](https://github.com/Lord-Eve/deepseek-desktop/releases) · [Windows 构建记录](https://github.com/Lord-Eve/deepseek-desktop/actions/workflows/windows.yml)

面向 Windows 11 x64 的非官方桌面客户端，支持 DeepSeek、智谱清言与 Grok OAuth（CLI 通道）。使用原生窗口管理账号与服务，在 `127.0.0.1` 提供 OpenAI 兼容 API，可供 Cherry Studio、AstrBot、Eve pet、tibo watch 等客户端配置接入。取得初始登录授权后，无需一直打开浏览器，也无需安装 Node、Python 或 .NET 运行时。

**当前版本**：0.4.1，DeepSeek 引擎固定为 ds-free-api v0.5.1。桌面层采用 WPF/.NET 8，清言与 Grok 使用独立 C# 适配。用户已确认清言 API 与工具调用成功；新增 Grok 已通过模拟 OAuth/Exa 闭环检查，真实模型调用与额度仍待本机验证。

## 使用 Grok OAuth（CLI 通道）

停止服务后选择“Grok OAuth（CLI 通道）”，在“账号管理”填写名称并点击“登录 / 重新登录 Grok”，在 xAI 官方页面完成授权。若网页显示授权码，返回本程序粘贴到临时授权码框，点击“完成登录”，无需另开 Grok CLI。自动回调仅监听 `127.0.0.1:56121/callback`，5 分钟超时，可取消；取消或超时后需重新发起登录并使用新代码。若其他程序占用此回调端口，请先完成或取消其登录。授权按账号使用 Windows DPAPI 加密保存，刷新令牌轮换时同步保存；不读取 Cherry Studio 或 Grok CLI 的登录凭据，不需要粘贴 Token。每次启用一个 Grok 账号。

客户端继续使用原地址与 Key，模型先填 `grok-build`。其他预设见 `/v1/models` 及界面的模型选择框，目录只是参考，实际访问资格和额度由官方账号与服务器决定。程序启动仅载入/刷新授权，模型对话由客户端请求或“发送测试消息”主动触发，不自动探测额度。此适配独立实现 OAuth + Responses 请求，不执行本机 Grok CLI，也不自动安装或更新软件。

Grok 提供 `/v1/chat/completions`（文本流式/非流式、原生函数工具声明/选择/结果回传）和 `/v1/responses`（保留原生 Responses 事件）。Exa MCP 等外部工具继续由原客户端执行。Chat Completions 请求转换成 Responses 后，工具会话依赖上游加密思考和原始工具项；这些内容仅在内存缓存最多 128 项、合计 16 MB、有效期 30 分钟，并绑定账号、模型及历史前缀。重启服务、缓存过期或修改历史后，请新建工具会话。加密思考不会混入 Chat Completions 正文或日志。

Responses 客户端负责保留并回传原生加密上下文；本适配使用 `store=false`，需发送完整历史，不接受 `previous_response_id`。Chat Completions 暂仅支持文本，不保证图片/音频/文件转换；每次串行处理一个请求、最多等待 8 个，沿用本机每小时请求上限。只有未接收事件流时的 HTTP 401 会刷新并重试一次，其他错误不自动重放。

协议事实参考 [Cherry Studio v2.1.6](https://github.com/CherryHQ/cherry-studio/tree/v2.1.6)，以独立 C# 实现并使用自己的客户端标识。官方认证与推理域名不等于官方对第三方适配背书；真实登录、资格、模型权限及实际 Exa MCP 仍需用户测试。完整使用步骤与边界见 [0.3.1 说明](docs/releases/v0.3.1.md)。后续通过 GitHub Release 与程序内更新入口交付，用户自行更新软件目录。

后续在 Git 工作区开发和验证，发版由 GitHub CI 构建并上传 Release，你通过程序内更新入口自行更新常用软件目录；不会主动在本机 dist 准备压缩包、下载安装包或覆盖该目录。

## 使用智谱清言

在“服务概览”选择“智谱清言”，然后在“账号管理”填写账号名称和 `chatglm_refresh_token`。使用自己的浏览器登录 `https://chatglm.cn`，按 F12，在 Application（应用）的 Cookies 或 Local Storage 中查找该字段。不要使用短期的 `chatglm_token`，不要把 Token 发到聊天或截图里。编辑已有账号时留空保留原值；有新令牌时直接填写并保存。

启动会刷新并校验登录态，不发送对话；点击“发送测试消息”才会主动发出测试对话。上游轮换 refresh token 时会自动加密保存新值。客户端沿用原有地址和 API Key，模型改为 `qingyan-default`。停止服务后可以切回 DeepSeek，另一平台的账号一直保留，旧配置默认归属于 DeepSeek。

清言支持 `/v1/models`、`/v1/chat/completions` 的文本流式和非流式回复。多轮消息按角色拼接后传给一次新的网页会话，这与原生 Chat Completions 的角色隔离不同；不保证模型遵循每一条 system 指令。每次对话结束会尽力删除临时会话，失败时网页端可能残留会话。每账号每小时上限在本机执行，服务重启后重新计数；不代表上游认可的使用额度。同一时刻只向清言发送一个对话请求，最多等待 8 个请求，相邻请求至少间隔 1.5 秒，该间隔不保证免受上游限制。

`qingyan-default` 仅代表清言网页默认模式，不等同于特定付费 GLM 模型。当前不提供图片/文件/音频、结构化输出约束、Responses、Anthropic 路由或显式深度思考切换；不支持的接口或内容会报错。温度、采样参数及输出 token 上限目前由网页默认行为决定，不作官方 API 等价保证。普通请求的搜索默认读取连接设置，可用 `web_search_options.search_context_size: "none"` 关闭；真实上游是否遵循该选项仍待验证。

### 清言与 Exa MCP 等外部工具

在原客户端启用 `qingyan-default` 的工具调用并选择已配置的 MCP 工具即可。客户端将工具目录作为 OpenAI `tools` 传入；本程序把清言输出转成标准 `tool_calls`，客户端执行 Exa 等工具，再通过 `role: "tool"` 和匹配的 `tool_call_id` 回传结果。MCP 由客户端管理，不需要在本程序里重复配置 Exa Key 或 MCP 服务。本程序不会执行工具或读取这些凭据。

支持 `tool_choice: "auto"`、`"none"`、`"required"` 和指定 function；`parallel_tool_calls: false` 限制每轮至多一个工具调用。非流式返回 `message.tool_calls`，流式返回 `delta.tool_calls`（含 index）及 `finish_reason: "tool_calls"`，调用参数为 JSON 字符串。结果回传后可以继续回答或再次发起调用。旧 `functions/function_call` 协议暂不支持。

这是一种提示词兼容适配，清言可能不遵循协议。带工具时使用网页通用模式并关闭内置搜索，避免用内置搜索替代你指定的 Exa。为避免客户端执行不完整调用，本程序会先收齐并校验回复，再发送文本或工具事件；这类请求的首段输出较晚。`tool_choice: "none"` 或不提供工具时仍沿用普通文本流式行为。未收到上游完成标记、调用块不完整、工具名称不符、参数不符合常见 schema 约束时会报错，不自动补参数或重放对话；收到两份同名同参数调用时只保留一次。

参数校验覆盖类型、必需字段、枚举、对象属性、数组元素与长度、数字范围及 anyOf/oneOf/allOf 等常见约束，不是完整 JSON Schema 实现；工具客户端仍应按自身 schema 校验。单次工具定义最多 64 个、定义文本合计最多 65536 字符，每轮最多 16 个调用。模拟检查已覆盖“请求搜索 → 客户端执行 → 结果回传 → 生成回答”的完整流程，用户也已确认本版工具调用成功；其他工具与客户端仍需分别验证。

清言协议参考固定版本的 [Lequait/dsh-glm-web](https://github.com/Lequait/dsh-glm-web/tree/a774779de6d06017ec606b82dde1e51f26e4e3fe)，沿用本项目强制本地鉴权、仅绑定 `127.0.0.1`、不开放浏览器跨源访问的边界，不读取浏览器数据、不自动处理验证码或绕过账号限制。两小时访问令牌缓存来自社区观察，未获得官方保证；失效时提示更新登录态，不自动重放失败的对话。

## 使用 DeepSeek

1. 将整个 ZIP 解压到一个固定目录，运行 `DeepSeekDesktop.exe`。不要只移动主程序，`engine/` 必须放在它旁边。
2. 在“账号管理”添加自己的账号：填写名称、邮箱或不含 `+86` 的大陆手机号、登录密码，以及该账号的真实 `device_id`，保存。
3. 在“服务概览”启动服务。上游 v0.5.1 引擎会登录账号并为每个账号发送一条健康检查消息；界面已明确提示这一行为。没有开机自启或应用启动后自动登录。
4. 显示“账号已就绪”后，复制 API 地址和 API Key。可先“检查本地接口”（只获取模型列表，不发送对话），再主动点击“发送测试消息”验证真实上游。
5. 将地址、Key 和模型填入客户端，先测试一轮普通回复，再测试流式和多轮对话。

默认连接信息：

| 项目 | 值 |
|---|---|
| 服务类型 | OpenAI 兼容 |
| Base URL | `http://127.0.0.1:22217/v1` |
| API Key | 从程序中复制，首次启动随机生成 |
| 模型 | `deepseek-default` |
| Chat Completions 完整地址 | `http://127.0.0.1:22217/v1/chat/completions` |

若某个客户端会自行追加 `/v1`，该客户端的地址栏使用 `http://127.0.0.1:22217`，避免重复成 `/v1/v1`。客户端与程序须在同一台 Windows 主机、同一网络环境中；Docker、WSL 或另一台机器中的 `127.0.0.1` 指向它们自身，本版本未开放局域网监听。

`deepseek-default` 表示上游网页的默认模式，不是官方付费 API 模型 ID。`deepseek-chat` 是同一模式的兼容别名；本版本没有虚构 V3、V4、Expert 或 Reasoner 等独立模型。默认思考行为由协议引擎决定；可通过 `reasoning_effort: "none"` 请求关闭思考。默认不开启联网搜索，可在连接设置中调整。

## device_id 与账号接入

DeepSeek 沿用上游实现的“密码 + 真实设备信息”接入，不提供自动浏览器登录、短信验证码或已有 DeepSeek 网页 Token 的导入。

使用自己的浏览器登录 `https://chat.deepseek.com`，在开发者工具的 Network 中查看登录请求 `/api/v0/users/login` 的请求体，找到 `device_id`，填写到桌面程序的对应字段。每个账号分别获取，不用随机字符串代替。编辑现有账号时，密码和设备字段留空表示保留原值；更换登录名时需要重新填写两者。

**上游已有请求体变化和账号受限的反馈。** 如果浏览器请求体已加密、字段不存在或需要验证码，本版本暂不能自动解决；不能把这种情况理解为密码填写错误并反复重试。登录错误、禁言、网络错误会显示在日志或账号池状态中。上游发布说明已撤回关于规避账号处罚的验证结论，见 [v0.5.1 发布说明](https://github.com/NIyueeE/ds-free-api/releases/tag/v0.5.1)。

不需要把密码、Token 或 device_id 发给开发者；真实账号验证应在你自己的 Windows 电脑上完成。

## 核心功能与边界

- 账号添加、编辑、启停与删除；多账号由上游引擎选择调度，支持每账号每小时上限。
- 本机 API 一键启停；端口占用时先报错，不结束其他程序。启动等待上限为 3 分钟，可取消。
- 自动生成本地 API Key，可复制或重新生成。更换 Key 后客户端需同步更新。
- 两平台均提供模型列表、Chat Completions 流式/非流式和多轮历史；DeepSeek 引擎另外提供 Responses 和 Anthropic 兼容路由。
- 账号池状态、本次服务的成功/失败次数、脱敏运行日志、本地接口检查和主动对话测试。
- 关闭窗口静默收起到系统托盘，不弹收起通知；双击托盘图标恢复；“退出程序”会停止引擎。停止服务将终止进行中的请求。

更新入口位于“连接设置 → 数据与版本”：启动时检查，也可手动检查。发现新版本后点击“下载并重启更新”，程序会通过已登录的 GitHub CLI 下载私有仓库的 ZIP 与 SHA-256 文件，校验后退出，由独立组件替换当前软件目录文件并重新启动；原先运行的服务会恢复启动。下载期间可取消，下载和校验失败时继续使用原版；替换或新版本初始化失败时尝试恢复旧文件。账号、API Key 和连接设置继续保存在当前 Windows 用户的数据目录。

0.3.x 及更早版本仍只有网页下载入口，需最后手动升级一次到完整的 0.4.0；之后可在软件内更新。软件目录需对当前用户可写，更新不自动提权，不支持在 Git 工作区或目录链接中直接替换。检查和下载需要本机安装并登录 GitHub CLI，且有本私有仓库访问权限。下载页保留为备用入口，不读取或复制 GitHub 登录令牌。更新缓存位于数据目录的 `updates` 中，成功后清除下载与备份，运行中的小型更新组件在下次正常启动时清除；失败备份保留供恢复。更新不删除软件目录内不属于新包的文件。

完整更新说明见 [0.4.0 发布说明](docs/releases/v0.4.0.md)。

0.4.1 修复 Grok 测试请求错误地关闭思考的问题：grok-4.7 测试使用 low，客户端请使用 low/medium/high/xhigh，不能传 none；grok-4.5 支持 low/medium/high。授权保存成功后可关闭浏览器中的 code 页面，是否可对话仍需单独测试。错误提示现在保留参数错误的 HTTP 状态与安全字段，不再一律显示 502。见 [0.4.1 发布说明](docs/releases/v0.4.1.md)。

本版没有自动替换程序文件、开机自启、文件/图片专用管理界面或按客户端分别分配 API Key。DeepSeek 和清言的工具调用均采用相应的提示词与解析适配，不能等同于官方原生工具调用。本地接口就绪不代表每个账号长期可用；深度思考、搜索、模型能力和限流行为仍取决于上游。

## 凭据与本地数据

- 默认数据目录：`%LOCALAPPDATA%\DeepSeekDesktop`。
- `settings.dpapi` 保存全部设置，使用当前 Windows 用户的 DPAPI 加密，不保存硬编码解密密钥。复制到其他 Windows 用户通常无法解密。
- 数据目录关闭继承权限，仅对当前用户授予访问。无法读取或解密设置时，程序报错退出，不静默覆盖账号数据。
- 引擎仅接受 TOML 文件，因此运行时需要临时明文配置，位于 `runtime/<随机ID>/`，使用相同的私有访问权限。停止后删除，异常退出残留会在下次启动时清理。这不保护同一 Windows 用户下运行的恶意进程。
- 上游运行日志和统计也位于该临时目录，停止后清理；桌面日志仅保留当前应用会话的有限长度显示，并替换已知凭据。分享日志前仍请检查个人信息。
- 上游 Web 管理密码登录与首次设置入口通过非空、不可用的密码哈希锁定；原生窗口使用每次启动随机生成的内部凭据读取状态，不引导用户访问网页控制台。
- Windows Job Object 使桌面程序被终止时，其托管的后端进程也随之结束。正常退出会进一步清理临时文件。
- 协议引擎在每次启动前校验固定 SHA-256。发布包未签名，Windows 可能显示未知发布者。

需要隔离测试数据时，可在启动进程前设置环境变量 `DEEPSEEK_DESKTOP_DATA_DIR` 为一个独立目录。

## 接口示例

以下操作会发送真实消息；Key 仅在你自己的终端中填写：

```powershell
$headers = @{ Authorization = 'Bearer 这里填程序复制的Key' }
$body = @{
    model = 'deepseek-default'
    messages = @(@{ role = 'user'; content = '你好' })
    stream = $false
} | ConvertTo-Json -Depth 5
Invoke-RestMethod 'http://127.0.0.1:22217/v1/chat/completions' `
    -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($body))
```

## 从源码构建

Windows 安装 .NET 8 SDK 和 Python 3.11+（用于本地协议检查）后，在项目目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\publish-windows.ps1
```

脚本会校验并准备固定版本引擎、执行核心、Windows 原生和本地模拟上游协议检查，生成自包含 ZIP。便携程序无需管理员权限；构建不需要 DeepSeek 凭据，也不会启动真实账号。源码从 `source/` 解压构建时，应保留 `vendor/` 中的对应引擎源码与版本锁文件。

跨平台核心检查：

```text
dotnet run --project tests/DeepSeekDesktop.Checks -c Release
```

Linux 上可编译 Windows 项目，但不能用该结果代替真实 Win11 桌面验收：

```text
dotnet build src/DeepSeekDesktop/DeepSeekDesktop.csproj -c Release -p:EnableWindowsTargeting=true
```

协议回归：先用核心检查生成测试配置，再让 Python 启动固定版本真实引擎和一个本地假上游。测试专用 WASM 不会被打包或用于真实账号：

```text
dotnet run --project tests/DeepSeekDesktop.Checks -c Release -- --fixture fixture.toml
python tests/engine_contract.py --engine /path/to/ds-free-api --config fixture.toml
```

## 已完成验证与待验收

本版在本机通过 27 项核心检查、14 项界面检查，并实际验证了新增检查器读取私有仓库版本信息。完整发布检查由对应 tag 的工作流执行，包含 Windows 原生与本地模拟上游检查，不使用真实 DeepSeek 账号。

前版 Windows CI 与本机验证的历史记录见 [验证记录](docs/validation.md)。每次发布针对对应 tag 重新执行自动检查。

未验证：完整 Win11 窗口、托盘和 DPI 交互；跨时段使用；Cherry Studio、Eve pet、tibo watch 的真实接入。用户已报告 AstrBot 接入跑通。托管 Windows runner 的检查不能代替你的 Win11 桌面和真实账号验收。

## 许可证和来源

GPL-3.0，见 `LICENSE`。协议引擎来源、版本、完整对应源码和第三方声明见 `THIRD-PARTY-NOTICES.md` 与 `vendor/engine-lock.json`。没有复制 Antigravity Tools 的代码或资源。
