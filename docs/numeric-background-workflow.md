# 数值／文本控件后台配置

本机 FStudio 3.0.15685.0、F010 的原生模型流程。每个写操作返回任务编号，需要读取 `fstudio_visible_job` 至完成后再用结果中的引用。不要以排队成功代替配置完成。

1. `fstudio_canvas_prepare` 指定 `Flexem.Studio.GraphicsDesigner.Tools.NumericCharTool`，在 `tool_properties.OperationAttrib` 中显式选择 `NumericDisplay`、`NumericInput`、`CharDisplay` 或 `CharInput`。
2. 从返回的 `model` 通过 `fstudio_model_get` 读取 `NormalData`。数值模式设置 `DataType`、`IntegerValue`、`DecimalValue`；文本模式读取 `CharSettingData` 设置 `CharCount`，再在其 `Encoding` 上设置编码。
3. 本地直接地址可调用 `fstudio_address_configure_local`，传入 ReadAddress 的 ref 和 address 索引。接口从当前工程原生工厂选择本地位／字寄存器，兼容 AddressInfo 和 AddressData，保留原有数据类型，返回实际设备、寄存器和地址。标签、索引、站号引用、偏移等高级地址须使用对应配置流程，接口会拒绝它们；不进行硬件读写。
4. 共用读写地址时设置 `IsReadAndWriteAddressDifferent=false`；此时 `WriteAddress` 可以为空，不要直接读取它的 Raw。需要独立写地址时，通过 `fstudio_model_call` 调用 ReadAddress 的 `Clone()`，把结果引用赋给 `NormalData.WriteAddress`，再打开 `IsReadAndWriteAddressDifferent` 并分别设置两个 Raw.MainAddress。
5. 输入范围使用 `NormalData.NumericLimit`：打开 `UseLimit`，分别设置 `MinLimit`、`MaxLimit` 的 `IsConstant=true` 与 `Constant` 数值。触摸键盘配置位于 `KeyBoardData.KeyBoard`，可设 `KeyMode=TouchControl`、`UsePopKeyBoard=Yes`；它配置的是触摸屏运行时行为，不会在编辑器中打开键盘。
6. 同一草稿／同一页面的关联属性可用 `fstudio_model_set_many` 一次设置，`member` 支持当前模型的点分隔路径；多个已知属性可通过 `fstudio_model_get_many` 一次读取，无需逐层取得句柄。路径以原生模型为准，不能直接照抄 XML 字段层级。`fstudio_canvas_commit` 给出控件名称与位置尺寸，原生画布直接更新。已提交控件的属性批次使用一次原生撤销事务。
7. 保存工程，重开回读配置，再执行 `fstudio_build_start` 并轮询对应 build_id。必须检查 `compiler_success=true`，不能只检查 status=completed。

上述步骤来自 F010 的特定配置样例，不代表全部模式已验证。Raw.DataType／Length 与控件的显示类型／字符长度是不同字段；当前没有通过运行时检查证明其多字读写解释。编译通过不代表 PLC 通信、实际写入、边界拒绝或运行时键盘已经验证。
