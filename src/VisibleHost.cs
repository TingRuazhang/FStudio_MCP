using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using ICSharpCode.Core;

namespace FStudioMcp {
 public sealed class StartHost : AbstractCommand {
  static bool started;
  public override void Run() { if(started)return; started=true; VisibleHost.Start(); }
 }
 internal static partial class VisibleHost {
  static Dispatcher dispatcher;
  const string ExecutionMode="background_with_live_canvas";
  // 端点与健康检查共用 API 版本，便于客户端分辨磁盘已更新但宿主仍加载旧 DLL 的情况。
  const string ApiVersion="0.5.0";
  // 仅公布实际已实现的通用宿主操作，不把缺失能力或私有扩展声明为可调用接口。
  static readonly string[] HostFeatures={"drawing_snapshot","drawing_create_many","read_members","apply_members","member_paths","inline_job_result",
   "page_open_public","drawing_configure_modbus_rtu_address","drawing_configure_numeric_zoom","drawing_configure_numeric_limits",
   "build_start","build_status","build_cancel","build_diagnostics","task_render_canvas","task_simulate_offline",
   "task_simulate_compiled_offline","task_simulator_capture","task_simulator_close","task_compiled_helper_close"};
  static readonly object gate=new object();
  static readonly Dictionary<string,Dictionary<string,object>> jobs=new Dictionary<string,Dictionary<string,object>>();
  static Dictionary<string,Type> commands;
  // 仅缓存已经加载的类型元数据；查找失败不缓存，允许 FStudio 后续加载插件。
  static readonly ConcurrentDictionary<string,Type> nativeTypes=new ConcurrentDictionary<string,Type>(StringComparer.Ordinal);
  static string workspace, stateDir, pipeName;
  static Dictionary<string,object> Obj(params object[] values) {var d=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)d[(string)values[i]]=values[i+1];return d;}
  static string S(Dictionary<string,object> a,string key) {return Convert.ToString(a[key]);}
  static JavaScriptSerializer Json() {return new JavaScriptSerializer{MaxJsonLength=8000000};}
  internal static void Start() {
   dispatcher=Dispatcher.CurrentDispatcher;
   var location=Path.GetDirectoryName(typeof(VisibleHost).Assembly.Location);
   var config=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(location,"host-settings.json")));
   workspace=Path.GetFullPath(S(config,"workspace")).TrimEnd('\\')+"\\";
   stateDir=Path.GetFullPath(S(config,"state_directory"));Directory.CreateDirectory(stateDir);
   pipeName="fstudio-mcp-"+Process.GetCurrentProcess().Id;
   Log("宿主接口已启动。后台执行，仅原生画布显示绘图变化。");
   File.WriteAllText(Path.Combine(stateDir,"endpoint.json"),Json().Serialize(Obj("pipe",pipeName,"pid",Process.GetCurrentProcess().Id,"workspace",workspace,"version",ApiVersion,"execution_mode",ExecutionMode,"visible_log",false)),new UTF8Encoding(false));
   new Thread(Listen){IsBackground=true,Name="FStudio MCP pipe"}.Start();
  }
  static void Log(string text) {File.AppendAllText(Path.Combine(stateDir,"visible-host.log"),DateTime.Now.ToString("o")+" "+text+Environment.NewLine,Encoding.UTF8);}
  // PNG 导出只观察既有画布或已拥有的模拟器，不保存/切换工程；这两项沿用只读观察权限。
  // compiled preflight 会授权服务端安装 Bin，不能因为该阶段自身只读就绕过编译所有权。
  static bool IsReadOnly(string op) {return op=="state"||op=="commands"||op=="output"||op=="inspect_object"||op=="read_member"||op=="read_members"||op=="drawing_snapshot"||op=="drawing_state"||op=="drawing_catalog"||op=="build_status"||op=="build_diagnostics"||op=="pages_list"||op=="task_render_canvas"||op=="task_simulator_capture";}
  /// <summary>复用运行中程序集的类型元数据，减少每个控件重复扫描所有程序集的开销。</summary>
  static Type Native(string name) {
   Type cached;if(nativeTypes.TryGetValue(name,out cached))return cached;
   foreach(var a in AppDomain.CurrentDomain.GetAssemblies()){var t=a.GetType(name,false);if(t!=null){nativeTypes[name]=t;return t;}}
   throw new ArgumentException("Native type not loaded: "+name);
  }
  static object ProjectService(string method,params object[] args) {return Native("Flexem.Studio.Project.FsProjectService").GetMethod(method,BindingFlags.Public|BindingFlags.Static).Invoke(null,args);}
  static object CurrentProject() {return Native("Flexem.Studio.Project.FsProjectService").GetProperty("CurrentProject").GetValue(null,null);}
  static string ProjectFile() {var p=CurrentProject();return p==null?null:Convert.ToString(p.GetType().GetProperty("FileName").GetValue(p,null));}
  static int DirtyViewCount() {
   // A diagnostic must never be the reason the refusal path throws, so failures report -1.
   try{
    var workbench=Native("ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton").GetProperty("Workbench",BindingFlags.Public|BindingFlags.Static).GetValue(null,null);
    var views=Member(workbench,"ViewContentCollection");
    return ((System.Collections.IEnumerable)views).Cast<object>().Count(x=>{
     var flag=x.GetType().GetProperty("IsDirty");object value=flag==null?null:flag.GetValue(x,null);
     return value is bool&&(bool)value;});
   }catch(Exception){return -1;}
  }
  static void Within(string file) {if(!Path.GetFullPath(file).StartsWith(workspace,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Operation is outside configured MCP workspace");}
  static void RequireProject() {var f=ProjectFile();if(f==null)throw new InvalidOperationException("No open project");Within(f);}
  static Dictionary<string,Type> Commands() {
   if(commands!=null)return commands;
   commands=new Dictionary<string,Type>();
   foreach(var a in AppDomain.CurrentDomain.GetAssemblies().Where(x=>x.GetName().Name.StartsWith("Flexem.")||x.GetName().Name.StartsWith("FStudio."))) {
    Type[] types;try{types=a.GetTypes();}catch(ReflectionTypeLoadException e){types=e.Types.Where(x=>x!=null).ToArray();}
    foreach(var t in types) if(t.IsPublic&&!t.IsAbstract&&t.GetConstructor(Type.EmptyTypes)!=null&&(typeof(ICSharpCode.Core.ICommand).IsAssignableFrom(t)||typeof(System.Windows.Input.ICommand).IsAssignableFrom(t)))commands[t.FullName]=t;
   }
   return commands;
  }
  static object OnUi(Dictionary<string,object> a) {
   string op=S(a,"op");
   // 所有入口先遵守原有编译所有权；预览启动会保存工程或使用 Bin，关闭也会改变运行环境。
   // build_cancel 的原有特例和 build_status/diagnostics 的只读权限保持不变。
   lock(buildGate)if(activeBuild!=null&&!activeBuild.Finalized&&!IsReadOnly(op)&&op!="build_cancel")throw new InvalidOperationException("A background build owns the project; wait for its build_id before changing or switching the project");
   if(op=="task_compiled_preview_preflight")return TaskCompiledPreviewPreflight();
   if(op=="task_simulate_compiled_offline")return TaskPreviewLaunchCompiledOffline();
   if(op=="task_compiled_helper_close")return TaskPreviewCloseCompiledHelper();
   if(op=="task_render_canvas"||op=="task_simulate_offline"||op=="task_simulator_capture"||op=="task_simulator_close")return NativeTaskPreview(a);
   if(op.StartsWith("build_"))return BackgroundBuildOperation(a);
   if(op=="sampling_add")return AddLocalSampling(a);
   if(op=="pages_list"||op.StartsWith("page_"))return NativePageOperation(a);
   if(op=="state")return LiveState();
   if(op=="output")return Output();
   if(op.StartsWith("drawing_"))return DrawingOperation(a);
   if(op=="create_project")return CreateProject(a);
   if(op=="inspect_object"||op=="read_member"||op=="read_members"||op=="set_member"||op=="set_members"||op=="apply_members"||op=="invoke_method"||op=="native_factory")return ModelOperation(a);
   if(op=="commands"){commands=null;return Obj("commands",Commands().OrderBy(x=>x.Key).Select(x=>Obj("command",x.Key,"assembly",x.Value.Assembly.GetName().Name,"status","discovered_not_executed")).ToArray());}
   // 在调用原生打开服务之前校验目录，超长路径不会创建无法应答的模态窗口。
   if(op=="open_project") {string f=Path.GetFullPath(S(a,"file"));Within(f);RequireNativeProjectDirectory(Path.GetDirectoryName(f));if(!File.Exists(f)||Path.GetExtension(f)!=".fsprj")throw new ArgumentException("Expected an existing fsprj");if(String.Equals(ProjectFile(),f,StringComparison.OrdinalIgnoreCase))return Obj("project",f,"already_open",true);ClearRefs();ProjectService("OpenProjectWithPath",f);if(!String.Equals(ProjectFile(),f,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("FStudio did not open the requested project; inspect native dialogs/output");return Obj("project",ProjectFile());}
   if(op=="close_project") {
    RequireProject();
    bool discard=a.ContainsKey("discard_unsaved")&&(bool)a["discard_unsaved"];
    // CheckOpenedWindow builds a save-changes dialog on a dirty canvas. In background mode nothing can answer it
    // and the close job never returns, so refuse before the native call instead of entering that path.
    if(!discard&&!(bool)ProjectService("CanCloseCurrentProject"))throw new InvalidOperationException("Native close refused: "+DirtyViewCount()+" open view(s) are dirty. Save first, or pass discard_unsaved=true to close and drop the unsaved changes.");
    object closed;
    try {closed=ProjectService("CloseCurrentProject",!discard);}
    catch(Exception e) {
     var inner=e is TargetInvocationException&&e.InnerException!=null?e.InnerException:e;
     if(ProjectFile()!=null)throw new InvalidOperationException("Native close left the project open ("+inner.GetType().FullName+": "+inner.Message+"). Unsaved canvas changes make the native window check build a save-changes dialog, which cannot exist in background mode. Save first, or pass discard_unsaved=true to skip the native window check and drop the unsaved changes.");
     ClearRefs();
     return Obj("closed",true,"discard_unsaved",discard,"native_handler_exception",inner.GetType().FullName+": "+inner.Message,"vendor_note","Flexem.Studio.HMI.Window.WindowsToolBox.ProjectServiceOnProjectClosed sets _comboBox.ItemsSource on the ProjectClosed event; in background mode the tool box is never constructed so that field is null. The project reference was already released, so the close succeeded.");
    }
    if(!(bool)closed)throw new InvalidOperationException("Native close was cancelled by CheckOpenedWindow; the project is still open. Save first, or pass discard_unsaved=true.");
    if(ProjectFile()!=null)throw new InvalidOperationException("Native close reported success but the project is still open");
    ClearRefs();
    return Obj("closed",true,"discard_unsaved",discard);
   }
   if(op=="save_project") {RequireProject();ProjectService("SaveCurrentProject");return Obj("project",ProjectFile());}
   if(op=="command") {
    throw new InvalidOperationException("BACKGROUND_ONLY: Interactive native commands are disabled. Use background drawing, model properties, pages or build APIs; no dialog, activation or mouse operation was dispatched.");
   }
   throw new ArgumentException("Unknown host operation: "+op);
  }
  static object Handle(Dictionary<string,object> a) {
   var op=S(a,"op");
   if(op=="health")return Obj("pid",Process.GetCurrentProcess().Id,"pipe",pipeName,"host_initialized",true,"workspace",workspace,"version",ApiVersion,"features",HostFeatures,"execution_mode",ExecutionMode,"visible_log",false);
   if(op=="job") {lock(gate){Dictionary<string,object> j;if(!jobs.TryGetValue(S(a,"job_id"),out j))throw new ArgumentException("Unknown job");return new Dictionary<string,object>(j);}}
   var timer=Stopwatch.StartNew();
   var id=Guid.NewGuid().ToString("N");var job=Obj("job_id",id,"operation",op,"status","queued");lock(gate)jobs[id]=job;
   dispatcher.BeginInvoke(new Action(()=>{
    var queuedMs=timer.Elapsed.TotalMilliseconds;
    lock(gate){job["status"]="running";job["queue_ms"]=queuedMs;}
    bool writeLog=!IsReadOnly(op);
    object result=null;string error=null;
    try{
     if(writeLog)Log("开始 "+op+ (a.ContainsKey("command")?" · "+S(a,"command"):""));
     result=OnUi(a);
    }catch(Exception e){
     while(e is TargetInvocationException&&e.InnerException!=null)e=e.InnerException;
     error=e.ToString();
    }finally{
     // 在同一锁下发布终态与计时，并唤醒管道线程。日志失败不能把已提交操作改报失败。
     string status=error==null?"completed":"failed";
     lock(gate){
      double elapsed=timer.Elapsed.TotalMilliseconds;job["elapsed_ms"]=elapsed;job["execution_ms"]=elapsed-queuedMs;
      if(error==null)job["result"]=result;else job["error"]=error;
      job["status"]=status;Monitor.PulseAll(gate);
     }
     try{if(writeLog||status=="failed")Log(status+" "+op);}catch(Exception){/* 诊断日志不可使 UI 调度器崩溃或改变已经发布的终态。 */}
    }
   }),op=="build_diagnostics"?DispatcherPriority.ContextIdle:DispatcherPriority.Normal);
   // 只在独立管道线程短等 40 ms，不阻塞 UI，不改变任务身份，也不重试修改。
   // 常见小操作可在首次应答携带结果；慢操作仍立即交还原 job_id 供后续查询。
   lock(gate){
    while(S(job,"status")=="queued"||S(job,"status")=="running"){
     int remaining=40-(int)timer.ElapsedMilliseconds;if(remaining<=0)break;
     Monitor.Wait(gate,remaining);
    }
    return new Dictionary<string,object>(job);
   }
  }
  static void Listen() {
   while(true)try{
    var security=new PipeSecurity();security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
    using(var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.None,8192,8192,security)){
     pipe.WaitForConnection();using(var reader=new StreamReader(pipe,new UTF8Encoding(false),false,8192,true))using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),8192,true){AutoFlush=true}){
      string line=reader.ReadLine();if(line==null)continue;
      try{var result=Handle(Json().Deserialize<Dictionary<string,object>>(line));writer.WriteLine(Json().Serialize(Obj("ok",true,"data",result)));}
      catch(Exception e){writer.WriteLine(Json().Serialize(Obj("ok",false,"error",e.Message)));}
     }
    }
   }catch(Exception e){File.AppendAllText(Path.Combine(stateDir,"host-errors.log"),e.ToString()+Environment.NewLine);Thread.Sleep(1000);}
  }
 }
}
