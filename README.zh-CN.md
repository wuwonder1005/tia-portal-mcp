# TIA Portal MCP Server（中文说明）

把西门子 **TIA Portal V20** 接入任意支持 MCP 的 AI 助手（DeepSeek Harness、Claude Desktop、Claude Code、VS Code、Cursor、Codex 等）。用自然语言就能：导入 SCL 程序块、管理 PLC 变量表、编译、导出程序块、下载到真实 PLC。

交付物是一个**单文件 EXE**：`bin/TiaMcpServer.exe`（约 66 KB）。无需安装 Python / Node / 任何第三方库——只依赖 Windows 自带的 .NET Framework 4.8，以及本机已装、带 Openness 的 TIA Portal V20。

---

## 功能

- **导入程序**：粘贴 SCL 源码即可生成 FB/FC/OB/DB/UDT（中文标识符与注释原样保留）。
- **管理变量表**：批量增/改 PLC 变量（`tia_upsert_tags`），表不存在自动创建。
- **编译**：逐条返回错误/警告，并带块路径。
- **回读/导出**：任意块导出为 SCL 或 XML。
- **项目**：打开/新建/保存/关闭，列设备与程序块，从硬件目录添加 PLC。
- **下载到 PLC**：显式 `confirm=true` 才执行（会停机）。
- **诊断**：连接状态、进程/会话/账号/组、残留进程清理。

## 为什么用 HTTP 而不是 stdio

会以子进程方式拉起 MCP 的客户端（典型如 DeepSeek Harness）会把子进程放进沙箱，沙箱里只能看到寥寥几个进程，根本够不着博途。所以服务器**独立运行**在本机，客户端经 **Streamable HTTP** 连接：

```
TIA Portal V20 ──Openness──> TiaMcpServer.exe (http://127.0.0.1:8756/mcp) <── 任意 MCP 客户端
```

## 环境要求

- Windows 10/11（使用内置 .NET Framework 4.8）
- Siemens TIA Portal **V20**，且安装了 **Openness** 组件
- 当前 Windows 账号在 **`Siemens TIA Openness`** 组内（加入后需注销重登录）
- 程序需**以管理员身份运行**（双击会自动弹 UAC 提权）
- 把 exe 放在**可写目录**（它会在旁边写日志和临时目录）

## 快速开始

1. 双击 `bin\TiaMcpServer.exe`，UAC 点“是”。控制台显示 `listening on http://127.0.0.1:8756/mcp`。
2. 让 MCP 客户端连接该端点。DeepSeek Harness 的配置
   （`~/.dsh/profiles/desktop/cordis.patch.yml`）：

```yaml
- insert:
    - id: mcp-tia
      name: "@deepseek-ai/dsh-mcp-client"
      config:
        serverName: tia
        transport: streamable-http
        url: http://127.0.0.1:8756/mcp
        toolCallTimeoutMs: 950000
        failOnStartupError: false
```

Claude Desktop 则写到 `%APPDATA%\Claude\claude_desktop_config.json`：

```json
{
  "mcpServers": {
    "tia": { "type": "http", "url": "http://127.0.0.1:8756/mcp" }
  }
}
```

3. 直接提需求，例如：“打开 D:\Projects\Line1\Line1.ap20，把这个 SCL 导入 PLC_1，然后编译。”

### 开机自启（Windows）

- `install.cmd` —— 在“启动”文件夹创建带“以管理员身份运行”标记的快捷方式。
- `uninstall.cmd` —— 移除。

登录时会弹一次 UAC，点“是”后服务器在后台静默运行。

## 命令行

| 命令 | 作用 |
|---|---|
| `TiaMcpServer.exe` | 启动 HTTP 服务（默认端口 8756） |
| `TiaMcpServer.exe --port 9000` | 指定端口 |
| `TiaMcpServer.exe --status` | 打印连接状态（JSON） |
| `TiaMcpServer.exe --selftest [目录]` | 端到端自测（新建演示项目） |
| `TiaMcpServer.exe --help` | 帮助 |

## 工具一览

| 工具 | 作用 |
|---|---|
| `tia_status`、`tia_env` | 连接状态 / 诊断 |
| `tia_open_project`、`tia_create_project`、`tia_close_project`、`tia_save_project` | 项目 |
| `tia_list_devices`、`tia_list_blocks` | 设备与程序块 |
| `tia_import_scl` | **导入 SCL** → 生成块（自动保存） |
| `tia_export_block` | 导出块为 SCL 或 XML |
| `tia_delete_block` | 删除块并保存 |
| `tia_compile` | 编译，逐条返回错误/警告 |
| `tia_list_tag_tables`、`tia_read_tags`、`tia_upsert_tags` | 变量表 / **批量导入** |
| `tia_download` | 下载到真实 PLC（需 `confirm=true`） |
| `tia_add_plc`、`tia_launch_ui`、`tia_kill_portals` | 加设备 / 拉起界面 / 清理残留进程 |

## 从源码构建

不需要装 SDK——用 Windows 自带的 .NET Framework 编译器：

```
build.cmd
```

`build.cmd`/`build.ps1` 会自动定位 Openness 的 `Siemens.Engineering.dll`
（注册表 → 环境变量 `TiaPortalLocation` → 常见安装目录）。

## 架构 / 源码结构

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口、命令行、UAC 自提权、临时目录重定向、程序集解析 |
| `Protocol.cs` | MCP JSON-RPC、工具声明与分发 |
| `Engine.cs` | 全部 Openness 调用 |
| `HttpTransport.cs` | localhost HTTP/1.1（keep-alive、按字节解析请求体） |
| `TiaLocator.cs` | 定位 TIA 安装目录 |
| `Json.cs`、`AssemblyInfo.cs` | JSON 封装 / 版本信息 |

## 排错

| 现象 | 原因 / 解决 |
|---|---|
| 约 8 秒后 `Connection to TiaPortal failed.` | 未提权。以管理员身份运行。 |
| `EngineeringSecurityException: This process is not permitted to connect to TIA-Portal` | 用计划任务启动（其令牌过不了 Openness 的组检查）。改用手动/UAC 启动或自启快捷方式，不要用计划任务。 |
| 启动报 `%TEMP%\<guid>\System` 访问被拒绝 | 安全软件拦截提权进程写用户 Temp。程序已把 TEMP 重定向到 `bin\runtime-temp`；仍失败就把 exe 移到可写目录。 |
| 中文路径 `fetch failed` | 本版本已修复，请升级。 |
| Openness 看不到任何进程 | 必须在 `Siemens TIA Openness` 组内，且加入后重新登录。 |

日志在 exe 旁边（`tia-mcp.log`）。

## 许可证

MIT —— 见 [LICENSE](LICENSE)。
