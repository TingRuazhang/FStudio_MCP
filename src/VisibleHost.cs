using System;
using System.Collections.Generic;
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
  static readonly object gate=new object();
  static readonly Dictionary<string,Dictionary<string,object>> jobs=new Dictionary<string,Dictionary<string,object>>();
  static Dictionary<string,Type> commands;
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
   File.WriteAllText(Path.Combine(stateDir,"endpoint.json"),Json().Serialize(Obj("pipe",pipeName,"pid",Process.GetCurrentProcess().Id,"workspace",workspace,"version","0.3.0","execution_mode",ExecutionMode,"visible_log",false)),new UTF8Encoding(false));
   new Thread(Listen){IsBackground=true,Name="FStudio MCP pipe"}.Start();
  }
  static void Log(string text) {File.AppendAllText(Path.Combine(stateDir,"visible-host.log"),DateTime.Now.ToString("o")+" "+text+Environment.NewLine,Encoding.UTF8);}
  static bool IsReadOnly(string op) {return op=="state"||op=="commands"||op=="output"||op=="inspect_object"||op=="read_member"||op=="drawing_state"||op=="drawing_catalog"||op=="build_status"||op=="build_diagnostics"||op=="pages_list";}
  static Type Native(string name) {foreach(var a in AppDomain.CurrentDomain.GetAssemblies()){var t=a.GetType(name,false);if(t!=null)return t;}throw new ArgumentException("Native type not loaded: "+name);}
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
   lock(buildGate)if(activeBuild!=null&&!activeBuild.Finalized&&!IsReadOnly(op)&&op!="build_cancel")throw new InvalidOperationException("A background build owns the project; wait for its build_id before changing or switching the project");
   if(op.StartsWith("build_"))return BackgroundBuildOperation(a);
   if(op=="sampling_add")return AddLocalSampling(a);
   if(op=="pages_list"||op.StartsWith("page_"))return NativePageOperation(a);
   if(op=="state")return LiveState();
   if(op=="output")return Output();
   if(op.StartsWith("drawing_"))return DrawingOperation(a);
   if(op=="create_project")return CreateProject(a);
   if(op=="inspect_object"||op=="read_member"||op=="set_member"||op=="set_members"||op=="invoke_method"||op=="native_factory")return ModelOperation(a);
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
   if(op=="health")return Obj("pid",Process.GetCurrentProcess().Id,"pipe",pipeName,"host_initialized",true,"workspace",workspace,"execution_mode",ExecutionMode,"visible_log",false);
   if(op=="job") {lock(gate){Dictionary<string,object> j;if(!jobs.TryGetValue(S(a,"job_id"),out j))throw new ArgumentException("Unknown job");return new Dictionary<string,object>(j);}}
   var id=Guid.NewGuid().ToString("N");var job=Obj("job_id",id,"operation",op,"status","queued");lock(gate)jobs[id]=job;
   dispatcher.BeginInvoke(new Action(()=>{
    lock(gate)job["status"]="running";
    bool writeLog=!IsReadOnly(op);
    if(writeLog)Log("开始 "+op+ (a.ContainsKey("command")?" · "+S(a,"command"):""));
    try{var result=OnUi(a);lock(gate){job["result"]=result;job["status"]="completed";}if(writeLog)Log("返回 "+op);}
    catch(Exception e){while(e is TargetInvocationException&&e.InnerException!=null)e=e.InnerException;lock(gate){job["status"]="failed";job["error"]=e.ToString();}Log("失败 "+op+" · "+e.Message);}
   }),op=="build_diagnostics"?DispatcherPriority.ContextIdle:DispatcherPriority.Normal);
   return Obj("job_id",id,"status","queued");
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
