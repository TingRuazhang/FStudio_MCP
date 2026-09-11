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
   if(minimized||!IsWindowVisible(handle))ShowWindow(handle,4);
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
   foreach(var target in new[]{address,raw}.Distinct())foreach(var flag in new[]{"IsLabelAddress","UseIndex","UseStationAddress","UseStationReference","UseTagByteReference","UseOffset","UseWordRegisterAsBit"}){
    var property=target.GetType().GetProperty(flag);
    if(property!=null&&property.PropertyType==typeof(bool)&&(bool)property.GetValue(target,null))throw new ArgumentException("Configure local address requires a direct address; advanced option is active: "+flag);
   }
   string kind=Convert.ToString(Member(address,"AddressType"));
   if(kind!="Bit"&&kind!="Word")throw new ArgumentException("Unsupported native address type: "+kind);
   var devices=Member(PageServices(),"DeviceService");
   var source=kind=="Bit"?PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateBit",devices):PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateWord",devices,Member(raw,"DataType"),true);
   var sourceRaw=Member(source,"Raw");var register=Member(sourceRaw,"RegisterId");
   var described=(Dictionary<string,object>)DescribeOwned(raw,scope);string rawRef=S(described,"$ref");
   var edits=new[]{Obj("ref",rawRef,"member","DeviceId","value",Member(sourceRaw,"DeviceId")),Obj("ref",rawRef,"member","RegisterId","value",legacy?Member(register,"Id"):register),Obj("ref",rawRef,"member","MainAddress","value",Convert.ToUInt32(number))};
   var changed=(Dictionary<string,object>)SetModelProperties(Obj("changes",edits));
   // Legacy AddressInfo keeps persisted display text and CSLA validity derived
   // from Raw. A prior AddressType/IsCombinedBits change can leave those values
   // stale even though DeviceId/RegisterId/MainAddress are already correct.
   if(legacy){CallNative(address,"UpdateAbbreviation");CallNative(address,"UpdateDeviceAbbreviation");CallNative(address,"CheckRules");}
   var actualRegister=Member(raw,"RegisterId");
   return Obj("model",DescribeOwned(address,scope),"address_type",kind,"device_id",Convert.ToString(Member(raw,"DeviceId")),"register_id",legacy?actualRegister:Member(actualRegister,"Id"),"address",Member(raw,"MainAddress"),"data_type",Convert.ToString(Member(raw,"DataType")),"native_undo_transaction",changed["native_undo_transaction"],"persistence","save_project_to_persist","hardware_io",false);
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
   if(op=="drawing_show_canvas")return ShowCanvasWithoutActivation();
   if(op=="drawing_state")return Obj("project",ProjectFile(),"page",DescribeOwned(page,view),"graphics",DescribeOwned(Member(page,"Graphicses"),view),"view",Describe(view),"dirty",Member(view,"IsDirty"),"draft_count",drawingDrafts.Count,"drafts",drawingDrafts.Select(d=>Obj("draft_id",d.Key,"model",Describe(d.Value.Model))).ToArray(),"execution_mode","background_with_live_canvas");
   if(op=="drawing_configure_bit_switch")return ConfigureBitSwitch(a,view);
   if(op=="drawing_configure_local_address")return ConfigureLocalAddress(a,view);
   if(op=="drawing_configure_trend")return ConfigureTrendSampling(a,view);
   if(op=="drawing_remove_graphic")return RemoveCanvasGraphic(a,view,page);
   if(op=="drawing_prepare"){
    var type=Native(S(a,"tool"));
    if(type.Namespace!="Flexem.Studio.GraphicsDesigner.Tools"||type.IsAbstract||type.ContainsGenericParameters||!type.Name.EndsWith("Tool"))throw new ArgumentException("Expected a concrete native drawing tool");
    // 此版本窗口选择器的 Child_Fetch 在载入 PagingAddress 前调用 OnFetching/Init，触发空引用。
    // 在创建草稿前拒绝，避免把无法再次打开的控件写入用户工程；未来版本不套用此限制。
    if(type.Name=="WindowSelectorTool"&&type.Assembly.GetName().Version.ToString()=="3.0.15685.0")
     throw new NotSupportedException("NATIVE_UNSUPPORTED: WindowSelectorTool in FStudio 3.0.15685.0 accesses PagingAddress before deserialization and prevents project reopening; draft creation was refused to preserve the project");
    var tool=Activator.CreateInstance(type);
    // Native tool hooks require the current design context; Activate opens property UI.
    for(var baseType=type;baseType!=null;baseType=baseType.BaseType)foreach(var field in baseType.GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly)){
     if(field.FieldType.FullName=="ICSharpCode.WpfDesign.DesignContext")field.SetValue(tool,context);
     if(field.FieldType.FullName=="ICSharpCode.WpfDesign.IDesignPanel")field.SetValue(tool,Member(Member(context,"Services"),"DesignPanel"));
    }
    if(a.ContainsKey("tool_properties"))foreach(var p in (Dictionary<string,object>)a["tool_properties"])NativeSet(tool,p.Key,p.Value);
    var create=ToolHook(tool,"CreateModel",0);if(create==null)throw new ArgumentException("Native tool has no model creation hook");
    var model=create.Invoke(tool,new object[0]);
    var project=Member(Resolve("Flexem.Studio.Hmi.Project.IHmiProjectService"),"CurrentProject");
    if(!(bool)Member(model,"IsReferenced"))CallNative(model,"EnableReference",project);
    object vm;
    try{
     InitializeDrawingModel(model);
     var createVm=ToolHook(tool,"CreateViewModel",1);
     // 基础几何模型自行初始化属性，无需调用属性面板工厂。
     bool basicShape=new[]{"LineTool","PolyLineTool","PolygonTool","RectangleTool","EllipseTool","ArcTool","SectorTool","SectorRingTool"}.Contains(tool.GetType().Name);
     vm=basicShape?null:createVm==null?Native("Flexem.Studio.GraphicsDesigner.PropertyViewModelFactory").GetMethod("GetViewModel",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{model}):createVm.Invoke(tool,new object[]{model});
    }catch{
     // 草稿尚未登记，客户端无法显式丢弃；此处释放失败初始化建立的项目引用。
     if((bool)Member(model,"IsReferenced"))CallNative(model,"DisableReference");
     throw;
    }
    var id=Guid.NewGuid().ToString("N");var createdDraft=new DrawingDraft{Model=model,ViewModel=vm,Tool=tool,Page=page};drawingDrafts[id]=createdDraft;
    return Obj("draft_id",id,"model",DescribeOwned(model,createdDraft),"view_model",DescribeOwned(vm,createdDraft),"tool",Describe(tool),"canvas_changed",false);
   }
   DrawingDraft draft;string draftId=S(a,"draft_id");if(!drawingDrafts.TryGetValue(draftId,out draft))throw new ArgumentException("Unknown or expired drawing draft");
   if(!Object.ReferenceEquals(draft.Page,page))throw new InvalidOperationException("Draft belongs to a different page; reopen its original page");
   // 插入失败的原生回滚可能已释放引用；仅释放仍有效的引用，确保失败草稿也能清理。
   if(op=="drawing_discard"){if((bool)Member(draft.Model,"IsReferenced"))CallNative(draft.Model,"DisableReference");drawingDrafts.Remove(draftId);return Obj("discarded",true,"canvas_changed",false);}
   if(op!="drawing_commit")throw new ArgumentException("Unknown drawing operation");
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
   var group=CallNative(item,"OpenGroup","MCP: "+S(a,"name"));object placement=null;
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
    CallNative(placement,"Commit");CallNative(group,"Commit");
   }catch{if(placement!=null)CallNative(placement,"Abort");CallNative(group,"Abort");throw;}
   // Do not select/locate the item: SelectGraphics(showSetting:true, ...) opens native UI.
   // The designer updates through native bindings without focus, selection, or drag input.
   draft.CommittedView=view;
   drawingDrafts.Remove(draftId);
   return Obj("draft_id",draftId,"model",DescribeOwned(draft.Model,view),"design_item",DescribeOwned(item,view),"name",S(a,"name"),"name_storage","native_Comment","display_name",Member(draft.Model,"DisplayName"),"canvas_changed",true,"dirty",Member(view,"IsDirty"),"project",ProjectFile());
  }
 }
}
