# 高效模型操作

MCP 的耗时不只是原生执行时间。逐属性工具调用会反复经历 AI 决策、协议传输、宿主任务排队和结果返回。旧接口读取深层属性需要每层取得一个 `$ref`，大批量制作时容易产生大量往返。

本次分析的一份 HMI 制作日志中，15,719 次业务调用有 11,294 次 `model_get`，占约 72%；日志不包含 `visible_job` 轮询。该数字描述此工作流，不代表所有工程，也不能据此比较 Figma 与 FStudio 底层执行速度。

## 先用快照定位和检查布局

`fstudio_canvas_snapshot` 一次返回当前页 UUID、名称、尺寸和顶层控件的稳定名称（原生 `Comment`）、UUID、模型类型、会话句柄与实际边界，无需逐个读取 Comment 或 Position：

```json
{
  "page_uuid": "00000000-0000-0000-0000-000000000001",
  "names": ["settings.target", "settings.caption"],
  "limit": 100
}
```

示例 UUID 和名称需替换为实际值。`page_uuid` 可省略，提供时拒绝读错活动页；`names` 是精确筛选，可省略以检查全页。最多返回 100 项，按 `next_offset` 获取后续匹配项；`missing_names` 和 `duplicate_names` 按完整匹配集计算，不把分页外的匹配误报缺失。重名返回诊断而非选择首个对象，修改前必须定位清楚。

`scope: active_page_top_level_only` 明确表示不合成公共页面、不递归组合图元内部，也不是渲染截图。检查共享所有权时分别取各页面自身内容，再做实际切页验证。`index` 仅用于显示顺序，不能用来定位以后要修改的对象。

新增接口已在真实 FStudio 副本中验证筛选、分页、重名、错误活动页拒绝、只读状态和重开后 UUID 一致。配套 [Fstudio_skills](../skills/fstudio-skills/SKILL.md) 提供工具选择方法与只读布局检查脚本；脚本需要完整分页数据，检查结果不代表运行交互通过。

## 按路径批量读取

先从 `canvas_state`、`canvas_prepare` 等取得真实模型句柄，首次发现字段后直接读取已知属性路径。下面的 `<model-ref>` 替换为当前模型句柄；示例中的 `Position` 来自已测矩形模型，不是 XML 的 `DisplaySettings.Position`。

`fstudio_model_get_many`：

```json
{
  "reads": [
    {"ref": "<model-ref>", "member": "Position.X"},
    {"ref": "<model-ref>", "member": "Position.Y"},
    {"ref": "<model-ref>", "member": "Position.Width"},
    {"ref": "<model-ref>", "member": "Position.Height"}
  ]
}
```

一次宿主 UI 任务返回有序 `results`，每项包含 `ref`、`member` 和 `value`。复杂值返回带作用域的句柄；集合不自动展开。已有 `fstudio_model_get` 也支持点分隔路径，仍保留其集合分页行为。

可跨模型读取同一批已知字段，例如一次读取多个控件的 `Comment`。每批最多 100 项，路径最多 16 段，不支持索引表达式或方法调用。任何路径失败都会让本次读取报错；它不返回可误认为完整的部分结果。读取走原有危险 getter 拒绝逻辑，不递归枚举对象。

## 从根句柄批量修改

`fstudio_model_set_many`：

```json
{
  "changes": [
    {"ref": "<model-ref>", "member": "Position.X", "value": 24},
    {"ref": "<model-ref>", "member": "Position.Y", "value": 96}
  ]
}
```

无需先获取 `Position` 的句柄。原来的单字段 `member` 仍可用。全部目标必须属于同一页面或草稿；路径、类型、同一目标的别名重复、父子对象替换冲突均在写入前检查。已有页面使用一个原生撤销事务；草稿失败时回滚。不能在同批替换 `Parent`，又修改旧对象 `Parent.Child`。

批量修改仍返回异步任务，查询同一 `job_id` 至终态后保存。工程重开后所有旧句柄都要重新获取。路径只能来自当前模型，不可假设 DTO/XML 与编辑器模型层级相同。
末级属性的父对象如果是值类型，批量写入会拒绝，避免仅改到反射装箱副本而错误报告成功；需要通过原生支持的方法更新其所属属性。

## 多个控件应用相同属性

`fstudio_model_apply_many` 把共用字段展开为同一个 `model_set_many` 事务，适合统一宽度、颜色或对齐到同一条边：

```json
{
  "refs": ["<model-ref-1>", "<model-ref-2>"],
  "properties": [
    {"member": "Position.X", "value": 24},
    {"member": "Position.Width", "value": 240}
  ]
}
```

展开后最多 100 个属性修改；重复句柄、重复路径和跨页面作用域会拒绝。各控件需要不同坐标时直接使用 `model_set_many`，不用为每个控件重复调用。

## 一次绘制并配置多个控件

已确认原生工具及属性后，使用 `fstudio_canvas_create_many` 把创建草稿、配置和插入合成一个任务。预设只在本次请求内共享，无需预先注册模板：

