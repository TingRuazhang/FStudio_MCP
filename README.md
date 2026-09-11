# FStudio_MCP

**让 AI 能够根据自然语言描述，自动生成和修改繁易触摸屏的界面。**

通过 MCP 协议连接 AI 客户端与 FStudio 3，调用原生工程、控件和编译接口，完成页面布局、属性配置、工程保存与隔离编译。

> 仅供学习交流，请勿用于商业用途。使用前请阅读 [非商业学习交流许可](LICENSE)。

## 功能

- 创建、打开和保存原生工程，管理页面和画布控件。
- 按自然语言需求生成布局，查询和修改控件位置、尺寸、外观及属性。
- 配置本地寄存器地址，以及工程中的采样、定时和数据传输模型。
- 导入图片、检查模型，保存并重新打开工程。
- 对已保存工程创建快照，运行原生隔离编译，查询或取消任务。

例如，可以向已连接 MCP 的 AI 提出：

> 创建一个用于学习的电机监控界面，包含标题、启动和停止按钮、状态灯与转速显示。使用本地寄存器，保存到新工程并检查原生编译结果。

完整工具和参数以 MCP `tools/list` 返回的 69 个接口为准。具体控件支持情况见下方验证范围。

## 环境要求

- Windows、Python 3.11+（服务仅依赖标准库）。
- .NET Framework 4.x 的 x86 编译环境。
- 已合法安装的 FStudio 3；当前验证版本为 `3.0.15685.0`。
- 支持 stdio MCP 的 AI 客户端。

本仓库不分发 FStudio、厂商 DLL、模板、字体或其他第三方资源。

## 安装

建议克隆到较短的目录，例如 `C:\MCP\FStudio_MCP`。原生工程目录最多 90 个 UTF-16 单元，超过限制会提前拒绝创建或打开。

```powershell
git clone https://github.com/TingRuazhang/FStudio_MCP.git
cd FStudio_MCP
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -File '.\build.ps1'
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -File '.\install-visible-host.ps1'
```

默认安装位置为 `C:\Program Files (x86)\Flexem\FStudio 3.x`。自定义安装路径时使用：

```powershell
.\build.ps1 -FStudioBin 'D:\FStudio\Bin'
.\install-visible-host.ps1 -FStudioRoot 'D:\FStudio'
```

插件生成在 `artifacts/visible-addin`，通过 FStudio 的 `-addindir:` 参数加载，不复制到厂商安装目录。更新插件后，需保存工程并重启已加载旧 DLL 的宿主。

## 连接 AI 客户端

按实际路径修改 [mcp.example.json](mcp.example.json)，加入客户端的 MCP 配置：

```json
{
  "mcpServers": {
    "fstudio": {
      "command": "python",
      "args": [
        "C:/MCP/FStudio_MCP/server.py",
        "--workspace",
        "C:/MCP/FStudio_MCP/projects",
        "--fstudio-bin",
        "C:/Program Files (x86)/Flexem/FStudio 3.x/Bin"
      ]
    }
  }
}
```

`command` 可替换为 Python 解释器的完整路径。自定义工作区时，应同步修改 `artifacts/visible-addin/host-settings.json` 的 `workspace` 并重启宿主。

## 操作流程

1. 调用 `fstudio_visible_start` 连接或启动宿主，再创建或打开工作区工程。
2. 使用 `fstudio_canvas_prepare` 创建控件草稿，通过 `fstudio_model_inspect/get/set/set_many` 查询或配置，再调用 `fstudio_canvas_commit` 加入画布；不需要的草稿用 `fstudio_canvas_discard` 清理。
3. 返回 `job_id` 时，使用 `fstudio_visible_job` 查询同一任务至终态，不要重复提交写操作。
4. 调用 `fstudio_visible_save` 保存工程。模型句柄属于当前会话，重开工程后重新获取。
5. 调用 `fstudio_build_start`，再用 `fstudio_build_status` 查询。检查 `compiler_success`、`cleanup_complete`、`cleanup_errors` 和产物哈希；取消后也应继续查询至终态。

后台页面接口支持非保留的 Basic 页面；离线编辑前应关闭目标工程。编译使用已保存工程的快照，产物位于 `artifacts/isolated-jobs`，不会覆盖编辑器工程。

数值与文本控件配置见 [配置说明](docs/numeric-background-workflow.md)。

## 验证范围与已知限制

在 FStudio `3.0.15685.0`、F010 工程上检查了 69 个 MCP 接口：68 个完成代表性成功调用，1 个兼容入口按设计拒绝；全部接口经过非法参数和连接复用检查。

52 种绘图控件中，42 种通过创建、配置、提交、保存、重开及隔离编译，并通过产物与源工程保护检查。其余结果如下：

| 控件 | 当前限制 |
| --- | --- |
| Camera、DataTransmission、Pipe、Slider、StopWatch、StreamingMedia、TimePiece、Timer | 原生编译器报告图元类型不支持，已检查 16 / 32 位配置。 |
| WindowSelector | 当前原生版本反序列化时提前访问 PagingAddress，导致工程无法重开；已在创建草稿前保护性拒绝。 |
| WebCamera | 原生编译器报告 16 位色深不支持摄像头浏览；修改为 32 位并核验保存重开后仍未通过。 |

通过结果只覆盖已测配置。轨迹和饼图关闭了可选标签，GCodeEditor 使用本地设备 ID 验证编译。设备下载、PLC 通信、摄像头连接、实际触摸屏运行及全部参数组合未验证。`fstudio_validate_project` 仅执行结构检查。

`fstudio_visible_run_command` 为禁用的兼容入口，返回 `BACKGROUND_ONLY`。厂商内部接口可能随版本变化，升级 FStudio 后需重新验证。

## 源码目录

- `server.py`：MCP 协议、参数校验与工具分发。
- `visible_host.py`：宿主命名管道客户端。
- `build_snapshot.py`、`desktop_process.py`、`isolated_*.py`：快照与隔离编译任务管理。
- `src/`：原生桥接与宿主插件源码。
- `build.ps1`、`install-visible-host.ps1`：构建与本地插件安装。
- `docs/`：使用文档。
- `mcp.example.json`：客户端配置示例。

过程测试脚本、测试工程及报告不纳入源码分发。运行生成的 `artifacts/`、`projects/`、缓存和本地配置均由 `.gitignore` 排除。

## 版权与使用声明

Copyright (c) 2026 TingRuazhang and FStudio_MCP contributors.

**仅供学习交流，请勿用于商业用途。** 本项目允许非商业学习、教学、研究、修改和分享；须保留版权与完整许可声明，并标明修改。详细条件以 [LICENSE](LICENSE) 为准。

本项目为独立项目，并非繁易官方产品，不代表厂商认可或背书。FStudio 及相关商标、第三方代码和资源的权利归各自权利人所有。

软件按现状提供，AI 生成结果需自行审查，不保证适用于真实设备。禁止商用的许可属于源码公开许可，不属于 [OSI 定义的开源许可](https://opensource.org/osd)。
