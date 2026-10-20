# DeepSeek Desktop

[私有仓库](https://github.com/Lord-Eve/deepseek-desktop) · [预发布下载](https://github.com/Lord-Eve/deepseek-desktop/releases) · [Windows 构建记录](https://github.com/Lord-Eve/deepseek-desktop/actions/workflows/windows.yml)

面向 Windows 11 x64 的非官方桌面客户端，支持 DeepSeek 和智谱清言。使用原生窗口管理账号与服务，在 `127.0.0.1` 提供 OpenAI 兼容 API，可供 Cherry Studio、AstrBot、Eve pet、tibo watch 等客户端配置接入。取得初始网页登录凭据后，无需一直打开浏览器，也无需安装 Node、Python 或 .NET 运行时。

**当前版本**：0.2.0，DeepSeek 引擎固定为 ds-free-api v0.5.1。桌面层采用 WPF/.NET 8，DeepSeek 使用 Rust 引擎，清言使用独立 C# 适配。用户已确认清言 API 正常调用；其他客户端、完整场景及跨时段稳定性仍待验证。

## 使用智谱清言

在“服务概览”选择“智谱清言”，然后在“账号管理”填写账号名称和 `chatglm_refresh_token`。使用自己的浏览器登录 `https://chatglm.cn`，按 F12，在 Application（应用）的 Cookies 或 Local Storage 中查找该字段。不要使用短期的 `chatglm_token`，不要把 Token 发到聊天或截图里。编辑已有账号时留空保留原值；有新令牌时直接填写并保存。

启动会刷新并校验登录态，不发送对话；点击“发送测试消息”才会主动发出测试对话。上游轮换 refresh token 时会自动加密保存新值。客户端沿用原有地址和 API Key，模型改为 `qingyan-default`。停止服务后可以切回 DeepSeek，另一平台的账号一直保留，旧配置默认归属于 DeepSeek。

清言支持 `/v1/models`、`/v1/chat/completions` 的文本流式和非流式回复。多轮消息按角色拼接后传给一次新的网页会话，这与原生 Chat Completions 的角色隔离不同；不保证模型遵循每一条 system 指令。每次对话结束会尽力删除临时会话，失败时网页端可能残留会话。每账号每小时上限在本机执行，服务重启后重新计数；不代表上游认可的使用额度。同一时刻只向清言发送一个对话请求，最多等待 8 个请求，相邻请求至少间隔 1.5 秒，该间隔不保证免受上游限制。

`qingyan-default` 仅代表清言网页默认模式，不等同于特定付费 GLM 模型。当前不提供清言工具调用、图片/文件/音频、结构化输出约束、Responses、Anthropic 路由或显式深度思考切换；不支持的接口或内容会报错。温度、采样参数及输出 token 上限目前由网页默认行为决定，不作官方 API 等价保证。搜索默认读取连接设置，可用 `web_search_options.search_context_size: "none"` 关闭；真实上游是否遵循该选项仍待验证。

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

更新入口位于“连接设置 → 数据与版本”：支持启动时检查、手动检查和打开下载页。自动检查需要本机安装并登录 GitHub CLI，且有私有仓库访问权限；失败时可用浏览器登录 GitHub 后查看下载页。升级仍需退出旧程序、下载并解压完整新包，同一 Windows 用户沿用原有设置。

本版没有自动替换程序文件、开机自启、文件/图片专用管理界面或按客户端分别分配 API Key。DeepSeek 的工具调用继承上游的提示词与解析适配，不能等同于官方原生工具调用；清言暂不支持工具调用。本地接口就绪不代表每个账号长期可用；深度思考、搜索、模型能力和限流行为仍取决于上游。

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