```json
{
  "presets": {
    "card": {
      "tool": "Flexem.Studio.GraphicsDesigner.Tools.RectangleTool",
      "properties": [
        {"member": "NormalInfo.Radius.UseRadius", "value": true},
        {"member": "NormalInfo.Radius.Radius", "value": 8}
      ]
    }
  },
  "widgets": [
    {"name": "card-1", "preset": "card", "x": 24, "y": 24, "width": 240, "height": 96},
    {"name": "card-2", "preset": "card", "x": 280, "y": 24, "width": 240, "height": 96},
    {
      "name": "caption", "tool": "Flexem.Studio.GraphicsDesigner.Tools.StaticTextTool",
      "x": 24, "y": 144, "width": 240, "height": 40,
      "text": "压力 Pressure",
      "text_style": {"font_name": "Microsoft YaHei", "font_size": 18, "color": "#FFE0EDF8", "bold": true}
    },
    {
      "name": "pressure-input", "tool": "Flexem.Studio.GraphicsDesigner.Tools.NumericCharTool",
      "tool_properties": {"OperationAttrib": "NumericInput"},
      "x": 280, "y": 144, "width": 240, "height": 56,
      "local_addresses": [{"member": "NormalData.ReadAddress", "address": 120}]
    }
  ]
}
```

每批 1–30 个控件，最多 20 个预设；每个控件最多 60 个属性、8 个本地地址，全批最多 1,200 个属性。`tool_properties` 和 `text_style` 按字段合并，`properties`、`local_addresses` 按 `member` 合并，控件值覆盖预设值。预设不克隆 UUID、项目引用或旧坐标；`name` 和几何尺寸必须逐控件明确提供。

`text` / `text_style` 仅用于原生静态文字控件，使用 Graphic 字库并初始化当前语言条目；需要翻译时再分别配置各语言的 `LabelContent`。这仍是可编辑的原生文字。`local_addresses` 使用原生本地地址初始化器，保持字/位类型；PLC 设备、转换比例和具体业务约束仍按真实点表配置。

宿主先完成整批草稿配置，再用一次原生插入和撤销组加入画布。结果按输入顺序返回 `created`，包含每项 `model` 句柄、`requested_bounds`、`actual_bounds` 和 `bounds_adjusted`。字体或原生最小尺寸可能改变宽高；`bounds_adjusted: true` 时应使用回读值检查对齐。控件名称在当前页必须唯一；它是创建接口，重复请求不会覆盖已有控件。超时后只查询原 `job_id`，不能改名重试来猜测是否创建成功。

配置失败释放本批草稿；插入失败会尝试撤销并核对原页成员。若原生清理未完成，会明确报错，应检查页面和草稿后处理。已有未提交草稿不属于本批清理范围。此接口支持现有原生绘图通路，不代表全部工具已经通过编译或运行验收；陌生控件仍先完成单实例验证。

## 使用顺序

1. 同一种组件首次读取必要属性和枚举，保留已确认的路径。
2. 用一次批量读取取得本轮需要的字段，不索取无关对象。
3. 新控件使用批量创建与共用预设；已有控件使用批量修改或共用属性，遵循字体、格式和尺寸的原生依赖。
4. 提交草稿后检查实际尺寸；完成一个逻辑批次再保存，不对每个标量重复保存和编译。
5. 仍按修改范围完成保存重开、编译和模拟检查；减少调用不能省略验证。

## 验证与边界

本次新增接口经过 schema 校验、独立进程中的批量行为检查，以及私有桌面 FStudio 工程副本的读取等价、原生事务、保存重开检查。性能测试对比已缓存 `Position` 句柄的四次单项读取与一次批量读取；交替执行七轮，结果必须相同。

另以 25 个控件的 100 项位置属性进行了七轮真实 stdio MCP 测试，基线已经缓存各控件的 `Position` 句柄。2026-09-12 最终版本验证中，单项读取中位数为 101.4 ms，批量为 7.8 ms，工具调用从 100 次变为 1 次；同日此前一轮为 113.8 ms 与 8.3 ms。四字段的小样本在不同轮次中波动明显，不能据此承诺固定提速倍数。

同日批量绘图验证包含矩形、原生中英文文字、数值显示、数值输入和椭圆：保存重开后保留五个独立 UUID，输入模式和本地地址正确，原生编译成功。四种失败批次检查了非法属性、错误文字工具、非法地址路径和页面重名，均保持原页成员与草稿数量。窄文字区域从请求 140 扩宽到原生 174，回读和 `bounds_adjusted` 正确报告该变化；未将其误报为完全按请求尺寸插入。这些结果不等于触摸输入、模拟器视觉或硬件运行验收。

创建同样十个矩形、各配置一个圆角开关属性，交替执行三轮：原先每个控件 `prepare → set_many → commit` 共 30 次业务调用，中位数 1,237.5 ms；新批量接口一次调用，中位数 71.4 ms。此前一轮为 965.0 ms 与 70.1 ms，体现本机调度波动。相同控件数量和属性配置下比较本地调用耗时，轮询不计作业务调用。

原生测试脚本集中在 `tests/performance/`，输出在 `artifacts/performance/`。该计时只涵盖本地宿主管道与 UI 调度，不含 AI 思考、网络、工程编译或硬件运行，因此不能直接称作整套 HMI 制作提速倍数。

客户端只读任务的等待从固定 100 毫秒改为 5、10、20、40、50 毫秒退避，较慢任务后续保持最多 50 毫秒的间隔。仍只提交一次操作，五秒等待预算结束时保留原 `job_id`；传输超时也不授权重新提交写操作。

更新后要让 MCP 服务加载新 `server.py`，并让宿主加载新 DLL；已运行的旧宿主不会热更新。不能只更新 Python 后向旧 DLL 发送新操作。安装脚本在 DLL 被锁定时暂存更新，保存用户工程后按正常流程重启加载，不强制关闭用户应用。
