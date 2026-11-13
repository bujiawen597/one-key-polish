# 一键润色（one-key-polish）

在任意输入框里选中一段草稿，按 **`Ctrl+Alt+P`**：它把这段字改写成能直接发出去的话，然后**就地替换**。
替换就是一次普通粘贴，`Ctrl+Z` 可以撤销。

它解决的是「说得出、发不出」：语音输入只负责把话变成字，字还是啰嗦的、没重点的。这个工具负责第二步。

```
选中文字 → Ctrl+Alt+P → 复制 → POST 127.0.0.1:8791/polish → 改写 → 粘贴回去
```

## 两个部件

| 部件 | 位置 | 作用 |
| --- | --- | --- |
| `dsh-polish-bridge` | `polish-bridge/` | DSH 宿主插件。在 `127.0.0.1:8791` 开一个只有本机能访问的接口，用**宿主自己的模型凭据**（`ctx.llm`）发请求 |
| `PolishKey.exe` | `polish-key/` | Windows 托盘工具。注册全局热键，复制选区、发请求、把结果粘贴回原处，失败时顶部提示 |

没有第二份 API Key，也不需要单独配 provider：模型凭据一直在 DSH 里，桥接只做转发。代价是 **DSH 得开着**。

## 安装

**1. 装桥接插件**（需要 DSH Desktop）

```powershell
$src = ".\polish-bridge"
$dst = "$env:USERPROFILE\.dsh\profiles\desktop\node_modules\dsh-polish-bridge"
Copy-Item $src $dst -Recurse -Force
```

然后编辑 `%USERPROFILE%\.dsh\profiles\desktop\package.json`：

- `dependencies` 里加 `"dsh-polish-bridge": "file:./node_modules/dsh-polish-bridge"`
- `dsh.profile.bundles` 数组末尾加 `"dsh-polish-bridge"`

改完 DSH 会热加载（端口 8791 由宿主进程监听）；不生效就重启 DSH。

**2. 编译托盘工具**（Windows 自带 .NET Framework 4 即可，不用装 SDK）

```bat
polish-key\src\build.cmd
```

产物在 `polish-key\out\PolishKey.exe`。

**3. 运行**，托盘出现「润」字图标。右键菜单可以切模式、看桥接状态、打开开机自启。

## 用法

选中文字 → `Ctrl+Alt+P`。三种模式（托盘右键切换，也可以每次指定）：

| 模式 | 产出 |
| --- | --- |
| `chat` 聊天消息（默认） | 2–5 句，结论在前，去掉口头语 |
| `mail` 邮件 | 一句来意 + 必要背景 + 明确请求，不写称呼署名 |
| `brief` 要点 | 最多三条，每条一行 |

没选中文字时，它会退一步按 `Ctrl+A` 再复制（可在配置里关掉这个兜底）。

失败不会吃掉你原来的剪贴板：会还原，并在屏幕顶部说明原因（比如 DSH 没开、桥接没起来）。

## HTTP 接口

```http
GET  /health                  -> { ok, port, provider, model, style }
POST /polish   { text, style? } -> { ok, text, provider, model, ms }
POST /config   { provider, model, style }
```

手工试一下：

```powershell
Invoke-RestMethod http://127.0.0.1:8791/polish -Method Post -ContentType application/json `
  -Body ([Text.Encoding]::UTF8.GetBytes('{"text":"那个我明天上午十点才能到 路上有点堵"}'))
```

单次上限 6000 字符，请求体上限 256 KB，只监听回环地址。

## 配置

- 桥接：`%LOCALAPPDATA%\dsh-polish\config.json`（provider、model、默认风格、端口）
- 托盘：`%LOCALAPPDATA%\PolishKey\config.json`（端口、模式、全选兜底）
- 日志：`%LOCALAPPDATA%\dsh-polish\bridge.log` —— 只记长度和耗时，**不记正文**。

## 已知限制

- Windows only：全局热键走 `RegisterHotKey`，输入走 `SendInput`。
- DSH 必须运行；桥接插件停止时热键仍响应，但会提示「桥接未连接」。
- 模型调用走宿主默认的 DeepSeek 路由（`deepseek-account` / 带 `flash` 的模型优先），单次 2–4 秒。
- 插件随 DSH 版本走，DSH 大版本升级后可能需要跟着调 API（见 `polish-bridge/lib/index.js` 顶部注释）。

## License

MIT
