using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace FStudioMcp {
 /// <summary>为原生宿主补充批量控件创建，复用草稿初始化、属性校验和设计器事务。</summary>
 internal static partial class VisibleHost {
  /// <summary>记录本批拥有的草稿、声明和请求矩形，失败时仅清理该批资源。</summary>
  sealed class BatchDrawingEntry {internal string Id;internal DrawingDraft Draft;internal Dictionary<string,object> Spec;internal Rect Requested;}

  /// <summary>检查声明的有限边界；与单控件接口相同，不把非法坐标静默修正。</summary>
  static Rect BatchDrawingBounds(Dictionary<string,object> spec) {
   var values=new List<double>();
   foreach(var key in new[]{"x","y","width","height"}){
    object raw=spec[key];
    if(raw==null||raw is bool||raw is string)throw new ArgumentException("Invalid widget bounds: "+key);
    double value=Convert.ToDouble(raw);
    if(Double.IsNaN(value)||Double.IsInfinity(value)||value<0||value>16384||((key=="width"||key=="height")&&value==0))
     throw new ArgumentException("Invalid widget bounds: "+key);
    values.Add(value);
   }
   return new Rect(values[0],values[1],values[2],values[3]);
  }

  /// <summary>以原生布局模型返回实际矩形，便于发现字体或最小尺寸造成的扩宽。</summary>
  static object DrawingBoundsResult(object model) {
   var position=Member(model,"Position");
   return Obj("x",Member(position,"X"),"y",Member(position,"Y"),"width",Member(position,"Width"),"height",Member(position,"Height"));
  }

  /// <summary>将配置完的草稿注册成设计项；调用方必须已经开启原生撤销组。</summary>
  static object RegisterBatchDrawingItem(object context,DrawingDraft draft) {
   var extensions=Member(Member(context,"Services"),"ExtensionManager");
   var viewType=(Type)Member(draft.Model,"ViewType");
   var component=CallNative(extensions,"CreateInstanceWithCustomInstanceFactory",viewType,null);
   var item=CallNative(DesignerService(context,"ICSharpCode.WpfDesign.IComponentService"),"RegisterComponentForDesigner",component);
   CallNative(extensions,"ApplyDefaultInitializers",item);
   // 与单项插入保持一致：滑块绑定器依赖先应用样式模板，不能直接插入空视觉对象。
   if(viewType.FullName=="Flexem.Studio.GraphicsDesigner.Presentation.Shapes.Sliding"){
    var element=(FrameworkElement)component;
    var canvas=Member(Member(context,"RootItem"),"View") as FrameworkElement;
    if(element.Style==null&&canvas!=null)element.Style=canvas.TryFindResource(viewType) as Style;
    element.ApplyTemplate();
    if(new[]{"BackgroundGalleryViewer","SliderGalleryViewer","Scale"}.Any(name=>Member(component,name)==null))
     throw new InvalidOperationException("Native Sliding view template did not initialize its required parts");
   }
   CallNative(DesignerService(context,"Flexem.Studio.GraphicsDesign.IRootGraphicRegistry"),"RegisterRootGraphic",draft.Model,item);
   return item;
  }

  /// <summary>删除本批失败草稿的句柄，避免调用者继续修改已经释放引用的对象。</summary>
  static void ReleaseBatchHandles(HashSet<string> beforeRefs) {
   foreach(var id in refs.Keys.Where(id=>!beforeRefs.Contains(id)).ToArray()){refs.Remove(id);refViews.Remove(id);}
  }

  /// <summary>先配置所有草稿，再用一次原生插入和撤销组提交；失败验证页面成员未变化。</summary>
  /// <param name="a">已经展开预设的 widgets 声明；所有控件属于当前 Basic 业务页面。</param>
  /// <param name="view">当前设计页，作为成功对象句柄与撤销事务的所有者。</param>
  /// <param name="page">当前页模型，失败检查以其原始控件集合为依据。</param>
  /// <param name="context">原生设计上下文；不打开属性窗口或执行运行时 IO。</param>
  /// <returns>有序组件句柄、请求与实际尺寸及持久化状态；成功后仍需保存和验证编译。</returns>
  /// <exception cref="AggregateException">插入失败且原生回滚或资源释放未完成。</exception>
  static object CreateCanvasBatch(Dictionary<string,object> a,object view,object page,object context) {
   RequireBasicPage(page);
   var specs=((IEnumerable)a["widgets"]).Cast<Dictionary<string,object>>().ToArray();
   if(specs.Length<1||specs.Length>30)throw new ArgumentException("Expected 1..30 widgets");
   var collection=Member(page,"Graphicses");var before=((IEnumerable)collection).Cast<object>().ToArray();
   var names=new HashSet<string>(before.Select(g=>Convert.ToString(Member(g,"Comment"))),StringComparer.Ordinal);
   foreach(var spec in specs){
    var name=S(spec,"name");
    if(String.IsNullOrWhiteSpace(name)||name.Length>80||!names.Add(name))throw new ArgumentException("Widget name is empty or already exists: "+name);
    BatchDrawingBounds(spec);
   }
   var entries=new List<BatchDrawingEntry>();var beforeRefs=new HashSet<string>(refs.Keys);
   object group=null,placement=null;Dictionary<string,object>[] results;
   try{
    foreach(var spec in specs){
     var prepare=Obj("op","drawing_prepare","tool",spec["tool"]);
     if(spec.ContainsKey("tool_properties"))prepare["tool_properties"]=spec["tool_properties"];
     var result=(Dictionary<string,object>)DrawingOperation(prepare);
     string id=S(result,"draft_id");var draft=drawingDrafts[id];
     var entry=new BatchDrawingEntry{Id=id,Draft=draft,Spec=spec,Requested=BatchDrawingBounds(spec)};entries.Add(entry);
     string modelRef=S((Dictionary<string,object>)result["model"],"$ref");
     if(spec.ContainsKey("properties")){
      var properties=((IEnumerable)spec["properties"]).Cast<Dictionary<string,object>>().ToArray();
      if(properties.Length>60)throw new ArgumentException("Too many widget properties");
      var reserved=new[]{"Comment","UniqueId","ComponentId","Position","IsReferenced"};
      if(properties.Any(p=>reserved.Contains(ModelPath(S(p,"member"))[0])))throw new ArgumentException("Use explicit name and bounds; identity and lifecycle properties are not preset fields");
      if(properties.Length>0)SetModelProperties(Obj("changes",properties.Select(p=>Obj("ref",modelRef,"member",p["member"],"value",p["value"])).ToArray()));
     }
     if(spec.ContainsKey("text")||spec.ContainsKey("text_style")){
      if(draft.Tool.GetType().Name!="StaticTextTool")throw new ArgumentException("text and text_style are only supported for StaticTextTool");
      var labels=((IEnumerable)ReadModelPath(draft.Model,"NormalInfo.StaticTextContentInfos")).Cast<object>().ToArray();
      if(labels.Length==0)throw new InvalidOperationException("Static text has no initialized language labels");
      foreach(var label in labels){
       if(spec.ContainsKey("text"))NativeSet(label,"LabelContent",spec["text"]);
       if(spec.ContainsKey("text_style")){
        var style=(Dictionary<string,object>)spec["text_style"];
        var font=ReadModelPath(label,"FontStyleInfo.Font");
        // 使用原生图形字库；不把文本渲染成不可本地化的图片切片。
        NativeSet(ReadModelPath(label,"FontStyleInfo"),"UseFontStyle",false);
        NativeSet(font,"FontType","Graphic");
        foreach(var pair in new[]{new[]{"font_name","FontName"},new[]{"font_size","FontSize"},new[]{"color","FontColor"},new[]{"bold","IsBold"}})
         if(style.ContainsKey(pair[0]))NativeSet(font,pair[1],style[pair[0]]);
       }
      }
     }
     if(spec.ContainsKey("local_addresses"))foreach(var address in ((IEnumerable)spec["local_addresses"]).Cast<Dictionary<string,object>>()){
      var target=ReadModelPath(draft.Model,S(address,"member"));
      var handle=(Dictionary<string,object>)DescribeOwned(target,draft);
      if(handle==null||!handle.ContainsKey("$ref"))throw new ArgumentException("Address path must reference a native address model");
      ConfigureLocalAddress(Obj("ref",handle["$ref"],"address",address["address"]),view);
     }
     // 属性和文本会改变最小尺寸，最终布局在这些配置之后施加，并回传原生实际值。
     var position=Member(draft.Model,"Position");
     foreach(var key in new[]{"X","Y","Width","Height"})NativeSet(position,key,spec[key.ToLowerInvariant()]);
     NativeSet(draft.Model,"Comment",spec["name"]);
     var setName=draft.Model.GetType().GetMethod("SetDisplayName",new Type[]{typeof(string)});
     if(setName!=null)setName.Invoke(draft.Model,new object[]{spec["name"]});
    }
    group=BeginModelChange(view,"MCP: create "+entries.Count+" controls");
    var items=Array.CreateInstance(Native("ICSharpCode.WpfDesign.DesignItem"),entries.Count);
    for(int i=0;i<entries.Count;i++)items.SetValue(RegisterBatchDrawingItem(context,entries[i].Draft),i);
    var root=Member(context,"RootItem");var add=Native("ICSharpCode.WpfDesign.PlacementType").GetField("AddItem").GetValue(null);
    placement=Native("ICSharpCode.WpfDesign.PlacementOperation").GetMethod("TryStartInsertNewComponents").Invoke(null,new object[]{root,items,entries.Select(e=>e.Requested).ToArray(),add});
    if(placement==null)throw new InvalidOperationException("Native designer rejected batch insertion");
    CallNative(placement,"Commit");
    // 插入事务已经关闭；后续校验失败由外层撤销组回滚，不能再次 Abort 已提交的插入。
    placement=null;
    var after=((IEnumerable)collection).Cast<object>().ToArray();
    if(after.Length!=before.Length+entries.Count||before.Any(g=>!after.Any(v=>Object.ReferenceEquals(g,v)))||entries.Any(e=>!after.Any(v=>Object.ReferenceEquals(e.Draft.Model,v))))
     throw new InvalidOperationException("Native batch inserted unexpected page members");
    results=entries.Select(e=>{
     var actual=(Dictionary<string,object>)DrawingBoundsResult(e.Draft.Model);
     var requested=Obj("x",e.Requested.X,"y",e.Requested.Y,"width",e.Requested.Width,"height",e.Requested.Height);
     // 字体或原生最小尺寸的修正不算插入失败，但必须明确提示客户端重新检查布局。
     bool adjusted=requested.Any(p=>Convert.ToDouble(p.Value)!=Convert.ToDouble(actual[p.Key]));
     return Obj("name",e.Spec["name"],"model",DescribeOwned(e.Draft.Model,view),
       "requested_bounds",requested,"actual_bounds",actual,"bounds_adjusted",adjusted);
    }).ToArray();
    CallNative(group,"Commit");
   }catch(Exception error){
    var failures=new List<Exception>{error};
    if(placement!=null)try{CallNative(placement,"Abort");}catch(Exception rollback){failures.Add(rollback);}
    if(group!=null)try{CallNative(group,"Abort");}catch(Exception rollback){failures.Add(rollback);}
    var after=((IEnumerable)collection).Cast<object>().ToArray();
    bool unchanged=after.Length==before.Length&&!after.Where((item,i)=>!Object.ReferenceEquals(item,before[i])).Any();
    if(!unchanged)failures.Add(new InvalidOperationException("Rollback did not restore the original page member order; inspect the page before retrying"));
    foreach(var e in entries){
     // 若原生回滚未移除该模型，不释放它仍在页面使用的引用，保留草稿供诊断。
     if(after.Any(item=>Object.ReferenceEquals(item,e.Draft.Model)))continue;
     try{if((bool)Member(e.Draft.Model,"IsReferenced"))CallNative(e.Draft.Model,"DisableReference");drawingDrafts.Remove(e.Id);}
     catch(Exception cleanup){failures.Add(cleanup);}
    }
    if(failures.Count==1){ReleaseBatchHandles(beforeRefs);throw;}
    throw new AggregateException("Batch creation failed and cleanup was incomplete; inspect the page and drafts",failures);
   }
   // 成功提交后再读未保存状态；回包或句柄整理不进入已关闭事务的回滚分支。
   foreach(var e in entries){e.Draft.CommittedView=view;drawingDrafts.Remove(e.Id);}
   return Obj("created",results,"count",results.Length,"native_undo_transaction",true,"dirty",Member(view,"IsDirty"),
     "canvas_changed",true,"persistence","save_project_to_persist","hardware_io",false);
  }
 }
}
