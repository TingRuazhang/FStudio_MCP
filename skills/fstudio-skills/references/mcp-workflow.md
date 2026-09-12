# 用技能高效驱动 MCP

## 先选择最低调用成本的通路

先用当前连接提供的 schema 核对要用的工具。以下名称是 FStudio MCP 的业务名，连接器可能加命名空间。只缺一个新接口时使用兼容通路，不把接口缺失误判为工程损坏。

| 能力缺失 | 兼容通路 |
| --- | --- |
| `fstudio_canvas_snapshot` | `canvas_state` 取 graphics 句柄，`model_get` 分页取成员，再分批 `model_get_many` 读取 Comment、UniqueId 和 Position 四字段 |
| `fstudio_model_get_many` 或点路径 | 用 `model_get` 逐层发现，缓存本会话中间句柄；不重复读取同一分支 |
| `fstudio_model_apply_many` | 将共用属性展开成一次 `model_set_many`，遵守总项数限制 |
| `fstudio_canvas_create_many` | 每控件一次 prepare、一批关联属性、一次 commit；不逐属性保存 |

Python 服务与宿主 DLL 必须同时支持新操作。DLL 被旧进程锁定时保留更新文件，正常保存和重启后加载；不要关闭用户未保存的工程来换取提速。

## 接续和局部修复

1. 根据实际工程及当前页确认 UUID。按稳定名称请求 `canvas_snapshot`，传 `page_uuid` 防止读错活动页。返回 `missing_names` 与 `duplicate_names` 时先处理身份，不选第一个同名控件。
2. 快照已有名称、UUID、类型、坐标和尺寸，不再为这些字段逐个 `model_get`。只对额外的已知属性调用 `model_get_many`，每批最多 100 项。
3. 相同修改用 `model_apply_many`；不同修改用 `model_set_many`。全部目标在同页或同草稿内，展开后最多 100 个属性。不要拆成每个字段一次调用。
4. 等待同一 `job_id` 到终态；只回读本轮改过的属性及受影响的邻接布局。完成一个逻辑批次再保存；仅对涉及的行为重开、编译或模拟。

`canvas_snapshot` 是当前页顶层控件的几何数据，不是截图：不包含继承的公共组件，也不展开组内子控件。`index` 是当前集合次序，不是稳定身份或触摸层级证据。编辑器对象路径是 `Position.Width` 等，不能从 XML 的 `DisplaySettings.Position` 猜路径。

## 批量创建

同一工具和模式有已验证样本后，把文字、样式、地址与最终边界组成声明。预设只复用设计及模式，不携带上一工程的 UUID、设备 ID 或未经核对的控制动作。

`fstudio_canvas_create_many` 参数示例（本地地址仅演示用，交付前按真实点表配置）：

```json
{
  "presets": {
    "input": {
      "tool": "Flexem.Studio.GraphicsDesigner.Tools.NumericCharTool",
      "tool_properties": {"OperationAttrib": "NumericInput"},
      "properties": [
        {"member": "NormalData.IntegerValue", "value": 4},
        {"member": "NormalData.DecimalValue", "value": 1}
      ]
    }
  },
  "widgets": [
    {
      "name": "settings.target", "preset": "input",
      "x": 240, "y": 120, "width": 180, "height": 56,
      "local_addresses": [{"member": "NormalData.ReadAddress", "address": 120}]
    },
    {
      "name": "settings.caption", "tool": "Flexem.Studio.GraphicsDesigner.Tools.StaticTextTool",
      "text": "目标值", "text_style": {"font_size": 18, "color": "#FFE0EDF8"},
      "x": 24, "y": 120, "width": 200, "height": 56
    }
  ]
}
```

每批 1–30 控件，最多 20 个预设；每控件最多 60 个属性、8 个本地地址，全批最多 1,200 个属性。`tool_properties` / `text_style` 按字段合并，`properties` / `local_addresses` 按路径合并，控件值覆盖预设。名称、坐标、尺寸逐控件明确填写；不要把身份或 Position 填进属性预设。

静态文字的 `text` / `text_style` 初始化当前语言条目，保留原生可编辑文本；它们只适用于 `StaticTextTool`，不等于自动翻译。其他控件的按钮标题、状态图、语言和动作要使用该控件真实模型配置，不能生搬静态文字路径。

