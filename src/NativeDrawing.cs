using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FStudioMcp {
 internal static partial class VisibleHost {
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr handle);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
  /// <summary>显示已有工作台画布；后台启动尚未创建窗口句柄时，以不激活方式完成首次显示。</summary>
  static object ShowCanvasWithoutActivation() {
   RequireProject();
   var window=Application.Current.Windows.Cast<Window>().Single(w=>w.GetType().FullName=="ICSharpCode.SharpDevelop.Gui.WpfWorkbench");
   var before=GetForegroundWindow();var handle=new WindowInteropHelper(window).Handle;
   bool initialized=handle==IntPtr.Zero;
   if(initialized){
    // 使用已存在的 WPF 工作台对象完成首次布局，不构造第二个工作台，也不抢占焦点。
    bool activate=window.ShowActivated;
    try{window.ShowActivated=false;window.Show();handle=new WindowInteropHelper(window).Handle;}
    finally{window.ShowActivated=activate;}
   }
   if(handle==IntPtr.Zero)throw new InvalidOperationException("Native canvas window handle initialization failed");
   bool minimized=IsIconic(handle);
   // 已显示过的窗口用 SW_SHOWNOACTIVATE 恢复尺寸，保持当前应用焦点。
   if(minimized||window.WindowState==WindowState.Minimized||!IsWindowVisible(handle)){
    // WPF 的 WindowState 也必须复位；只调用 Win32 ShowWindow 会被绑定状态重新最小化。
    // 保持 ShowActivated=false，恢复画布时不抢占用户当前应用的键盘焦点。
    bool activate=window.ShowActivated;
    try{window.ShowActivated=false;window.WindowState=WindowState.Maximized;ShowWindow(handle,4);}
    finally{window.ShowActivated=activate;}
   }
   return Obj("visible",IsWindowVisible(handle),"minimized",IsIconic(handle),"was_minimized",minimized,"handle_initialized",initialized,"foreground_preserved",before==GetForegroundWindow(),"settings_operation_dispatched",false,"mouse_input_used",false);
  }
  sealed class DrawingDraft {public object Model, ViewModel, Tool, Page, CommittedView;}
  static readonly Dictionary<string,DrawingDraft> drawingDrafts=new Dictionary<string,DrawingDraft>();
  static object CallNative(object target,string name,params object[] args){
   var methods=target.GetType().GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(m=>m.Name==name&&!m.ContainsGenericParameters&&m.GetParameters().Length==args.Length&&m.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(x=>x)).ToArray();
   if(methods.Length!=1)throw new InvalidOperationException("Ambiguous native call: "+target.GetType().FullName+"."+name);
   return methods[0].Invoke(target,args);
  }
  static object DesignerView(){
   RequireProject();var view=Member(Resolve("Flexem.Studio.Workbench.IWorkbench"),"ActiveViewContent");
   if(view==null||view.GetType().FullName!="Flexem.Studio.HMI.Window.HMIWindowViewContent")throw new InvalidOperationException("An HMI page must be open in the designer");
   return view;
  }
  static object DesignerService(object context,string type){return CallNative(Member(context,"Services"),"GetService",Native(type));}
  static void NativeSet(object target,string property,object value){var p=target.GetType().GetProperty(property);if(p==null||p.GetSetMethod()==null)throw new ArgumentException("Native property is not publicly writable: "+property);p.SetValue(target,ConvertArg(value,p.PropertyType),null);}
  static MethodInfo ToolHook(object tool,string name,int count){return tool.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SingleOrDefault(m=>m.Name==name&&m.GetParameters().Length==count);}
  /// <summary>补齐后台草稿的配方子模型；原生工具默认值省略这些数据，选定配方后会在保存时空引用。</summary>
  /// <param name="model">已关联当前项目、尚未加入画布的原生模型。</param>
  /// <exception cref="InvalidOperationException">项目未配置配方所需的显示语言。</exception>
  static void InitializeDrawingModel(object model){
   if(model.GetType().FullName!="Flexem.Studio.Components.Parts.RecipePart.RecipeDisplayInfo")return;
   var setting=Member(model,"RecipeSetting");
   var languages=Member(PageServices(),"LanguageConfigurationService");
   var first=((IEnumerable)Member(languages,"Configurations")).Cast<object>().FirstOrDefault();
   if(first==null)throw new InvalidOperationException("Recipe drawing requires a configured project language");
   // 配置项显式实现接口的 Id，不能按具体类型的公共属性读取。
   var languageContract=Native("Flexem.Studio.Hmi.Configurations.Language.IReadonlyLanguageConfigItem");
   int languageId=Convert.ToInt32(languageContract.GetProperty("Id").GetValue(first,null));
   int format=Convert.ToInt32(Member(setting,"DisplayFormat"));
   // 与原生属性编辑器采用相同的构造函数，仅初始化缺失字段，不覆盖已有配方配置。
   const string prefix="Flexem.Studio.Hmi.Models.Components.Parts.RecipePart.DataConfig.";
   if(Member(setting,"RecipeCommonSetting")==null)NativeSet(setting,"RecipeCommonSetting",Activator.CreateInstance(Native(prefix+"RecipeCommonSettingData"),new object[]{languageId,format}));
   if(Member(setting,"RecipeItemSettingList")==null)NativeSet(setting,"RecipeItemSettingList",Activator.CreateInstance(Native(prefix+"RecipeItemSettingDataList"),new object[]{languages,format}));
  }
  /// <summary>用原生工具创建完整草稿；静态文字批量通路直接初始化语言数据，避免无窗口的属性面板留下全局状态。</summary>
  static DrawingDraft CreateDrawingDraft(Dictionary<string,object>a,object page,object context,bool batch=false){
    var type=Native(S(a,"tool"));
    if(type.Namespace!="Flexem.Studio.GraphicsDesigner.Tools"||type.IsAbstract||type.ContainsGenericParameters||!type.Name.EndsWith("Tool"))throw new ArgumentException("Expected a concrete native drawing tool");
    // 此版本窗口选择器的 Child_Fetch 在载入 PagingAddress 前调用 OnFetching/Init，触发空引用。
    // 在创建草稿前拒绝，避免把无法再次打开的控件写入用户工程；未来版本不套用此限制。
    if(type.Name=="WindowSelectorTool"&&type.Assembly.GetName().Version.ToString()=="3.0.15685.0")
     throw new NotSupportedException("NATIVE_UNSUPPORTED: WindowSelectorTool in FStudio 3.0.15685.0 accesses PagingAddress before deserialization and prevents project reopening; draft creation was refused to preserve the project");
    var tool=Activator.CreateInstance(type);
    // 工具钩子需要当前设计上下文，但 Activate 会打开属性面板，因此只注入已有上下文。
    for(var baseType=type;baseType!=null;baseType=baseType.BaseType)foreach(var field in baseType.GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly)){
     if(field.FieldType.FullName=="ICSharpCode.WpfDesign.DesignContext")field.SetValue(tool,context);
     if(field.FieldType.FullName=="ICSharpCode.WpfDesign.IDesignPanel")field.SetValue(tool,Member(Member(context,"Services"),"DesignPanel"));
    }
    if(a.ContainsKey("tool_properties"))foreach(var p in (Dictionary<string,object>)a["tool_properties"])NativeSet(tool,p.Key,p.Value);
    var create=ToolHook(tool,"CreateModel",0);if(create==null)throw new ArgumentException("Native tool has no model creation hook");
    var model=create.Invoke(tool,new object[0]);
    object vm;
    try{
     var project=Member(Resolve("Flexem.Studio.Hmi.Project.IHmiProjectService"),"CurrentProject");
     if(!(bool)Member(model,"IsReferenced"))CallNative(model,"EnableReference",project);
     InitializeDrawingModel(model);
     var createVm=ToolHook(tool,"CreateViewModel",1);
     // 基础几何模型自行初始化属性，无需调用属性面板工厂。
     bool basicShape=new[]{"LineTool","PolyLineTool","PolygonTool","RectangleTool","EllipseTool","ArcTool","SectorTool","SectorRingTool"}.Contains(tool.GetType().Name);
     // 批量文字在 ConfigureBatchText 中使用原生语言/字体工厂补全；省去面板视图模型的文本库扫描与事件订阅。
     vm=basicShape||(batch&&type.Name=="StaticTextTool")?null:createVm==null?Native("Flexem.Studio.GraphicsDesigner.PropertyViewModelFactory").GetMethod("GetViewModel",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{model}):createVm.Invoke(tool,new object[]{model});
     // SettingFirstCreateComponentTool 的默认 CreateViewModel 返回 null；原生 ShowSetting 此时回退到工厂。
     // 批量数值输入等工具的 BeforeShowSetting 依赖真正的属性视图模型；旧 prepare 不调用此钩子，保留其原有返回语义。
     if(batch&&vm==null&&!basicShape&&type.Name!="StaticTextTool")vm=Native("Flexem.Studio.GraphicsDesigner.PropertyViewModelFactory").GetMethod("GetViewModel",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{model});
    }catch(Exception error){
     // 草稿尚未登记，客户端无法显式丢弃；此处释放失败初始化建立的项目引用。
     try{if((bool)Member(model,"IsReferenced"))CallNative(model,"DisableReference");}
     catch(Exception cleanup){throw new AggregateException("Native draft initialization and reference cleanup failed",error,cleanup);}
     throw;
    }
   return new DrawingDraft{Model=model,ViewModel=vm,Tool=tool,Page=page};
  }
  static object ConfigureLocalAddress(Dictionary<string,object>a,object view) {
   var address=Ref(a);string type=address.GetType().FullName;
   bool legacy=type=="Flexem.Studio.Models.HMI.AddressInfo";
   if(!legacy&&type!="Flexem.Studio.Hmi.Models.Address.AddressData")throw new ArgumentException("Expected an AddressInfo or AddressData handle");
   var scope=OwningScope(S(a,"ref"));var draft=scope as DrawingDraft;
   if(scope==null||(draft==null&&!Object.ReferenceEquals(scope,view))||(draft!=null&&!Object.ReferenceEquals(draft.Page,Member(view,"Model"))))throw new ArgumentException("Address must belong to the active page or its draft");
   object supplied=a["address"];
   if(supplied==null||supplied is bool||supplied is string)throw new ArgumentException("address must be an unsigned integer");
   double number=Convert.ToDouble(supplied);
   if(Double.IsNaN(number)||Double.IsInfinity(number)||number<0||number>UInt32.MaxValue||number!=Math.Truncate(number))throw new ArgumentException("address must be an unsigned integer");
   var raw=legacy?address:Member(address,"Raw");
   if(raw==null)throw new ArgumentException("Address Raw is not initialized");
   // 字内位是可转换的直接地址形式，不能与标签/索引/变量站号等高级引用混为一谈。
   // 后续使用本地 CreateBit/CreateWord 的原生默认值清理其标志，允许 RTU 字内位安全切回 LB。
   // 旧模型的配方设备通过 FormulaRegisterId 查找寄存器；标签设备也不能当作普通数字地址转换。
   // 仅检查启用标志，不清除休眠的配方/引用对象，避免更改与本次转换无关的配置。
   foreach(var target in new[]{address,raw}.Distinct())foreach(var flag in new[]{"IsLabelAddress","UseIndex","UseStationAddress","UseStationReference","UseTagByteReference","UseOffset","UseAddressReference","IsCombinedBits","IsVariableTagDevice","IsTagDevice","IsRecipeDevice","IsFormula"}){
    var property=target.GetType().GetProperty(flag);
    if(property!=null&&property.PropertyType==typeof(bool)&&(bool)property.GetValue(target,null))throw new ArgumentException("Configure local address requires a direct address; advanced option is active: "+flag);
   }
   string kind=Convert.ToString(Member(address,"AddressType"));
   if(kind!="Bit"&&kind!="Word")throw new ArgumentException("Unsupported native address type: "+kind);
   if(kind=="Word"&&(bool)Member(raw,legacy?"UseByteReference":"UseWordRegisterAsBit"))
    throw new ArgumentException("A Word address cannot be normalized from an active holding-register-bit mode");
   var devices=Member(PageServices(),"DeviceService");
   var source=kind=="Bit"?PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateBit",devices):PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateWord",devices,Member(raw,"DataType"),true);
   var sourceRaw=Member(source,"Raw");var register=Member(sourceRaw,"RegisterId");
   object dataType=Member(raw,"DataType");string id=S(a,"ref"),prefix=legacy?"":"Raw.";
   // 始终通过地址根路径写入：若某个 setter 替换 Raw，统一批量入口会检测到，而不是继续修改脱离的旧对象。
   // UseByteReference/UseWordRegisterAsBit 必须恢复为本地工厂的值，否则 LB 会继续被解释为 4X 字内位。
   var edits=new[]{
    // 先切换字内位模式：旧模型的该属性规则会刷新寄存器，显式官方 RegisterId 必须随后写入以免被覆盖。
    Obj("ref",id,"member",prefix+(legacy?"UseByteReference":"UseWordRegisterAsBit"),"value",Member(sourceRaw,"UseWordRegisterAsBit")),
    Obj("ref",id,"member",prefix+"DeviceId","value",Member(sourceRaw,"DeviceId")),
    Obj("ref",id,"member",prefix+"RegisterId","value",legacy?Member(register,"Id"):register),
    Obj("ref",id,"member",prefix+"MainAddress","value",Convert.ToUInt32(number)),
    Obj("ref",id,"member",prefix+(legacy?"StationNO":"StationNo"),"value",Member(sourceRaw,"StationNo")),
    Obj("ref",id,"member",prefix+"SubAddress","value",Member(sourceRaw,"SubAddress")),
    Obj("ref",id,"member",prefix+"SubAddressIndex","value",Member(sourceRaw,"SubAddressIndex")),
    Obj("ref",id,"member",prefix+"DataType","value",dataType)
   };
   var changed=(Dictionary<string,object>)SetModelProperties(Obj("changes",edits));
   // 旧 AddressInfo 还保存派生缩写与规则结果；设备、寄存器及字内位模式改变后同步刷新。
   if(legacy){CallNative(address,"UpdateAbbreviation");CallNative(address,"UpdateDeviceAbbreviation");CallNative(address,"CheckRules");}
   // 回读必须重新解析当前子模型。写入前取得的 Raw 可能已被原生 setter 替换，不能用旧引用证明成功。
   raw=legacy?address:Member(address,"Raw");
   var actualRegister=Member(raw,"RegisterId");
   return Obj("model",DescribeOwned(address,scope),"address_type",kind,"device_id",Convert.ToString(Member(raw,"DeviceId")),"register_id",legacy?actualRegister:Member(actualRegister,"Id"),"address",Member(raw,"MainAddress"),"data_type",Convert.ToString(Member(raw,"DataType")),"native_undo_transaction",changed["native_undo_transaction"],"persistence","save_project_to_persist","hardware_io",false);
  }
  /// <summary>校验 RTU 工具的整数参数；拒绝布尔、字符串、小数和非有限数，避免宽松转换截断地址。</summary>
  static uint ModbusRtuInteger(Dictionary<string,object>a,string key,uint minimum,uint maximum){
   object supplied;if(!a.TryGetValue(key,out supplied)||supplied==null||supplied is bool||supplied is string)
    throw new ArgumentException(key+" must be an integer in "+minimum+".."+maximum);
   double number=Convert.ToDouble(supplied);
   if(Double.IsNaN(number)||Double.IsInfinity(number)||number!=Math.Truncate(number)||number<minimum||number>maximum)
    throw new ArgumentException(key+" must be an integer in "+minimum+".."+maximum);
   return Convert.ToUInt32(number);
  }
  /// <summary>
  /// 将当前页或未提交草稿中的直接地址绑定到已启用的 Modbus RTU 主站端口。
  /// 只修改原生模型，不访问串口或设备；设备必须属于当前工程，且原生驱动确实提供 4X 字寄存器。
  /// 新式地址沿用原生地址构造器：以驱动的组合 ID 和字宽构造完整 RegisterIdentity；旧式地址使用组合 ID 65540。
  /// </summary>
  /// <param name="a">ref、device_id、station(1..247)、address(1..65536)，位指示地址另外提供 bit(0..15)。</param>
  /// <param name="view">当前原生设计视图，用于验证归属和沿用页面撤销事务。</param>
  /// <returns>实际回读的设备、站号、4X 地址、位及数据类型；保存由调用方统一处理。</returns>
  static object ConfigureModbusRtuAddress(Dictionary<string,object>a,object view){
   var address=Ref(a);string type=address.GetType().FullName;
   bool legacy=type=="Flexem.Studio.Models.HMI.AddressInfo";
   if(!legacy&&type!="Flexem.Studio.Hmi.Models.Address.AddressData")throw new ArgumentException("Expected an AddressInfo or AddressData handle");
   var scope=OwningScope(S(a,"ref"));var draft=scope as DrawingDraft;
   if(scope==null||(draft==null&&!Object.ReferenceEquals(scope,view))||(draft!=null&&!Object.ReferenceEquals(draft.Page,Member(view,"Model"))))
    throw new ArgumentException("Address must belong to the active page or its draft");
   Guid deviceId;
   if(!a.ContainsKey("device_id")||!Guid.TryParse(S(a,"device_id"),out deviceId)||deviceId==Guid.Empty)
    throw new ArgumentException("device_id must be a non-empty current-project RTU device UUID");
   uint station=ModbusRtuInteger(a,"station",1,247),main=ModbusRtuInteger(a,"address",1,65536);
   bool asBit=a.ContainsKey("bit");uint bit=asBit?ModbusRtuInteger(a,"bit",0,15):0;
   var raw=legacy?address:Member(address,"Raw");if(raw==null)throw new ArgumentException("Address Raw is not initialized");
   // 保留控件原来的位/字用途，不把数值输入转换成指示器，也不把位指示转换成数值地址。
   string kind=Convert.ToString(Member(address,"AddressType"));
   if(kind!=(asBit?"Bit":"Word"))throw new ArgumentException(asBit?"bit requires an existing Bit address":"A Bit address requires an explicit holding-register bit");
   // 高级引用会改变实际地址含义；此专用入口拒绝它们，不能静默清除用户配置。
   foreach(var target in new[]{address,raw}.Distinct())foreach(string flag in new[]{"IsLabelAddress","UseIndex","UseStationAddress","UseStationReference","UseTagByteReference","UseOffset","UseAddressReference","IsCombinedBits","IsVariableTagDevice","IsFormula"}){
    var property=target.GetType().GetProperty(flag);
    if(property!=null&&property.PropertyType==typeof(bool)&&(bool)property.GetValue(target,null))
     throw new ArgumentException("RTU configuration requires a direct address; advanced option is active: "+flag);
   }
   // 依据当前工程现有物理端口查找身份，不创建端口，也不接受其他工程或已禁用的设备 UUID。
   var ports=(IEnumerable)Member(Member(Member(CurrentProject(),"HMIInfo"),"LocalConnectionInfo"),"Ports");
   var matches=ports.Cast<object>().Where(p=>(Guid)Member(p,"DeviceId")==deviceId).ToArray();
   if(matches.Length!=1)throw new ArgumentException("RTU device identity must match exactly one current-project serial port");
   var port=matches[0];
   if(!(bool)Member(port,"IsUsed")||Convert.ToInt32(Member(port,"PLCID"))!=4||Convert.ToString(Member(port,"PLCName"))!="Modbus RTU"||Convert.ToString(Member(port,"Mode"))!="Master")
    throw new ArgumentException("The serial port must be enabled with Modbus RTU PLCID 4 in Master mode");
   if((bool)Member(port,"UseAddressStation"))throw new ArgumentException("The RTU port must use fixed station numbers instead of an address-controlled station");
   // 读取已安装驱动的公开配置，证实 4X 为 Word，防止错误使用线圈或其他驱动的寄存器 ID。
   var configuration=CallNative(Member(PageServices(),"DeviceConfigurationService"),"GetSerialPlcConfiguration",4);
   if(configuration==null||Convert.ToInt32(Member(configuration,"ID"))!=4||Convert.ToString(Member(configuration,"Name"))!="Modbus RTU")
    throw new InvalidOperationException("The current project has no matching native Modbus RTU configuration");
   var width=Enum.Parse(Native("Flexem.Studio.Hmi.Device.Configuration.AddressWidthType"),"Word");
   var register=CallNative(configuration,"GetRegister",(uint)4,width);
   const uint combinedRegisterId=65540;
   if(register==null||Convert.ToUInt32(Member(register,"Id"))!=combinedRegisterId||Convert.ToString(Member(register,"Name"))!="4X"||Convert.ToString(Member(register,"AddressWidth"))!="Word")
    throw new InvalidOperationException("Native Modbus RTU 4X Word register verification failed");
   // AddressRawData 的原生构造器将 CombineRegisterId 的结果直接传入 RegisterIdentity(id,width)。
   // FromCombinedRegisterId 是另一种解码工厂，会将 65540 拆成 Id=4；该身份不能通过实际数值控件编译。
   // 所以此处保留官方 IRegister.Id=65540，与原生地址选择器写入和已有效的本地 65791 身份一致。
   object registerValue=legacy?(object)combinedRegisterId:Activator.CreateInstance(Native("Flexem.Studio.Hmi.Device.Configuration.RegisterIdentity"),new object[]{Convert.ToUInt32(Member(register,"Id")),width});
   if(!legacy&&(Convert.ToUInt32(Member(registerValue,"Id"))!=combinedRegisterId||Convert.ToString(Member(registerValue,"AddressWidthType"))!="Word"))
    throw new InvalidOperationException("Native 4X RegisterIdentity constructor returned an unexpected identity");
   object dataType=Member(raw,"DataType");
   string addressRef=S(a,"ref"),prefix=legacy?"":"Raw.";
   // 全部字段先由统一批量入口预检再写入。附着页面使用一个原生撤销组；草稿失败恢复原值。
   // Raw.DataType 最后回写原值，保留现有原始地址类型；AddressData 顶层的独立 DataType 完全不修改。
   // 新式使用地址根的 Raw.* 路径，让批量入口能发现前序 setter 是否替换了 Raw 子对象。
   var edits=new[]{
    Obj("ref",addressRef,"member",prefix+"DeviceId","value",deviceId),
    Obj("ref",addressRef,"member",prefix+(legacy?"StationNO":"StationNo"),"value",station),
    Obj("ref",addressRef,"member",prefix+"RegisterId","value",registerValue),
    Obj("ref",addressRef,"member",prefix+"MainAddress","value",main),
    Obj("ref",addressRef,"member",prefix+"SubAddress","value",bit),
    Obj("ref",addressRef,"member",prefix+(legacy?"UseByteReference":"UseWordRegisterAsBit"),"value",asBit),
    Obj("ref",addressRef,"member",prefix+"DataType","value",dataType)
   };
   var changed=(Dictionary<string,object>)SetModelProperties(Obj("changes",edits));
   // 旧模型持久化显示缩写与规则结果；与既有本地地址入口保持相同的原生刷新流程。
   if(legacy){CallNative(address,"UpdateAbbreviation");CallNative(address,"UpdateDeviceAbbreviation");CallNative(address,"CheckRules");}
   var actualRegister=Member(raw,"RegisterId");
   return Obj("model",DescribeOwned(address,scope),"address_model",legacy?"AddressInfo":"AddressData","address_type",kind,
    "device_id",Convert.ToString(Member(raw,"DeviceId")),"port",Convert.ToString(Member(port,"PortName")),
    "station",Member(raw,legacy?"StationNO":"StationNo"),"register_id",combinedRegisterId,
    "native_register_id",legacy?actualRegister:Member(actualRegister,"Id"),"register_width","Word","address",Member(raw,"MainAddress"),
    "bit",asBit?Member(raw,"SubAddress"):null,"word_as_bit",Member(raw,legacy?"UseByteReference":"UseWordRegisterAsBit"),
    "data_type",Convert.ToString(Member(raw,"DataType")),"native_undo_transaction",changed["native_undo_transaction"],
    "persistence","save_project_to_persist","hardware_io",false);
  }
  /// <summary>
  /// 以原生 DTO 构造完整 Numeric Zoom 子模型，配置当前页或草稿数值控件的倍率和偏移。
  /// 两个数值运算开关独立，需一并启用；DecimalValue 保持原值，不推断它的运行时小数语义。
  /// 本接口仅改模型，是否与显示小数位叠加必须在离线验证工程中以已知原始值验证。
  /// </summary>
  static object ConfigureNumericZoom(Dictionary<string,object>a,object view){
   var model=Ref(a);
   if(model.GetType().FullName!="Flexem.Studio.Components.Parts.NumericCharPart.NumericCharInfo")throw new ArgumentException("Expected a native NumericCharInfo");
   var scope=OwningScope(S(a,"ref"));var draft=scope as DrawingDraft;
   if(scope==null||(draft==null&&!Object.ReferenceEquals(scope,view))||(draft!=null&&!Object.ReferenceEquals(draft.Page,Member(view,"Model"))))
    throw new ArgumentException("Numeric control must belong to the active page or its draft");
   var normal=Member(model,"NormalData");var numericOperator=Member(normal,"NumericOperator");
   if(numericOperator==null)throw new InvalidOperationException("The numeric operator parent is not initialized");
   var values=new Dictionary<string,double>();
   foreach(string key in new[]{"gain","offset"}){
    object supplied;if(!a.TryGetValue(key,out supplied)||supplied==null||supplied is bool||supplied is string)throw new ArgumentException(key+" must be a finite number");
    double value=Convert.ToDouble(supplied);if(Double.IsNaN(value)||Double.IsInfinity(value))throw new ArgumentException(key+" must be a finite number");
    values[key]=value;
   }
   object scaling;if(!a.TryGetValue("is_decimal_scaling",out scaling)||!(scaling is bool))throw new ArgumentException("is_decimal_scaling must be an explicit boolean");
   const string prefix="Flexem.Studio.Components.Parts.NumericCharNewPart.";
   var dto=Activator.CreateInstance(Native(prefix+"NumericOperatorSettingDto"));
   var zoom=Activator.CreateInstance(Native(prefix+"ZoomDto"));
   foreach(string field in new[]{"GainValue","OffsetValue"}){
    var variable=Activator.CreateInstance(Native("Flexem.Studio.Components.Common.VariableDto"));
    NativeSet(variable,"VariableType","Constant");NativeSet(variable,"IsConstant",true);NativeSet(variable,"Constant",values[field=="GainValue"?"gain":"offset"]);
    NativeSet(zoom,field,variable);
   }
   NativeSet(zoom,"IsDecimalScaling",scaling);NativeSet(dto,"ComputationType","Zoom");NativeSet(dto,"Zoom",zoom);
   var setting=Activator.CreateInstance(Native("Flexem.Studio.Hmi.Models.Components.Parts.NumericCharPart.NumericSet.NumericOperatorSettingData"),new[]{dto});
   // 完整子模型一次赋值，无需临时切换运算类型；不会留下 GainValue 或 OffsetValue 为空的半成品。
   string id=S(a,"ref");
   var changes=new[]{Obj("ref",id,"member","NormalData.NumericOperator.NumericOperator","value",setting),
    Obj("ref",id,"member","NormalData.NumericOperator.UseNumericalOperator","value",true),
    Obj("ref",id,"member","NormalData.UseNumericalOperator","value",true)};
   var changed=(Dictionary<string,object>)SetModelProperties(Obj("changes",changes));
   return Obj("model",DescribeOwned(model,scope),"computation","Zoom","gain",values["gain"],"offset",values["offset"],
    "is_decimal_scaling",scaling,"decimal_digits",Member(normal,"DecimalValue"),"native_undo_transaction",changed["native_undo_transaction"],
    "hardware_io",false,"persistence","save_project_to_persist","runtime_scaling_verified",false);
  }
  /// <summary>
  /// 以完整原生 DTO 修复数值上下限子对象。原生构造及 SaveTo 在 UseLimit=false 时省略 Min/Max，
  /// 因此本初始化器保留 UseLimit=true，关闭溢出取值和闪烁，避免空子模型再次导致窗口编译异常。
  /// 用户先选择 DataType 再调用本方法；本方法不改读地址、数据类型或数值倍率。
  /// </summary>
  static object ConfigureNumericLimits(Dictionary<string,object>a,object view){
   var model=Ref(a);
   if(model.GetType().FullName!="Flexem.Studio.Components.Parts.NumericCharPart.NumericCharInfo")throw new ArgumentException("Expected a native NumericCharInfo");
   var scope=OwningScope(S(a,"ref"));var draft=scope as DrawingDraft;
   if(scope==null||(draft==null&&!Object.ReferenceEquals(scope,view))||(draft!=null&&!Object.ReferenceEquals(draft.Page,Member(view,"Model"))))
    throw new ArgumentException("Numeric control must belong to the active page or its draft");
   object used;
   if(!a.TryGetValue("use_limit",out used)||!(used is bool)||!(bool)used)
    throw new ArgumentException("use_limit must be true: native UseLimit=false omits MinLimit/MaxLimit during construction and persistence");
   var bounds=new Dictionary<string,double>();
   foreach(string key in new[]{"min","max"}){
    object supplied;if(!a.TryGetValue(key,out supplied)||supplied==null||supplied is bool||supplied is string)throw new ArgumentException(key+" must be a finite number");
    double number=Convert.ToDouble(supplied);if(Double.IsNaN(number)||Double.IsInfinity(number))throw new ArgumentException(key+" must be a finite number");
    bounds[key]=number;
   }
   if(bounds["min"]>bounds["max"])throw new ArgumentException("min must not exceed max");
   var parents=new List<Tuple<string,object>>();
   foreach(string name in new[]{"NormalData","NumericSetingData"}){
    var property=model.GetType().GetProperty(name);
    if(property==null||property.GetGetMethod()==null)continue;
    var parent=Member(model,name);
    // 某些版本旧名与新名是同一模型别名，必须去重，不能对同一原生属性重复批量写入。
    if(parent!=null&&parent.GetType().GetProperty("NumericLimit")!=null&&!parents.Any(p=>Object.ReferenceEquals(p.Item2,parent)))parents.Add(Tuple.Create(name,parent));
   }
   if(parents.Count==0)throw new InvalidOperationException("The numeric model has no supported limit parent");
   var changes=new List<object>();var paths=new List<string>();
   foreach(var parent in parents){
    var dto=Activator.CreateInstance(Native("Flexem.Studio.Components.Common.NumericLimit.NumericLimitDto"));
    NativeSet(dto,"UseLimit",true);
    foreach(string flag in new[]{"OverflowMinValue","OverflowMaxValue","OverflowMinBlink","OverflowMaxBlink"})NativeSet(dto,flag,false);
    foreach(string field in new[]{"MinLimit","MaxLimit"}){
     var variable=Activator.CreateInstance(Native("Flexem.Studio.Components.Common.VariableDto"));
     NativeSet(variable,"VariableType","Constant");NativeSet(variable,"IsConstant",true);
     // 保留调用方数值对象的类型；不构造多余的变量地址，也不留下没有 Raw 的占位地址。
     NativeSet(variable,"Constant",a[field=="MinLimit"?"min":"max"]);NativeSet(variable,"Variable",null);
     NativeSet(dto,field,variable);
    }
    var limit=Activator.CreateInstance(Native("Flexem.Studio.Hmi.Models.Components.Common.NumericLimit.NumericLimitData"),new[]{dto});
    if(Member(limit,"MinLimit")==null||Member(limit,"MaxLimit")==null)throw new InvalidOperationException("Native numeric limit construction left empty bounds");
    string path=parent.Item1+".NumericLimit";paths.Add(path);changes.Add(Obj("ref",S(a,"ref"),"member",path,"value",limit));
   }
   var changed=(Dictionary<string,object>)SetModelProperties(Obj("changes",changes.ToArray()));
   return Obj("model",DescribeOwned(model,scope),"min",a["min"],"max",a["max"],"use_limit",true,
    "overflow_value",false,"overflow_blink",false,"initialized_paths",paths.ToArray(),"native_undo_transaction",changed["native_undo_transaction"],
    "hardware_io",false,"persistence","save_project_to_persist");
  }
  static object ConfigureBitSwitch(Dictionary<string,object>a,object view) {
   var model=Ref(a);
   if(model.GetType().FullName!="Flexem.Studio.Components.Parts.SwitchPart.SwitchInfo")throw new ArgumentException("Expected a native SwitchInfo model");
   var scope=OwningScope(S(a,"ref"));var draft=scope as DrawingDraft;
   if(scope==null||(draft==null&&!Object.ReferenceEquals(scope,view))||(draft!=null&&!Object.ReferenceEquals(draft.Page,Member(view,"Model"))))throw new ArgumentException("Switch must belong to the current page or one of its drafts");
   string mode=S(a,"action");if(!new[]{"On","Off","Switch"}.Contains(mode))throw new ArgumentException("action must be On, Off or Switch");
   object rawValue=a["address"];if(rawValue==null||rawValue is bool||rawValue is string)throw new ArgumentException("address must be an unsigned integer");
   double number=Convert.ToDouble(rawValue);if(Double.IsNaN(number)||Double.IsInfinity(number)||number<0||number>UInt32.MaxValue||Math.Truncate(number)!=number)throw new ArgumentException("address must be an unsigned integer");
   var settings=Member(model,"SwitchSettings");var actions=Member(settings,"ActionList");
   if(actions==null)throw new InvalidOperationException("Switch action list is not initialized");
   if(((IEnumerable)actions).Cast<object>().Any())throw new InvalidOperationException("This initializer requires an empty action list; existing switch actions were preserved");
   var devices=Member(PageServices(),"DeviceService");
   var address=PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateBit",devices);
   var raw=Member(address,"Raw");NativeSet(raw,"MainAddress",Convert.ToUInt32(number));
   var action=Activator.CreateInstance(Native("Flexem.Studio.Hmi.Models.Components.Parts.SwitchNew.BitSwitchActions.BitSwitchActionData"));
   NativeSet(action,"ExecutionCondition","Action");NativeSet(action,"Action","KeyDown");NativeSet(action,"Function","Bit");NativeSet(action,"FuncType",mode);NativeSet(action,"PulseWidth",2);NativeSet(action,"Address",address);
   var owner=OwningView(S(a,"ref"));object group=owner==null?null:BeginModelChange(owner,"MCP: bit switch action");
   try{NativeSet(settings,"UseSwitch",true);CallNative(actions,"Add",action);if(group!=null)CallNative(group,"Commit");}
   catch{if(group!=null)CallNative(group,"Abort");throw;}
   return Obj("model",DescribeOwned(model,scope),"action",DescribeOwned(action,scope),"function",mode,"trigger","KeyDown","local_device_id",Member(raw,"DeviceId"),"register",DescribeOwned(Member(raw,"RegisterId"),scope),"address",Member(raw,"MainAddress"),"action_count",((IEnumerable)actions).Cast<object>().Count(),"native_undo_transaction",group!=null,"persistence","save_project_to_persist","execution_mode",ExecutionMode);
  }
  static object ConfigureTrendSampling(Dictionary<string,object>a,object view){
   var model=Ref(a);if(model.GetType().GetProperty("SamplingChannel")==null)throw new ArgumentException("Expected a sampling-backed curve model");
   var scope=OwningScope(S(a,"ref"));var draft=scope as DrawingDraft;
   if(scope==null||(draft==null&&!Object.ReferenceEquals(scope,view))||(draft!=null&&!Object.ReferenceEquals(draft.Page,Member(view,"Model"))))throw new ArgumentException("Trend must belong to the active page or draft");
   var trendProperty=model.GetType().GetProperty("TrendChartNormalInfo");
   var diskProperty=model.GetType().GetProperty("NormalInfo");
   var normalObject=trendProperty==null?(diskProperty==null?null:diskProperty.GetValue(model,null)):trendProperty.GetValue(model,null);
   if(normalObject==null||normalObject.GetType().GetProperty("PauseAddress")==null)throw new ArgumentException("Expected a trend or disk curve model");
   var pause=Member(normalObject,"PauseAddress");
   if(pause==null||(Guid)Member(pause,"DeviceId")==Guid.Empty||Convert.ToUInt32(Member(pause,"RegisterId"))==0)throw new ArgumentException("Configure the curve PauseAddress before binding sampling; use fstudio_address_configure_local");
   Guid id;if(!Guid.TryParse(S(a,"sample_id"),out id))throw new ArgumentException("Invalid sample_id");
   var collection=Member(Member(Member(CurrentProject(),"HMIConfigInfo"),"DataSampleConfigInfo"),"DataSampleInfos");
   var sample=((IEnumerable)collection).Cast<object>().SingleOrDefault(x=>(Guid)Member(x,"Id")==id);if(sample==null)throw new ArgumentException("Sampling group does not exist");
   if(!(bool)Member(Member(sample,"PropertyInfo"),"UseSerialAddress"))throw new ArgumentException("This binding requires serial sampling channels");
   var channels=((IEnumerable)Member(Member(sample,"ChannelConfigInfo"),"ChannelInfos")).Cast<object>().ToArray();if(channels.Length==0)throw new ArgumentException("Sampling group has no channels");
   var sampling=Member(model,"SamplingChannel");var old=Member(sampling,"Configures");if(old!=null&&((IEnumerable)old).Cast<object>().Any())throw new ArgumentException("Trend already has channel configuration; refusing to overwrite");
   var configs=Activator.CreateInstance(Native("Flexem.Studio.Hmi.Models.Components.Parts.SamplingChannel.SamplingChannelConfigureDataList"));
   foreach(var channel in channels){
    var setting=Activator.CreateInstance(Native("Flexem.Studio.Components.Parts.SamplingChannel.SamplingChannelSettingDto"));
    foreach(string bound in new[]{"MinValue","MaxValue"}){var variable=Activator.CreateInstance(Native("Flexem.Studio.Components.Common.VariableDto"));NativeSet(variable,"VariableType",0);NativeSet(variable,"Constant",bound=="MinValue"?0:100);NativeSet(setting,bound,variable);}
    var dto=Activator.CreateInstance(Native("Flexem.Studio.Components.Parts.SamplingChannel.SamplingChannelConfigureDto"));NativeSet(dto,"Id",Member(channel,"ChannelId"));NativeSet(dto,"IsUse",true);NativeSet(dto,"ChannelSetting",setting);
    var data=Activator.CreateInstance(Native("Flexem.Studio.Hmi.Models.Components.Parts.SamplingChannel.SamplingChannelConfigureData"),new[]{dto});CallNative(configs,"Add",data);
   }
   var handle=(Dictionary<string,object>)DescribeOwned(sampling,scope);var configHandle=DescribeOwned(configs,scope);var normal=(Dictionary<string,object>)DescribeOwned(normalObject,scope);
   var changes=new List<Dictionary<string,object>>{Obj("ref",S(handle,"$ref"),"member","SampleId","value",id.ToString()),Obj("ref",S(handle,"$ref"),"member","Configures","value",configHandle)};
   if(normalObject.GetType().GetProperty("DisplayType")!=null)changes.Add(Obj("ref",S(normal,"$ref"),"member","DisplayType","value","Immediate"));
   var scaleProperty=model.GetType().GetProperty("TrendChartScaleInfo");
   var scaleObject=scaleProperty==null?null:scaleProperty.GetValue(model,null);
   if(scaleObject!=null&&scaleObject.GetType().GetProperty("ChannelValue")!=null){var scale=(Dictionary<string,object>)DescribeOwned(scaleObject,scope);changes.Add(Obj("ref",S(scale,"$ref"),"member","ChannelValue","value",Member(channels[0],"ChannelId")));}
   var result=SetModelProperties(Obj("changes",changes.ToArray()));return Obj("curve_type",model.GetType().FullName,"sample_id",id.ToString(),"channels",channels.Length,"properties",result,"runtime","not_executed");
  }
  static object RemoveCanvasGraphic(Dictionary<string,object>a,object view,object page){
   var item=Ref(a);
   var collection=Member(page,"Graphicses");
   var before=((IEnumerable)collection).Cast<object>().ToArray();
   int index=Array.FindIndex(before,x=>Object.ReferenceEquals(x,item));
   if(index<0)throw new ArgumentException("Graphic handle is not an element of the active page canvas");
   var group=BeginModelChange(view,"MCP: remove canvas graphic");
   try{
    CallNative(collection,"Remove",item);
    var after=((IEnumerable)collection).Cast<object>().ToArray();
    var expected=before.Where(x=>!Object.ReferenceEquals(x,item)).ToArray();
    if(after.Length!=expected.Length||after.Where((x,i)=>!Object.ReferenceEquals(x,expected[i])).Any())throw new InvalidOperationException("Native removal changed unexpected collection entries");
    CallNative(group,"Commit");
   }catch(Exception error){try{CallNative(group,"Abort");}catch(Exception rollback){throw new AggregateException("Graphic removal and rollback failed",error,rollback);}throw;}
   return Obj("removed",true,"index",index,"remaining_count",((IEnumerable)collection).Cast<object>().Count(),
    "graphic_type",item.GetType().FullName,"comment",Member(item,"Comment"),"page_uuid",PageUuid(page).ToString(),
    "dirty",Member(view,"IsDirty"),"native_undo_transaction",true,"persistence","save_project_to_persist","runtime","not_executed");
  }
  /// <summary>在当前原生页面管理绘图草稿和画布修改；插入使用设计器事务，失败时回滚。</summary>
  /// <param name="a">已校验的操作名称、模型句柄、工具参数或插入边界。</param>
  /// <returns>草稿句柄或实际操作结果；修改后的画布需另行保存项目。</returns>
  /// <exception cref="InvalidOperationException">页面、草稿或原生模板状态不满足操作条件。</exception>
  static object DrawingOperation(Dictionary<string,object>a){
   var op=S(a,"op");var view=DesignerView();var page=Member(view,"Model");var context=Member(view,"DesignContext");
   if(op=="drawing_snapshot")return DrawingSnapshot(a,view,page);
   if(op=="drawing_create_many")return DrawingCreateMany(a,view,page,context);
   if(op=="drawing_show_canvas")return ShowCanvasWithoutActivation();
   if(op=="drawing_state")return Obj("project",ProjectFile(),"page",DescribeOwned(page,view),"graphics",DescribeOwned(Member(page,"Graphicses"),view),"view",Describe(view),"dirty",Member(view,"IsDirty"),"draft_count",drawingDrafts.Count,"drafts",drawingDrafts.Select(d=>Obj("draft_id",d.Key,"model",Describe(d.Value.Model))).ToArray(),"execution_mode","background_with_live_canvas");
   if(op=="drawing_configure_bit_switch")return ConfigureBitSwitch(a,view);
   if(op=="drawing_configure_local_address")return ConfigureLocalAddress(a,view);
   if(op=="drawing_configure_modbus_rtu_address")return ConfigureModbusRtuAddress(a,view);
   if(op=="drawing_configure_numeric_zoom")return ConfigureNumericZoom(a,view);
   if(op=="drawing_configure_numeric_limits")return ConfigureNumericLimits(a,view);
   if(op=="drawing_configure_trend")return ConfigureTrendSampling(a,view);
   if(op=="drawing_remove_graphic")return RemoveCanvasGraphic(a,view,page);
   if(op=="drawing_prepare"){
    var createdDraft=CreateDrawingDraft(a,page,context);
    var id=Guid.NewGuid().ToString("N");drawingDrafts[id]=createdDraft;
    return Obj("draft_id",id,"model",DescribeOwned(createdDraft.Model,createdDraft),"view_model",DescribeOwned(createdDraft.ViewModel,createdDraft),"tool",Describe(createdDraft.Tool),"canvas_changed",false);
   }
   DrawingDraft draft;string draftId=S(a,"draft_id");if(!drawingDrafts.TryGetValue(draftId,out draft))throw new ArgumentException("Unknown or expired drawing draft");
   if(!Object.ReferenceEquals(draft.Page,page))throw new InvalidOperationException("Draft belongs to a different page; reopen its original page");
   // 插入失败的原生回滚可能已释放引用；仅释放仍有效的引用，确保失败草稿也能清理。
   if(op=="drawing_discard"){if((bool)Member(draft.Model,"IsReferenced"))CallNative(draft.Model,"DisableReference");drawingDrafts.Remove(draftId);ClearScopeHandles(draft);return Obj("discarded",true,"canvas_changed",false);}
   if(op!="drawing_commit")throw new ArgumentException("Unknown drawing operation");
   // 草稿可能早于其他创建请求，同类型编号在正式登记前重新核对，避免旧草稿与新批次编号相撞。
   AllocateBatchComponentIds(new List<BatchWidget>{new BatchWidget{Draft=draft}},context);
   var position=Member(draft.Model,"Position");
   foreach(var p in new[]{"X","Y","Width","Height"}){double value=Convert.ToDouble(a[p.ToLowerInvariant()]);if(Double.IsNaN(value)||Double.IsInfinity(value)||value<0||(p=="Width"||p=="Height")&&value==0)throw new ArgumentException("Invalid widget bounds");NativeSet(position,p,value);}
   // Modern components derive their display name from type/id. Comment is their
   // public, persisted user label; never write a protected name field directly.
   NativeSet(draft.Model,"Comment",S(a,"name"));
   var setName=draft.Model.GetType().GetMethod("SetDisplayName",new Type[]{typeof(string)});
   if(setName!=null)setName.Invoke(draft.Model,new object[]{S(a,"name")});
   var extensionManager=Member(Member(context,"Services"),"ExtensionManager");
   var viewType=(Type)Member(draft.Model,"ViewType");var component=CallNative(extensionManager,"CreateInstanceWithCustomInstanceFactory",viewType,null);
   var item=CallNative(DesignerService(context,"ICSharpCode.WpfDesign.IComponentService"),"RegisterComponentForDesigner",component);
   var group=CallNative(item,"OpenGroup","MCP: "+S(a,"name"));object placement=null;Dictionary<string,object> committedResult=null;
   try{
    CallNative(extensionManager,"ApplyDefaultInitializers",item);
    // Sliding 的绑定器立即访问模板子控件；原生鼠标绘制会先应用模板，后台创建也必须完成此步骤。
    if(viewType.FullName=="Flexem.Studio.GraphicsDesigner.Presentation.Shapes.Sliding"){
     var element=(FrameworkElement)component;
     // 尚未插入视觉树的控件无法继承页面资源，先从当前原生画布解析同类型样式。
     var canvas=Member(Member(context,"RootItem"),"View") as FrameworkElement;
     if(element.Style==null&&canvas!=null)element.Style=canvas.TryFindResource(viewType) as Style;
     element.ApplyTemplate();
     if(new[]{"BackgroundGalleryViewer","SliderGalleryViewer","Scale"}.Any(name=>Member(component,name)==null))
      throw new InvalidOperationException("Native Sliding view template did not initialize its required parts");
    }
    var registry=DesignerService(context,"Flexem.Studio.GraphicsDesign.IRootGraphicRegistry");
    CallNative(registry,"RegisterRootGraphic",draft.Model,item);
    var root=Member(context,"RootItem");var items=Array.CreateInstance(Native("ICSharpCode.WpfDesign.DesignItem"),1);items.SetValue(item,0);
    var rect=new Rect(Convert.ToDouble(a["x"]),Convert.ToDouble(a["y"]),Convert.ToDouble(a["width"]),Convert.ToDouble(a["height"]));
    var addItem=Native("ICSharpCode.WpfDesign.PlacementType").GetField("AddItem").GetValue(null);
    placement=Native("ICSharpCode.WpfDesign.PlacementOperation").GetMethod("TryStartInsertNewComponents").Invoke(null,new object[]{root,items,new Rect[]{rect},addItem});
    if(placement==null)throw new InvalidOperationException("Native designer rejected the insertion bounds");
    CallNative(placement,"Commit");
    // 在仍可回滚时完成句柄分配与原生回读，避免创建成功后因句柄上限或 getter 报错而返回失败。
    committedResult=Obj("draft_id",draftId,"model",DescribeOwned(draft.Model,draft),"design_item",DescribeOwned(item,draft),"name",S(a,"name"),"name_storage","native_Comment","display_name",Member(draft.Model,"DisplayName"),"canvas_changed",true,"dirty",null,"project",ProjectFile());
    CallNative(group,"Commit");
   }catch(Exception error){
    // placement.Commit 之后组提交仍可能失败；已完成的 placement 不能再次 Abort，否则会掩盖首错并跳过外层回滚。
    var failures=new List<Exception>{error};
    if(placement!=null)try{if(!(bool)Member(placement,"IsCommitted")&&!(bool)Member(placement,"IsAborted"))CallNative(placement,"Abort");}catch(Exception rollback){failures.Add(rollback);}
    try{CallNative(group,"Abort");}catch(Exception rollback){failures.Add(rollback);}
    if(failures.Count>1)throw new AggregateException("Native insertion failed and rollback was incomplete",failures);
    throw;
   }
   // 不选择或定位控件；画布通过原生绑定更新，不打开属性窗口或抢占焦点。
   draft.CommittedView=view;
   drawingDrafts.Remove(draftId);
   foreach(var id in refViews.Where(x=>Object.ReferenceEquals(x.Value,draft)).Select(x=>x.Key).ToArray())refViews[id]=view;
   try{committedResult["dirty"]=Member(view,"IsDirty");}
   catch(Exception diagnostic){committedResult["warnings"]=new[]{"创建已提交，但未能读取页面修改状态："+diagnostic.Message};}
   return committedResult;
  }
 }
}
