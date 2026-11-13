# 一键润色（PolishKey）

选中任意输入框里的文字，按 `Ctrl+Alt+P`：取走选中文字 → 交给 DSH 里的模型改写 → 直接粘贴替换。
替换是普通粘贴，`Ctrl+Z` 可以撤销。

## 它由两块组成

| 组件 | 位置 | 作用 |
| --- | --- | --- |
| 桥接插件 `dsh-polish-bridge` | `polish-bridge/`（部署在 `%USERPROFILE%\.dsh\profiles\desktop\node_modules\dsh-polish-bridge`） | 在 `127.0.0.1:8791` 开一个只有本机能访问的小接口，用宿主自己的 `ctx.llm` 调模型 |
| 托盘工具 `PolishKey.exe` | `polish-key/`（产物 `polish-key/out/PolishKey.exe`） | 注册全局热键，复制选区、发请求、粘贴结果、顶部提示 |

没有第二份 API Key：模型凭据一直在 DSH 宿主里，桥接只做转发。

## 接口

```
GET  /health                     -> {ok, port, provider, model, style}
POST /polish  {text, style?}     -> {ok, text, ms}
POST /config  {provider, model, style}
```

`style` 三选一：`chat`（聊天消息，默认）、`mail`（邮件正文）、`brief`（三条要点）。

手工试：

```powershell
Invoke-RestMethod http://127.0.0.1:8791/polish -Method Post -ContentType application/json `
  -Body ([Text.Encoding]::UTF8.GetBytes('{"text":"那个我明天上午十点才能到 路上有点堵"}'))
```

## 托盘工具

- 双击托盘图标或按热键都触发；托盘菜单可切模式、看桥接状态、开/关开机自启。
- 配置：`%LOCALAPPDATA%\PolishKey\config.json`（端口、模式、是否允许「没选中就全选」兜底）。
- 失败时不会吃掉你的剪贴板：会还原原来的内容，并在顶部提示原因。

## 已验证

- 桥接：`deepseek-account` / `deepseek-flash`，2–4 秒返回；三种模式输出都正常。
- 托盘：`RegisterHotKey` 成功占用 `Ctrl+Alt+P`（重复注册返回 1409）。
- 未验证：在你真实的应用里（微信输入法）走完「复制→替换」这一整条。这一步要你亲手试一次。

## 重新安装 / 升级后

```powershell
# 插件文件改了之后（热加载不一定生效，重启 DSH 最稳）
Copy-Item polish-bridge\lib\index.js `
  "$env:USERPROFILE\.dsh\profiles\desktop\node_modules\dsh-polish-bridge\lib\index.js" -Force

# 托盘工具重新编译
polish-key\src\build.cmd
```

DSH 没开的时候，热键仍会响应，但会提示「桥接未连接」。