批量接口先配置草稿，最后施加边界并插入。返回 `bounds_adjusted: true` 表示原生实际尺寸不同：检查字库、位数、字号、字重和最小尺寸，再调整相邻单位与卡片留白。实测某中英文标题从 140 扩为 174；这是特定字体与内容的结果，不是统一补偿常数。保存重开可能触发额外的原生计算，因此交付前还要回读一次。

特殊初始化器（状态按钮动作、采样绑定、图库资源等）不在批量声明支持范围内时，先走原有草稿通路，不靠任意方法调用或 XML 猜测绕过。创建接口不是 upsert：续做先查名称，已存在就更新。超时只查原任务；不要改名再创建来掩盖未知结果。

## 共享所有权与交互质量

跨页面工程只维护一份简短所有权表：公共页拥有背景、全局标题及导航；业务页拥有各自内容；键盘与弹窗按原生机制处理。公共页图元不再出现在业务页的生成声明中。名称可使用 `shared.*` 与业务页前缀，但应沿用已有项目规范。

检查两层证据：先分别读取所有者与业务页自身的控件，确认没有重复定义；再模拟切页确认公共层合成、选中状态、遮挡和触摸命中。快照不合成公共层，不能证明菜单已经显示或按键可用。当前批量创建和 page_* 修改接口保护保留页；不能绕过保护，也不能以每页复制公共栏作为替代。按已支持的公共页编辑机制实现，接口确实缺失时单独报告该限制。

输入必须是原生 Input 模式并配置真实类型、地址、缩放、限值和键盘；保存 `NumericInput` 只证明模式持久化，还要验证点按、提交、取消与越界。菜单或按钮优先让原生状态控件承载图片/文字和动作；不能把指示灯的存在当成切页或写入功能已完成。文字、单位和动态数值保持原生；仅必要插画使用经 alpha 检查的透明素材。

## 自动检查显式布局约束

将 `canvas_snapshot` 的工具成功结果保存为 JSON；若返回任务句柄，先等待得到实际快照。采集同页所有分页至 `next_offset: null`，采集期间不修改画布。脚本拒绝未采完、混页、筛选快照和重复 UUID，不对不完整数据给出布局通过结论。

约束文件只写本轮要验证的内容，示例 UUID 必须替换为快照中的真实值：

```json
{
  "page_uuid": "00000000-0000-0000-0000-000000000001",
  "tolerance": 1,
  "expected": [
    {"name": "settings.target", "bounds": {"x": 240, "y": 120, "width": 180, "height": 56}},
    {"name": "settings.caption", "type": "Flexem.Studio.Components.Graphicses.StaticTextInfo"}
  ],
  "forbidden_names": ["shared.background", "shared.header", "shared.navigation"],
  "align": [{"edge": "top", "names": ["settings.caption", "settings.target"]}],
  "gaps": [{"axis": "x", "before": "settings.caption", "after": "settings.target", "min": 12}]
}
```

```text
python <skill-dir>/scripts/check_layout.py --snapshot <task-dir>/snapshot.json --plan <task-dir>/layout-plan.json
```

分页时重复 `--snapshot`，例如 `--snapshot page-0.json --snapshot page-100.json`。退出码 0 表示声明范围无差异，1 表示发现问题，2 表示输入不完整或无效。结果写到标准输出，不修改源文件或工程。`expected.type` 为完整原生模型名，不是工具名；`forbidden_names` 应来自所有权表，不仅照抄示例的三个名字。

`align.edge` 支持 left/right/top/bottom；`gaps.axis` 支持 x/y，检查 after 的起点减去 before 的末端是否至少为 min。只对声明的对象关系做检查，不将背景与卡片等合法重叠全部判错。不分析组内子对象、旋转轮廓、字形基线或像素颜色；这些仍用相关模型和运行截图检查。

## 续做时只保存有用事实

复杂制作在任务目录复用一份现有记录：工程/原生版本、参考版本、页面 UUID 与所有权、稳定控件名、已验证路径与预设、必要素材与点表、最后任务 ID、验证层级及下一处具体差异。句柄仅作本会话缓存，不作为持久身份。小修复不强制新建记录，更不为每个调用生成一个脚本或报告。

验收通过后保留工程与必需资源，过程脚本集中在任务目录。性能对比同时记录调用次数和本地耗时，排除 AI/网络时间的测试不能当作整页制作提速倍数；样本成功不能推广为所有控件模式正常。
