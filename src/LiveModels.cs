using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace FStudioMcp {
 internal static partial class VisibleHost {
  static readonly Dictionary<string,object> refs=new Dictionary<string,object>();
  static readonly Dictionary<string,object> refViews=new Dictionary<string,object>();
  static void ClearRefs(){refs.Clear();refViews.Clear();drawingDrafts.Clear();}
  // Vendor getters that overflow the managed stack. A StackOverflowException is not catchable by any try/catch on
  // either side, so the host only survives if the call is refused before GetValue. Reproduced twice on 2026-09-08
  // at 15:02:23 and 15:06:17 (Application Error 1000, FStudio.exe 3.0.15685.0, exception 0xc00000fd, faulting
  // module unknown) by reading EllipseInfo.Normal, which the design surface also never needs: NormalInfo works.
  static readonly HashSet<string> StackOverflowGetters=new HashSet<string>{
   "Flexem.Studio.Components.Graphicses.EllipseInfo.Normal"};
  static object Member(object o,string n){
   if(o==null)return null;
   var t=o.GetType();
   var p=t.GetProperty(n,BindingFlags.Public|BindingFlags.Instance);
   if(p==null||p.GetIndexParameters().Length!=0)throw new ArgumentException("Unknown readable property: "+n);
   var declaring=p.DeclaringType??t;
   if(StackOverflowGetters.Contains(t.FullName+"."+n)||StackOverflowGetters.Contains(declaring.FullName+"."+n))
    throw new InvalidOperationException("Refused: the native getter "+declaring.FullName+"."+n+" is a known stack-overflow landmine that terminates the FStudio process and cannot be caught. This member is unreadable by design; read its documented equivalent instead.");
   return p.GetValue(o,null);
  }
  static bool Scalar(Type t){t=Nullable.GetUnderlyingType(t)??t;return t.IsPrimitive||t.IsEnum||t==typeof(string)||t==typeof(decimal)||t==typeof(Guid)||t==typeof(DateTime)||t==typeof(TimeSpan);}
  static object Describe(object o){
   if(o==null)return null;var t=o.GetType();
   if(Scalar(t)){if(t.IsEnum||t==typeof(Guid)||t==typeof(DateTime)||t==typeof(TimeSpan))return Convert.ToString(o,CultureInfo.InvariantCulture);if(o is double&&(double.IsNaN((double)o)||double.IsInfinity((double)o)))return o.ToString();return o;}
   string id=refs.FirstOrDefault(x=>Object.ReferenceEquals(x.Value,o)).Key;
   if(id==null){if(refs.Count>20000)throw new InvalidOperationException("Too many model handles; reopen project");id=Guid.NewGuid().ToString("N");refs[id]=o;}
   if(t.FullName=="Flexem.Studio.HMI.Window.HMIWindowViewContent")refViews[id]=o;
   return Obj("$ref",id,"type",t.FullName,"enumerable",o is IEnumerable);
  }
  static object DescribeOwned(object o,object view){var result=Describe(o);var d=result as Dictionary<string,object>;if(view!=null&&d!=null&&d.ContainsKey("$ref"))refViews[S(d,"$ref")]=view;return result;}
  static object OwningScope(string id){object scope;return refViews.TryGetValue(id,out scope)?scope:null;}
  static object OwningView(string id){var scope=OwningScope(id);var draft=scope as DrawingDraft;return draft==null?scope:draft.CommittedView;}
  static object Ref(Dictionary<string,object>a){object o;if(!refs.TryGetValue(S(a,"ref"),out o))throw new ArgumentException("Unknown or expired model reference");return o;}
  static object Resolve(string name){var type=Native(name);var ioc=Native("Flexem.Infrastructure.IoC");return ioc.GetMethods().Single(m=>m.Name=="Resolve"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0).MakeGenericMethod(type).Invoke(null,new object[0]);}
  static object LiveState(){
   var windows=Application.Current==null?new Window[0]:Application.Current.Windows.Cast<Window>().ToArray();
   var hmi=CurrentProject()==null?null:Member(Resolve("Flexem.Studio.Hmi.Project.IHmiProjectService"),"CurrentProject");
   return Obj("project",ProjectFile(),"project_model",Describe(CurrentProject()),"hmi_model",Describe(hmi),"workbench",Describe(Resolve("Flexem.Studio.Workbench.IWorkbench")),"windows",windows.Select(w=>Obj("title",w.Title,"type",w.GetType().FullName,"window_model",Describe(w),"data_context",Describe(w.DataContext),"visible",w.IsVisible,"window_state",w.WindowState.ToString())).ToArray(),"visible_log",false,"execution_mode",ExecutionMode);
  }
  static object Output(){
   var t=Native("Flexem.Studio.Pads.Output.OutputPad");var field=t.GetField("_instance",BindingFlags.NonPublic|BindingFlags.Static);
   var view=field==null?null:field.GetValue(null);
   if(view==null)return Obj("project",ProjectFile(),"categories",new object[0],"status","not_created");
   var categories=(IEnumerable)Member(view,"OutputCategories");
   return Obj("project",ProjectFile(),"categories",categories.Cast<object>().Select(c=>Obj("category",Member(c,"Category"),"display_name",Member(c,"DisplayCategory"),"text",Member(c,"Text"))).ToArray());
  }
  static object ConvertArg(object v,Type t){
   if(v==null){if(t.IsValueType&&Nullable.GetUnderlyingType(t)==null)throw new ArgumentException("Null is not allowed for "+t.FullName);return null;}
   var d=v as Dictionary<string,object>;if(d!=null&&d.ContainsKey("$ref")){object r;if(!refs.TryGetValue(S(d,"$ref"),out r)||!t.IsInstanceOfType(r))throw new ArgumentException("Incompatible model reference");return r;}
   t=Nullable.GetUnderlyingType(t)??t;
   if(t==typeof(byte[])&&v is string)return Convert.FromBase64String((string)v);
   if(t==typeof(Type)&&d!=null&&d.ContainsKey("$type"))return Native(S(d,"$type"));
   if(t.IsInstanceOfType(v))return v;
   if(t.IsEnum)return Enum.Parse(t,Convert.ToString(v),false);
   if(t==typeof(Guid))return Guid.Parse(Convert.ToString(v));
   if(t.IsArray){var values=(IEnumerable)v;var items=values.Cast<object>().ToArray();var arr=Array.CreateInstance(t.GetElementType(),items.Length);for(int i=0;i<items.Length;i++)arr.SetValue(ConvertArg(items[i],t.GetElementType()),i);return arr;}
   if(t.IsGenericType&&v is IEnumerable&&!(v is string)){
    var g=t.GetGenericTypeDefinition();
    if(g==typeof(IEnumerable<>)||g==typeof(ICollection<>)||g==typeof(IList<>)||g==typeof(List<>)){
     var itemType=t.GetGenericArguments()[0];var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
     foreach(var item in (IEnumerable)v)list.Add(ConvertArg(item,itemType));return list;
    }
   }
   if(Scalar(t))return Convert.ChangeType(v,t,CultureInfo.InvariantCulture);
   throw new ArgumentException("Complex arguments must use a compatible $ref: "+t.FullName);
  }
  static object MethodEntry(MethodInfo m){return Obj("name",m.Name,"signature",m.ToString(),"return_type",m.ReturnType.FullName,"parameters",m.GetParameters().Select(p=>Obj("name",p.Name,"type",p.ParameterType.FullName,"optional",p.IsOptional)).ToArray());}
  static void RequireBackgroundModelTarget(object target) {
   if(target is DependencyObject||target is System.Windows.Input.ICommand||target is ICSharpCode.Core.ICommand)
    throw new InvalidOperationException("BACKGROUND_ONLY: UI objects and interactive commands cannot be changed or invoked through model APIs.");
   if(target==null)return;
   var type=target.GetType();
   if(type.Namespace=="Flexem.Studio.GraphicsDesigner.Tools"||type.GetInterfaces().Any(i=>new[]{"Flexem.Studio.Workbench.IWorkbench","Flexem.Studio.Workbench.IViewContent","ICSharpCode.SharpDevelop.Gui.IViewContent","ICSharpCode.SharpDevelop.Gui.IWorkbenchWindow","ICSharpCode.SharpDevelop.Gui.IWorkbench","Caliburn.Micro.IWindowManager","Flexem.Studio.Workbench.IAsynchronousWaitDialog"}.Contains(i.FullName)))
    throw new InvalidOperationException("BACKGROUND_ONLY: Designer tools and workbench UI are inspect-only through model APIs.");
  }
  /// <summary>阻止会启动交互窗口的模型方法；图库导入还需在参数转换后检查重名。</summary>
  static void RequireBackgroundMethod(object target,string method) {
   RequireBackgroundModelTarget(target);
   string[] interactive={"Activate","Show","ShowDialog","ShowWindow","ShowSettings","ShowSetting","Edit","Execute","Run","Focus","BringToFront","SelectGraphics","StartDrag","DoDragDrop","OpenOrActiveWindow","CreateWindow","CreateWindowWithoutOpen","DeleteWindow","Close","CloseAsync","TryClose","Deactivate","Hide","CloseWindow","OpenHMIWindow","LocateSettings","ImportOrSelectStatusVector","BatchAddGraphs","OpenGraph"};
   if(interactive.Contains(method)||new[]{"Show","Activate","Focus","Bring","SelectGraphic"}.Any(prefix=>method.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))||method.IndexOf("Dialog",StringComparison.OrdinalIgnoreCase)>=0||method.IndexOf("Mouse",StringComparison.OrdinalIgnoreCase)>=0||method.IndexOf("Drag",StringComparison.OrdinalIgnoreCase)>=0)
    throw new InvalidOperationException("BACKGROUND_ONLY: Interactive method "+method+" is disabled; use the dedicated background API.");
  }
  /// <summary>图库导入仅允许显式、无重名的来源，避免选择或冲突对话框阻塞后台队列。</summary>
  static void RequireBackgroundGraphImport(object target,string method,object[] arguments){
   if(target==null||target.GetType().FullName!="Flexem.Studio.HMI.ProjectGraphLibraryService"||method!="Import")return;
   if(arguments.Length==0)throw new InvalidOperationException("BACKGROUND_ONLY: Import requires an explicit source graph; the graph-selection dialog is disabled");
   if(arguments[0]==null)throw new ArgumentException("Source graph is required");
   var category=Member(Member(CurrentProject(),"GraphLib"),"CurrentProjectGraphCategory");
   string directory=Convert.ToString(Member(category,"Dir")),name=Convert.ToString(Member(arguments[0],"Name"));
   // 原生按文件节点存储图库；检测磁盘文件可覆盖已保存但尚未加载到集合的重名资源。
   if(System.IO.Directory.Exists(directory)&&System.IO.Directory.EnumerateFiles(directory,"*.fsvg").Any(p=>String.Equals(System.IO.Path.GetFileNameWithoutExtension(p),name,StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("BACKGROUND_ONLY: A graph named '"+name+"' already exists; reuse its model handle instead of opening the native conflict dialog");
  }
  /// <summary>按精确签名转换参数并调用已通过后台约束的方法，返回归属当前会话的结果句柄。</summary>
  static object Invoke(MethodInfo[] methods,object target,Dictionary<string,object>a){
   var name=S(a,"method");var args=a.ContainsKey("arguments")?((IEnumerable)a["arguments"]).Cast<object>().ToArray():new object[0];
   var choices=methods.Where(m=>m.Name==name&&!m.ContainsGenericParameters&&m.GetParameters().Length==args.Length&&( !a.ContainsKey("signature")||m.ToString()==S(a,"signature"))).ToArray();
   if(choices.Length!=1)throw new ArgumentException("Method is missing or ambiguous; use exact signature from inspect_object");
   var method=choices[0];RequireBackgroundMethod(target,name);var pars=method.GetParameters();
   var converted=args.Select((x,i)=>ConvertArg(x,pars[i].ParameterType)).ToArray();
   RequireBackgroundGraphImport(target,name,converted);
   return DescribeOwned(method.Invoke(target,converted),a.ContainsKey("ref")?OwningScope(S(a,"ref")):null);
  }
  /// <summary>记录预校验后的属性写入及路径依赖，供去重、冲突检查和失败恢复使用。</summary>
  sealed class ModelPropertyChange {internal string Ref,Member;internal object Target,Scope,Before,After;internal PropertyInfo Property;internal object[] PathOwners;}
  /// <summary>拆分已知属性路径；不支持索引表达式或方法调用，最多十六级。</summary>
  static string[] ModelPath(string path) {
   if(String.IsNullOrEmpty(path)||path.Length>512||!System.Text.RegularExpressions.Regex.IsMatch(path,@"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*){0,15}\z"))
    throw new ArgumentException("Expected a property path with 1..16 segments");
   return path.Split('.');
  }
  /// <summary>按显式路径读取属性，沿用危险 getter 拒绝规则；中间空值给出定位错误。</summary>
  static object ReadModelPath(object root,string path) {
   object value=root;
   foreach(var name in ModelPath(path)){
    if(value==null)throw new ArgumentException("Null intermediate object in path: "+path);
    value=Member(value,name);
   }
   return value;
  }
  /// <summary>单次 UI 任务读取指定字段；只返回末级句柄，不展开集合或递归探测模型。</summary>
  static object ReadModelProperties(Dictionary<string,object> a) {
   var input=((IEnumerable)a["reads"]).Cast<object>().ToArray();
   if(input.Length<1||input.Length>100)throw new ArgumentException("Expected 1..100 reads");
   var results=new List<object>();
   foreach(var entry in input){
    var read=entry as Dictionary<string,object>;
    if(read==null)throw new ArgumentException("Each read must contain ref and member");
    var id=S(read,"ref");var path=S(read,"member");
    results.Add(Obj("ref",id,"member",path,"value",DescribeOwned(ReadModelPath(Ref(read),path),OwningScope(id))));
   }
   return Obj("results",results.ToArray());
  }
  // The designer owns the paired WPF/CSLA transaction. Beginning the CSLA
  // session directly would nest another transaction when geometry updates the view.
  static object BeginModelChange(object view,string name){return CallNative(Member(Member(view,"DesignContext"),"RootItem"),"OpenGroup",name);}
  /// <summary>先解析整批路径和类型，再一次写入；拒绝别名重复及父子替换，失败沿用原生撤销或草稿回滚。</summary>
  static object SetModelProperties(Dictionary<string,object> a) {
   var input=(IEnumerable)a["changes"];var changes=new List<ModelPropertyChange>();
   foreach(var entry in input){
    if(changes.Count>=100)throw new ArgumentException("At most 100 property changes are allowed");
    var change=entry as Dictionary<string,object>;if(change==null)throw new ArgumentException("Each change must contain ref, member and value");
    var target=Ref(change);var owners=new List<object>();
    var path=S(change,"member");var parts=ModelPath(path);
    for(int i=0;i<parts.Length-1;i++){
     if(target==null)throw new ArgumentException("Null intermediate object in path: "+path);
     RequireBackgroundModelTarget(target);owners.Add(target);target=Member(target,parts[i]);
    }
    if(target==null)throw new ArgumentException("Null parent object in path: "+path);
    // 值类型属性会被反射装箱；修改装箱副本不会回写原模型，必须拒绝假成功。
    if(target.GetType().IsValueType)throw new ArgumentException("Cannot write a boxed value-type parent; replace the owning property: "+path);
    RequireBackgroundModelTarget(target);owners.Add(target);
    var name=parts[parts.Length-1];var property=target.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.Instance);
    if(property==null||property.GetSetMethod()==null||property.GetGetMethod()==null||property.GetIndexParameters().Length!=0)throw new ArgumentException("Property is not publicly readable and writable: "+name);
    var id=S(change,"ref");var scope=OwningScope(id);
    if(scope==null)throw new ArgumentException("Batch properties require a page or draft scope; obtain handles from drawing_state or drawing_prepare");
    if(changes.Any(c=>Object.ReferenceEquals(c.Target,target)&&c.Property.Name==name))throw new ArgumentException("Duplicate property in batch: "+path);
    if(changes.Count>0&&!Object.ReferenceEquals(changes[0].Scope,scope))throw new ArgumentException("All batch properties must belong to the same page or draft");
    var before=Member(target,name);
    // 父属性替换会使另一项解析到旧对象；两个方向均拒绝，避免成功写到脱离页面的对象。
    if(changes.Any(c=>(c.Before!=null&&owners.Any(o=>Object.ReferenceEquals(o,c.Before)))||(before!=null&&c.PathOwners.Any(o=>Object.ReferenceEquals(o,before)))))
     throw new ArgumentException("Parent/child replacement conflict in batch: "+path);
    changes.Add(new ModelPropertyChange{Ref=id,Member=path,Target=target,Scope=scope,Property=property,Before=before,After=ConvertArg(change["value"],property.PropertyType),PathOwners=owners.ToArray()});
   }
   if(changes.Count==0)throw new ArgumentException("At least one property change is required");
   var owner=OwningView(changes[0].Ref);object transaction=null;int applied=0;
   try{
    if(owner!=null)transaction=BeginModelChange(owner,"MCP: properties");
    foreach(var c in changes){applied++;c.Property.SetValue(c.Target,c.After,null);}
    if(transaction!=null)CallNative(transaction,"Commit");
   }catch(Exception error){
    var failures=new List<Exception>{error};
    if(transaction!=null){try{CallNative(transaction,"Abort");}catch(Exception rollback){failures.Add(rollback);}}
    else for(int i=applied-1;i>=0;i--){try{changes[i].Property.SetValue(changes[i].Target,changes[i].Before,null);}catch(Exception rollback){failures.Add(rollback);}}
    if(failures.Count>1)throw new AggregateException("Property change failed and rollback was incomplete; inspect the model before retrying",failures);
    throw;
   }
   return Obj("results",changes.Select(c=>Obj("ref",c.Ref,"member",c.Member,"value",DescribeOwned(c.Property.GetValue(c.Target,null),c.Scope))).ToArray(),"native_undo_transaction",transaction!=null,"persistence","native_model_changed_save_project_to_persist");
  }
  /// <summary>在已打开工程内处理模型操作，批量路径与单属性共享同一访问和作用域规则。</summary>
  static object ModelOperation(Dictionary<string,object>a){
   RequireProject();var op=S(a,"op");
   if(op=="set_members")return SetModelProperties(a);
   if(op=="read_members")return ReadModelProperties(a);
   if(op=="native_factory"){
    var t=Native(S(a,"type"));if(t.Namespace==null||!t.Namespace.StartsWith("Flexem.Studio."))throw new ArgumentException("Factory type must be a native FStudio model");
    var m=S(a,"method");if(m!="CreateAsChild"&&m!="Create"&&m!="CreateNew")throw new ArgumentException("Only native model creation factories are exposed");
    return Obj("value",Invoke(t.GetMethods(BindingFlags.Public|BindingFlags.Static),null,a));
   }
   var o=Ref(a);var type=o.GetType();
   if(op=="inspect_object")return Obj("object",Describe(o),"properties",type.GetProperties(BindingFlags.Public|BindingFlags.Instance).Where(p=>p.GetIndexParameters().Length==0).Select(p=>Obj("name",p.Name,"type",p.PropertyType.FullName,"readable",p.CanRead,"writable",p.GetSetMethod()!=null,"enum_values",(Nullable.GetUnderlyingType(p.PropertyType)??p.PropertyType).IsEnum?Enum.GetNames(Nullable.GetUnderlyingType(p.PropertyType)??p.PropertyType):null)).ToArray(),"methods",type.GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(m=>!m.IsSpecialName&&m.DeclaringType!=typeof(object)&&!m.ContainsGenericParameters).Select(MethodEntry).ToArray());
   if(op=="read_member"){
    object value=a.ContainsKey("member")?ReadModelPath(o,S(a,"member")):o;
    if(a.ContainsKey("index")){var enumerable=value as IEnumerable;if(enumerable==null)throw new ArgumentException("Value is not enumerable");value=enumerable.Cast<object>().ElementAt(Convert.ToInt32(a["index"]));}
    var owner=OwningScope(S(a,"ref"));
    if(value is IEnumerable&&!(value is string)){int offset=a.ContainsKey("offset")?Convert.ToInt32(a["offset"]):0;return Obj("value",DescribeOwned(value,owner),"items",((IEnumerable)value).Cast<object>().Skip(offset).Take(100).Select(x=>DescribeOwned(x,owner)).ToArray(),"offset",offset);}
    return Obj("value",DescribeOwned(value,owner));
   }
   if(op=="set_member"){
    RequireBackgroundModelTarget(o);
    var p=type.GetProperty(S(a,"member"),BindingFlags.Public|BindingFlags.Instance);if(p==null||p.GetSetMethod()==null||p.GetIndexParameters().Length!=0)throw new ArgumentException("Property is not publicly writable");
    var owner=OwningView(S(a,"ref"));object transaction=null;
    try{
     if(owner!=null)transaction=BeginModelChange(owner,"MCP: "+p.Name);
     p.SetValue(o,ConvertArg(a["value"],p.PropertyType),null);
     if(transaction!=null)CallNative(transaction,"Commit");
    }catch{if(transaction!=null)CallNative(transaction,"Abort");throw;}
    return Obj("value",DescribeOwned(p.GetValue(o,null),owner),"native_undo_transaction",transaction!=null,"persistence","native_model_changed_save_project_to_persist");
   }
   if(op=="invoke_method")return Obj("value",Invoke(type.GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(m=>m.DeclaringType!=typeof(object)&&!m.IsSpecialName).ToArray(),o,a));
   throw new ArgumentException("Unknown model operation");
  }
 }
}
