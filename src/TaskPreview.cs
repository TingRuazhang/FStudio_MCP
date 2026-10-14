using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FStudioMcp {
 internal static partial class VisibleHost {
  // 这些状态只属于本宿主中的本次离线启动请求；不接受客户端传入任意进程或窗口句柄。
  static HashSet<int> taskPreviewExistingSimulatorIds;
  static DateTime taskPreviewSimulatorRequestedUtc;
  static string taskPreviewSimulatorProject;
  static int? taskPreviewCapturedSimulatorId;
  static DateTime? taskPreviewCapturedSimulatorStartUtc;
  static object taskPreviewSimulationOperation;
  static object taskPreviewSimulationResolver;

  /// <summary>记录原生模拟/编译输出，不打开执行日志窗口；其四个接口成员均已由本机元数据确认。</summary>
  sealed class TaskPreviewSimulationMessages:RealProxy {
   internal TaskPreviewSimulationMessages(Type contract):base(contract){}
   public override IMessage Invoke(IMessage message) {
    var call=(IMethodCallMessage)message;
    try {
     switch(call.MethodName) {
      case "AppendLine":case "AppendText":Log("离线模拟原生输出："+Convert.ToString(call.Args[0]));break;
      case "Clear":Log("离线模拟原生输出已清空。");break;
      case "BringToFront":break;
      default:throw new NotSupportedException("Unsupported native simulation output member: "+call.MethodName);
     }
     return new ReturnMessage(null,new object[0],0,call.LogicalCallContext,call);
    }catch(Exception error){return new ReturnMessage(error,call);}
   }
  }

  /// <summary>
  /// 保留原生依赖解析器，只为已确认的原生内部编译器提供其公开构造依赖。
  /// 与现有 BackgroundBuild 同样处理缺失的接口映射，不更改全局 Unity 注册或客户端配置。
  /// </summary>
  sealed class TaskPreviewSimulationResolver:RealProxy {
   readonly object original;
   internal TaskPreviewSimulationResolver(Type contract,object resolver):base(contract){original=resolver;}
   public override IMessage Invoke(IMessage message) {
    var call=(IMethodCallMessage)message;
    try {
     var method=(MethodInfo)call.MethodBase;
     Type requested=method.IsGenericMethod?method.GetGenericArguments()[0]:call.Args.OfType<Type>().FirstOrDefault();
     object value;
     if(call.MethodName=="Resolve"&&requested!=null&&call.ArgCount==0&&requested.FullName=="HB52bCSB8nBAlI1TVs.o9MkFLvKmwij5tJtId") {
      // 此类型和八参数构造已由现有、编译成功的 BackgroundBuild 通路确认，不接受任意类型构造。
      var constructor=requested.GetConstructors(BindingFlags.Public|BindingFlags.Instance).Single(c=>c.GetParameters().Length==8);
      value=constructor.Invoke(constructor.GetParameters().Select(p=>TaskPreviewSimulationDependency(p.ParameterType)).ToArray());
     }else if(call.MethodName=="Resolve"&&requested!=null&&call.ArgCount==0&&requested.FullName=="Flexem.Studio.Build.IBuildMessageService") {
      value=TaskPreviewSimulationDependency(requested);
     }else value=method.Invoke(original,call.Args);
     return new ReturnMessage(value,call.Args,call.ArgCount,call.LogicalCallContext,call);
    }catch(Exception error){return new ReturnMessage(error is TargetInvocationException&&error.InnerException!=null?error.InnerException:error,call);}
   }
  }

  /// <summary>
  /// 优先取得原生已注册依赖。缺少接口映射时，仅使用本机已确认公开构造的官方实现；
  /// 日志接口由宿主记录器承接，模拟逻辑仍由真正的厂商操作实例执行。
  /// </summary>
  static object TaskPreviewSimulationDependency(Type contract) {
   if(contract.FullName=="Flexem.Infrastructure.IDependencyResolver")return taskPreviewSimulationResolver;
   if(contract.FullName=="Flexem.Studio.Build.IBuildMessageService")return new TaskPreviewSimulationMessages(contract).GetTransparentProxy();
   try{return Resolve(contract.FullName);}catch(TargetInvocationException){}
   string implementation;
   switch(contract.FullName) {
    case "Flexem.Studio.Workbench.IAsynchronousWaitDialog":implementation="Flexem.Studio.Progress.AsynchronousWaitDialogImpl";break;
    case "Flexem.Services.IMessageService":implementation="Flexem.Studio.Core.Implementation.Services.MessageService";break;
    case "Flexem.Services.IStringParser":implementation="Flexem.Studio.Core.Implementation.Services.StringParserImpl";break;
    case "Caliburn.Micro.IWindowManager":implementation="Caliburn.Micro.WindowManager";break;
    case "Flexem.Studio.Hmi.Options.IBuildOptions":implementation="Flexem.Studio.Options.DefaultOptions";break;
    case "Flexem.Services.IFileSystem":
     var fileSystem=Native("Flexem.Services.FileSystem");
     var parser=Native("Flexem.Services.IStringParser");
     var fileConstructor=fileSystem.GetConstructor(new[]{parser});
     if(fileConstructor==null)throw new NotSupportedException("The installed FileSystem constructor changed");
     return fileConstructor.Invoke(new[]{TaskPreviewSimulationDependency(parser)});
    default:throw new NotSupportedException("No verified native simulation dependency mapping for "+contract.FullName);
   }
   var type=Native(implementation);
   if(!contract.IsAssignableFrom(type)||type.GetConstructor(Type.EmptyTypes)==null)
    throw new NotSupportedException("The verified native simulation dependency constructor changed: "+implementation);
   return Activator.CreateInstance(type);
  }

  /// <summary>
  /// 本版本未注册 ISimulateOperation 时，按已实测的八参数签名创建唯一官方实现并缓存。
  /// 类型按接口及完整构造签名定位，不依赖混淆类名，也不执行 OffLineSimulateCommand。
  /// </summary>
  static object TaskPreviewGetSimulationOperation(Type contract) {
   if(taskPreviewSimulationOperation!=null)return taskPreviewSimulationOperation;
   try{taskPreviewSimulationOperation=Resolve(contract.FullName);return taskPreviewSimulationOperation;}catch(TargetInvocationException){}
   string[] dependencies={"Flexem.Studio.Build.IBuildMessageService","Flexem.Studio.Workbench.IAsynchronousWaitDialog",
    "Flexem.Services.IStringParser","Flexem.Services.IMessageService","Caliburn.Micro.IWindowManager",
    "Flexem.Services.IFileSystem","Flexem.Studio.Hmi.Options.IBuildOptions","Flexem.Infrastructure.IDependencyResolver"};
   var types=dependencies.Select(Native).ToArray();
   var implementations=contract.Assembly.GetTypes().Where(t=>!t.IsInterface&&!t.IsAbstract&&contract.IsAssignableFrom(t)&&t.GetConstructor(types)!=null).ToArray();
   if(implementations.Length!=1)throw new NotSupportedException("The installed native simulation implementation/signature is not unique");
   var resolverContract=types[7];var originalResolver=Resolve(resolverContract.FullName);
   taskPreviewSimulationResolver=new TaskPreviewSimulationResolver(resolverContract,originalResolver).GetTransparentProxy();
   taskPreviewSimulationOperation=implementations[0].GetConstructor(types).Invoke(types.Select(TaskPreviewSimulationDependency).ToArray());
   return taskPreviewSimulationOperation;
  }

  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
  struct TaskPreviewProcessEntry {
   public uint Size,Usage,ProcessId;
   public IntPtr DefaultHeapId;
   public uint ModuleId,Threads,ParentProcessId;
   public int PriorityClassBase;
   public uint Flags;
   [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string ExecutableName;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct TaskPreviewRect { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)]
  struct TaskPreviewPoint { public int X, Y; }
  delegate bool TaskPreviewEnumWindowCallback(IntPtr window, IntPtr parameter);
  [DllImport("user32.dll")] static extern bool EnumWindows(TaskPreviewEnumWindowCallback callback, IntPtr parameter);
  [DllImport("user32.dll",EntryPoint="PostMessageW",SetLastError=true)] static extern bool TaskPreviewPostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
  [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint processId);
  [DllImport("kernel32.dll",EntryPoint="Process32FirstW",CharSet=CharSet.Unicode)] static extern bool TaskPreviewProcess32First(IntPtr snapshot,ref TaskPreviewProcessEntry entry);
  [DllImport("kernel32.dll",EntryPoint="Process32NextW",CharSet=CharSet.Unicode)] static extern bool TaskPreviewProcess32Next(IntPtr snapshot,ref TaskPreviewProcessEntry entry);
  [DllImport("kernel32.dll",EntryPoint="CloseHandle")] static extern bool TaskPreviewCloseHandle(IntPtr handle);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out TaskPreviewRect rectangle);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out TaskPreviewRect rectangle);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref TaskPreviewPoint point);
  [DllImport("user32.dll",SetLastError=true)] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
  [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
  [DllImport("user32.dll", SetLastError=true)] static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr item);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr deviceContext);

  /// <summary>
  /// 临时使用目标窗口自己的 DPI 上下文读取客户区与渲染边界，防止 WPF 宿主在
  /// 150% 等系统缩放下取得放大的边界，而旧版 FSGUI 实际只绘制与接受逻辑坐标。
  /// 作用域结束立即恢复宿主线程原有上下文，不改变应用或系统的全局 DPI 设置。
  /// </summary>
  sealed class TaskPreviewWindowDpiScope:IDisposable {
   IntPtr previous;
   internal TaskPreviewWindowDpiScope(IntPtr window) {
    IntPtr target=GetWindowDpiAwarenessContext(window);
    if(target==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot read simulator window DPI context");
    previous=SetThreadDpiAwarenessContext(target);
    if(previous==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot enter simulator window DPI context");
   }
   public void Dispose() {
    if(previous==IntPtr.Zero)return;
    IntPtr restore=previous;previous=IntPtr.Zero;
    if(SetThreadDpiAwarenessContext(restore)==IntPtr.Zero)
     throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot restore native host thread DPI context");
   }
  }

  /// <summary>只允许将 PNG 保存到当前 MCP 工作区；拒绝扩展名错误与无效目标。</summary>
  static string TaskPreviewPngPath(Dictionary<string,object> arguments) {
   string file=Path.GetFullPath(S(arguments,"file"));
   Within(file);
   if(!String.Equals(Path.GetExtension(file),".png",StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Preview destination must be a PNG file");
   string directory=Path.GetDirectoryName(file);
   if(String.IsNullOrEmpty(directory))throw new ArgumentException("Preview directory is missing");
   // 加上目录分隔符，使工作区根目录本身也能通过宿主的目录边界检查。
   Within(directory+Path.DirectorySeparatorChar);Directory.CreateDirectory(directory);
   return file;
  }

  /// <summary>限制位图尺寸，避免无布局或异常窗口造成零尺寸输出与过量内存分配。</summary>
  static void TaskPreviewRequireSize(int width,int height) {
   if(width<=0||height<=0||width>16384||height>16384||(long)width*height>64000000)
    throw new InvalidOperationException("Native preview dimensions are invalid or exceed the bitmap limit");
  }

  /// <summary>使用 WPF 原生 PNG 编码器保存实际渲染结果，不生成示意图或替代控件。</summary>
  static void TaskPreviewSavePng(BitmapSource bitmap,string file) {
   var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
   using(var stream=new FileStream(file,FileMode.Create,FileAccess.Write,FileShare.Read))encoder.Save(stream);
  }

  /// <summary>记录现有模拟进程，以便后续截图只关联本次离线启动后出现的 FSGUI。</summary>
  static HashSet<int> TaskPreviewSimulatorIds() {
   var ids=new HashSet<int>();
   foreach(var process in Process.GetProcessesByName("FSGUI"))
    using(process){try{ids.Add(process.Id);}catch(InvalidOperationException){}}
   return ids;
  }

  /// <summary>
  /// 在任何新离线请求之前拒绝既有官方模拟进程，不接管或关闭用户已有的预览。
  /// 原生操作的 IsSimulating 不一定知道编译归档入口启动的实例，因此仍需独立占用检查。
  /// </summary>
  static void TaskPreviewRejectExistingSimulators() {
   foreach(string name in new[]{"FCS","FSGUI"})foreach(var process in Process.GetProcessesByName(name))using(process)
    if(!process.HasExited)throw new InvalidOperationException("An existing "+name+" process must be closed before starting another offline preview");
  }

  /// <summary>
  /// 通用原生预览入口：编辑器渲染与离线模拟分别返回来源，不依赖公共页或控件命名。
  /// 仅打开官方离线模拟窗口、捕获其画面及请求正常关闭，不派发任意命令或坐标输入。
  /// </summary>
  static object NativeTaskPreview(Dictionary<string,object> a) {
   string op=S(a,"op");
   if(op=="task_render_canvas") {
    RequireProject();var designer=DesignerView();
    var context=Member(designer,"DesignContext");var root=Member(context,"RootItem");
    var canvas=Member(root,"View") as FrameworkElement;
    if(canvas==null)throw new InvalidOperationException("The active native canvas has no FrameworkElement view");
    // 此函数由宿主 UI 调度器执行；只更新既有布局，不改变画布尺寸、模型或控件状态。
    canvas.UpdateLayout();
    int width=(int)Math.Ceiling(canvas.RenderSize.Width),height=(int)Math.Ceiling(canvas.RenderSize.Height);
    TaskPreviewRequireSize(width,height);string file=TaskPreviewPngPath(a);
    var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
    bitmap.Render(canvas);bitmap.Freeze();TaskPreviewSavePng(bitmap,file);
    var model=Member(designer,"Model");
    return Obj("file",file,"width",width,"height",height,"source","native_editor_canvas",
     "runtime",false,"page_uuid",Convert.ToString(Member(model,"UniqueId")),
     "page_name",Convert.ToString(Member(model,"Name")),"render_method","RenderTargetBitmap",
     "canvas_object",canvas.GetType().FullName);
   }
   if(op=="task_simulate_offline") {
    RequireProject();TaskPreviewRejectExistingSimulators();ProjectService("SaveCurrentProject");
    var contract=Native("Flexem.Studio.Build.Operations.ISimulateOperation");
    var operation=TaskPreviewGetSimulationOperation(contract);
    var isSimulating=contract.GetProperty("IsSimulating",BindingFlags.Public|BindingFlags.Instance);
    if(isSimulating!=null&&(bool)isSimulating.GetValue(operation,null))
     throw new InvalidOperationException("The native simulator is already running; no duplicate offline request was started");
    var isOnline=contract.GetProperty("IsOnLine",BindingFlags.Public|BindingFlags.Instance);
    if(isOnline==null||isOnline.GetSetMethod()==null)throw new InvalidOperationException("Native offline simulation mode is unavailable");
    // 通过已确认的服务接口显式设为离线；不使用命令执行入口，也不沿用在线模拟设置。
    isOnline.SetValue(operation,false,null);
    var fileType=Native("Flexem.FileName");var factory=fileType.GetMethod("Create",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(string)},null);
    if(factory==null)throw new InvalidOperationException("Native FileName.Create(string) is unavailable");
    string project=ProjectFile();var nativeFile=factory.Invoke(null,new object[]{project});
    var simulate=contract.GetMethod("Simulate",new[]{fileType});
    if(simulate==null)throw new InvalidOperationException("Native Simulate(FileName) is unavailable");
    taskPreviewExistingSimulatorIds=TaskPreviewSimulatorIds();taskPreviewSimulatorRequestedUtc=DateTime.UtcNow;
    taskPreviewSimulatorProject=project;taskPreviewCapturedSimulatorId=null;taskPreviewCapturedSimulatorStartUtc=null;
    // 离线运行只记录当前工程及进程归属；任何公共页、导航布局或控件名称都不作为启动条件。
    try{simulate.Invoke(operation,new[]{nativeFile});}
    catch{taskPreviewExistingSimulatorIds=null;taskPreviewSimulatorProject=null;throw;}
    // 服务返回只证明启动请求已提交；实际模拟窗口与画面要由独立截图调用核实。
    return Obj("status","offline_start_requested","project",project,"online",false,
     "requested_utc",taskPreviewSimulatorRequestedUtc.ToString("o"),"runtime_verified",false,
     "native_service",contract.FullName,"native_instance_type",operation.GetType().FullName,"native_method","Simulate(FileName)");
   }
   if(op=="task_simulator_capture")return TaskPreviewCaptureSimulator(a);
   if(op=="task_simulator_close")return TaskPreviewCloseSimulator();
   throw new ArgumentException("Unsupported task preview operation: "+op);
  }

  /// <summary>
  /// 读取进程父子关系，不读取命令行或其他用户数据。用于证明模拟器由当前宿主启动。
  /// </summary>
  static Dictionary<int,int> TaskPreviewProcessParents() {
   var parents=new Dictionary<int,int>();IntPtr snapshot=CreateToolhelp32Snapshot(2,0);
   if(snapshot==IntPtr.Zero||snapshot==new IntPtr(-1))throw new InvalidOperationException("Cannot verify simulator process ownership");
   try {
    var entry=new TaskPreviewProcessEntry();entry.Size=(uint)Marshal.SizeOf(typeof(TaskPreviewProcessEntry));
    if(TaskPreviewProcess32First(snapshot,ref entry))do{parents[(int)entry.ProcessId]=(int)entry.ParentProcessId;}while(TaskPreviewProcess32Next(snapshot,ref entry));
   }finally{TaskPreviewCloseHandle(snapshot);}
   return parents;
  }

  /// <summary>仅认可当前宿主的子进程或仍可验证的后代；缺失或循环的父链不能作为归属证据。</summary>
  static bool TaskPreviewIsOwnedDescendant(int processId,Dictionary<int,int> parents) {
   int host=Process.GetCurrentProcess().Id,current=processId;var visited=new HashSet<int>();
   for(int depth=0;depth<64;depth++) {
    int parent;if(!visited.Add(current)||!parents.TryGetValue(current,out parent))return false;
    if(parent==host)return true;if(parent<=0||parent==current)return false;current=parent;
   }
   return false;
  }

  /// <summary>
  /// 在已有父链归属检查之外，核对官方执行路径和本工程的离线参数。通过 .NET Framework
  /// 自带 WMI 只读取候选进程的 ExecutablePath/CommandLine，不执行脚本或外部命令。
  /// 使用反射加载框架程序集，使现有宿主编译引用保持不变；任何缺失或不匹配均拒绝输入。
  /// </summary>
  static bool TaskPreviewSimulatorProjectMatches(int processId) {
   RequireProject();
   if(String.IsNullOrEmpty(taskPreviewSimulatorProject)||
      !String.Equals(Path.GetFullPath(ProjectFile()),Path.GetFullPath(taskPreviewSimulatorProject),StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The active project differs from the host-owned offline request");
   string expectedData=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(taskPreviewSimulatorProject),"Bin","fshmi.bin"));Within(expectedData);
   string official=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName),"FSGUI.exe"));
   var management=Assembly.Load("System.Management, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
   var searcherType=management.GetType("System.Management.ManagementObjectSearcher",true);
   object searcher=null,results=null;
   try {
    searcher=Activator.CreateInstance(searcherType,new object[]{"SELECT ExecutablePath,CommandLine FROM Win32_Process WHERE ProcessId="+processId});
    results=searcherType.GetMethod("Get",Type.EmptyTypes).Invoke(searcher,null);
    var rows=((IEnumerable)results).Cast<object>().ToArray();if(rows.Length!=1)return false;
    var row=rows[0];var property=row.GetType().GetMethod("GetPropertyValue",new[]{typeof(string)});
    string executable=Convert.ToString(property.Invoke(row,new object[]{"ExecutablePath"}));
    string commandLine=Convert.ToString(property.Invoke(row,new object[]{"CommandLine"}));
    if(String.IsNullOrWhiteSpace(executable)||!String.Equals(Path.GetFullPath(executable),official,StringComparison.OrdinalIgnoreCase))return false;
    var data=System.Text.RegularExpressions.Regex.Matches(commandLine,"<UserPrjDat>([^<>]+)</UserPrjDat>");
    var mode=System.Text.RegularExpressions.Regex.Matches(commandLine,"<RunMode>([^<>]+)</RunMode>");
    if(data.Count!=1||mode.Count!=1||mode[0].Groups[1].Value!="SimPlc")return false;
    string actualData=Path.GetFullPath(System.Net.WebUtility.HtmlDecode(data[0].Groups[1].Value));
    return String.Equals(actualData,expectedData,StringComparison.OrdinalIgnoreCase);
   } finally {
    var disposableResults=results as IDisposable;if(disposableResults!=null)disposableResults.Dispose();
    var disposableSearcher=searcher as IDisposable;if(disposableSearcher!=null)disposableSearcher.Dispose();
   }
  }

  /// <summary>
  /// 找到本次离线请求所属的唯一模拟器窗口。启动前已有进程、用户另外启动的进程和
  /// PID 复用均被排除；首次确认后固定 PID 和创建时间，调用者不能指定其他目标。
  /// </summary>
  static bool TaskPreviewOwnedSimulatorWindow(out int processId,out IntPtr window) {
   if(taskPreviewExistingSimulatorIds==null)throw new InvalidOperationException("No offline simulator request belongs to this host session");
   processId=0;window=IntPtr.Zero;var candidates=new List<int>();var startTimes=new Dictionary<int,DateTime>();
   var parents=TaskPreviewProcessParents();
   foreach(var process in Process.GetProcessesByName("FSGUI"))using(process) {
    try{
     int id=process.Id;
     if(taskPreviewExistingSimulatorIds.Contains(id)||process.HasExited)continue;
     // Windows 的进程创建时间可能只有较粗的精度，允许一秒误差；启动前已有的 PID 始终排除。
     DateTime started=process.StartTime.ToUniversalTime();
     if(started<taskPreviewSimulatorRequestedUtc.AddSeconds(-1))continue;
     if(!TaskPreviewIsOwnedDescendant(id,parents))continue;
     if(taskPreviewCapturedSimulatorId.HasValue&&taskPreviewCapturedSimulatorId.Value!=id)continue;
     if(taskPreviewCapturedSimulatorStartUtc.HasValue&&taskPreviewCapturedSimulatorStartUtc.Value!=started)continue;
     if(!TaskPreviewSimulatorProjectMatches(id))continue;
     candidates.Add(id);startTimes[id]=started;
    }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
   }
   if(candidates.Count==0)return false;
   if(candidates.Count!=1)throw new InvalidOperationException("Multiple new host-owned simulator processes; no window was selected");
   int foundProcessId=candidates[0];IntPtr foundWindow=IntPtr.Zero;long greatestArea=0;
   EnumWindows(delegate(IntPtr handle,IntPtr parameter) {
    uint owner;GetWindowThreadProcessId(handle,out owner);
    if(owner!=(uint)foundProcessId||!IsWindowVisible(handle)||IsIconic(handle))return true;
    TaskPreviewRect bounds;
    if(GetClientRect(handle,out bounds)) {
     long area=(long)(bounds.Right-bounds.Left)*(bounds.Bottom-bounds.Top);
     if(area>greatestArea){greatestArea=area;foundWindow=handle;}
    }
    return true;
   },IntPtr.Zero);
   if(foundWindow==IntPtr.Zero)return false;
   taskPreviewCapturedSimulatorId=foundProcessId;taskPreviewCapturedSimulatorStartUtc=startTimes[foundProcessId];
   processId=foundProcessId;window=foundWindow;return true;
  }

  /// <summary>捕获已验证归属的模拟器客户区；不修改运行状态或项目内容。</summary>
  static object TaskPreviewCaptureSimulator(Dictionary<string,object> a) {
   int processId;IntPtr window;
   if(!TaskPreviewOwnedSimulatorWindow(out processId,out window))return Obj("status","simulator_window_pending","source","native_offline_simulator","runtime_verified",false,"project",taskPreviewSimulatorProject);
   using(new TaskPreviewWindowDpiScope(window)) {
   TaskPreviewRect client,outer;TaskPreviewPoint origin=new TaskPreviewPoint();
   if(!GetClientRect(window,out client)||!GetWindowRect(window,out outer)||!ClientToScreen(window,ref origin))
    throw new InvalidOperationException("Cannot read native simulator window bounds");
   int width=client.Right-client.Left,height=client.Bottom-client.Top;TaskPreviewRequireSize(width,height);
   string file=TaskPreviewPngPath(a);IntPtr windowDc=IntPtr.Zero,memoryDc=IntPtr.Zero,bitmapHandle=IntPtr.Zero,oldBitmap=IntPtr.Zero;
   try {
    windowDc=GetDC(window);if(windowDc==IntPtr.Zero)throw new InvalidOperationException("Cannot acquire simulator client device context");
    memoryDc=CreateCompatibleDC(windowDc);if(memoryDc==IntPtr.Zero)throw new InvalidOperationException("Cannot create screenshot device context");
    bitmapHandle=CreateCompatibleBitmap(windowDc,width,height);if(bitmapHandle==IntPtr.Zero)throw new InvalidOperationException("Cannot create simulator screenshot bitmap");
    oldBitmap=SelectObject(memoryDc,bitmapHandle);
    // PW_CLIENTONLY 保留真实运行客户区，不包含桌面其他应用，也不依赖前台屏幕像素。
    if(!PrintWindow(window,memoryDc,1))throw new InvalidOperationException("Native simulator PrintWindow failed");
    var source=Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
    source.Freeze();TaskPreviewSavePng(source,file);taskPreviewCapturedSimulatorId=processId;
   } finally {
    if(memoryDc!=IntPtr.Zero&&oldBitmap!=IntPtr.Zero)SelectObject(memoryDc,oldBitmap);
    if(bitmapHandle!=IntPtr.Zero)DeleteObject(bitmapHandle);
    if(memoryDc!=IntPtr.Zero)DeleteDC(memoryDc);
    if(windowDc!=IntPtr.Zero)ReleaseDC(window,windowDc);
   }
   return Obj("status","captured","file",file,"width",width,"height",height,
    "source","native_offline_simulator_client","runtime",true,"runtime_window_verified",true,
    "project_content_verified",false,"online",false,
    "process_id",processId,"window_handle",window.ToInt64(),"project",taskPreviewSimulatorProject,
    "capture_method","PrintWindow/PW_CLIENTONLY",
    "dpi_context_applied",true,"coordinate_space","simulator_window_logical",
    "client_bounds",Obj("x",0,"y",0,"width",width,"height",height),
    "client_screen_bounds",Obj("x",origin.X,"y",origin.Y,"width",width,"height",height),
    "window_screen_bounds",Obj("x",outer.Left,"y",outer.Top,"width",outer.Right-outer.Left,"height",outer.Bottom-outer.Top));
   }
  }

  /// <summary>只向本次离线请求拥有的模拟器主窗口发送 WM_CLOSE；不强杀进程、不触碰其他模拟器。</summary>
  static object TaskPreviewCloseSimulator() {
   int processId;IntPtr window;
   if(!TaskPreviewOwnedSimulatorWindow(out processId,out window)) {
    return Obj("status","no_owned_simulator_window","close_requested",false,"project",taskPreviewSimulatorProject);
   }
   if(!TaskPreviewPostMessage(window,0x0010,IntPtr.Zero,IntPtr.Zero))throw new InvalidOperationException("Cannot request close of the host-owned simulator window");
   return Obj("status","close_requested","close_requested",true,"process_id",processId,"window_handle",window.ToInt64(),
    "project",taskPreviewSimulatorProject,"process_exit_verified",false,"forced_termination",false);
  }
 }
}
