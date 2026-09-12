# FStudio_MCP

**让 AI 能够根据自然语言描述，自动生成和修改繁易触摸屏的界面。**

通过 MCP 协议连接 AI 客户端与 FStudio 3，调用原生工程、控件和编译接口，完成页面布局、属性配置、工程保存与隔离编译。

> 本项目采用 [MIT 许可证](LICENSE)，允许商用、修改、分发和售卖，须保留版权及许可声明。

## 最新更新 · v2026.09.12

- 新增画布快照、批量属性读取、共用属性设置和批量控件创建，减少 AI 逐项调用。
- 回传原生实际尺寸与调整标记，支持重名诊断、失败清理和布局检查。
- 提供配套 **Fstudio_skills**，串联原型拆解、公共页面、批量制作与分层验收。

查看 [完整更新说明与升级步骤](CHANGELOG.md)，或前往 [GitHub Release](https://github.com/TingRuazhang/FStudio_MCP/releases/tag/v2026.09.12) 下载源码和技能包。

## 功能

- 创建、打开和保存原生工程，管理页面和画布控件。
- 按自然语言需求生成布局，查询和修改控件位置、尺寸、外观及属性。
- 配置本地寄存器地址，以及工程中的采样、定时和数据传输模型。
- 导入图片、检查模型，保存并重新打开工程。
- 对已保存工程创建快照，运行原生隔离编译，查询或取消任务。

例如，可以向已连接 MCP 的 AI 提出：

> 创建一个用于学习的电机监控界面，包含标题、启动和停止按钮、状态灯与转速显示。使用本地寄存器，保存到新工程并检查原生编译结果。

完整工具和参数以 MCP `tools/list` 返回的接口为准。具体控件支持情况见下方验证范围。

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
   接续已有页面时，先用 `fstudio_canvas_snapshot` 获取控件名称、句柄和实际位置；必要时按稳定名称筛选，处理缺失或重名后再修改。
2. 熟悉的控件优先用 `fstudio_canvas_create_many` 一次创建、配置和插入，并用预设复用样式。首次探索控件仍用 `fstudio_canvas_prepare`、`fstudio_model_inspect` 确认属性，再用 `fstudio_model_get_many` / `fstudio_model_set_many` 按路径批量配置、`fstudio_canvas_commit` 插入；不用的草稿用 `fstudio_canvas_discard` 清理。已有同页控件可用 `fstudio_model_apply_many` 一次应用共用属性。
3. 返回 `job_id` 时，使用 `fstudio_visible_job` 查询同一任务至终态，不要重复提交写操作。
4. 调用 `fstudio_visible_save` 保存工程。模型句柄属于当前会话，重开工程后重新获取。
5. 调用 `fstudio_build_start`，再用 `fstudio_build_status` 查询。检查 `compiler_success`、`cleanup_complete`、`cleanup_errors` 和产物哈希；取消后也应继续查询至终态。

后台页面接口支持非保留的 Basic 页面；离线编辑前应关闭目标工程。编译使用已保存工程的快照，产物位于 `artifacts/isolated-jobs`，不会覆盖编辑器工程。

数值与文本控件配置见 [配置说明](docs/numeric-background-workflow.md)。
批量属性路径、调用开销和实测范围见 [高效模型操作](docs/efficient-model-workflow.md)。

## 配套技能

[Fstudio_skills](skills/fstudio-skills/SKILL.md) 将工艺/原型拆解、公共页面所有权、MCP 工具选择、批量制作和分层验收连成可执行流程，适用于不同 HMI 工程。技能中的 `check_layout.py` 只读检查快照的尺寸、对齐、间距和公共组件禁入名单，不修改工程或调用硬件。

将仓库的 `skills/fstudio-skills` 整个目录放入支持该格式的 AI 技能目录；Codex 使用 `~/.codex/skills/fstudio-skills`。调用示例：“使用 $fstudio-skills 根据原型增量修改触摸屏，复用公共页面，并检查布局与原生交互。”详细参考按需加载，无需把整个工具或属性目录发给 AI。

## 验证范围与已知限制

在 FStudio `3.0.15685.0`、F010 工程上检查了 69 个 MCP 接口：68 个完成代表性成功调用，1 个兼容入口按设计拒绝；全部接口经过非法参数和连接复用检查。

2026-09-12 新增批量路径读写、共用属性和批量绘图优化，验证范围及本地计时单独记录在 [高效模型操作](docs/efficient-model-workflow.md)。批量绘图以五种混合控件验证保存重开、尺寸回读与编译，不据此扩大全部控件或运行模式的覆盖结论。

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

本项目采用标准 [MIT 许可证](LICENSE)，允许使用、复制、修改、合并、发布、分发、再许可和售卖；须在软件副本或实质性部分中保留版权及许可声明。

**特别声明：FStudio 软件的版权归繁易公司所有。本项目的 MIT 许可证不适用于 FStudio 软件及其厂商 DLL、模板等资源，也不授予其使用、修改或分发权利；使用者须自行取得相应授权并遵守繁易公司的许可条款。**

本项目为独立项目，并非繁易官方产品，不代表繁易公司的认可或背书。相关商标及其他第三方代码和资源的权利归各自权利人所有。

MIT 授权仅涵盖本项目有权授权的内容，不授予第三方软件或资源的使用权。软件按现状提供，AI 生成结果需自行审查，不保证适用于真实设备。
