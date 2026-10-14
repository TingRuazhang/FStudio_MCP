using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;

namespace FStudioMcp {
 internal static partial class VisibleHost {
  // 保存本宿主真正创建的 Process 句柄，避免通过外部 PID 误关闭其他进程或遭遇 PID 复用。
  static Process taskCompiledPreviewFcsProcess;
  // GUI 单独保留真实进程句柄；原生模拟入口会更新共享截图字段，不能据此判断本次 GUI 是否仍存活。
  static Process taskCompiledPreviewGuiProcess;
  static string taskCompiledPreviewFcsProject;

  /// <summary>
  /// 仅向本方法调用方持有的实际启动进程发送 WM_CLOSE，兼容后台隐藏窗口。
  /// Process 的真实句柄与 HasExited 保证不把已复用的 PID 当作本次进程；不接受外部 PID，不强制终止。
  /// </summary>
  static bool TaskCompiledRequestOwnedClose(Process process){
   if(process==null||process.HasExited)return false;
   int id=process.Id;bool requested=false;
   EnumWindows(delegate(IntPtr window,IntPtr parameter){
    uint owner;GetWindowThreadProcessId(window,out owner);
    if(owner==(uint)id&&!process.HasExited)requested|=TaskPreviewPostMessage(window,0x0010,IntPtr.Zero,IntPtr.Zero);
    return true;
   },IntPtr.Zero);
   return requested;
  }

  /// <summary>没有存活 GUI 时撤销未完成的截图请求；不释放仍需正常关闭的后台进程句柄。</summary>
  static void TaskCompiledClearFailedRequest(){
   taskPreviewExistingSimulatorIds=null;taskPreviewSimulatorProject=null;
   taskPreviewCapturedSimulatorId=null;taskPreviewCapturedSimulatorStartUtc=null;
  }

  /// <summary>按已经核实的原生接口读取属性，兼容厂商具体类型显式实现接口的情况。</summary>
  static object TaskCompiledContractProperty(object target,string contractName,string propertyName){
   var contract=Native(contractName);
   var property=contract.GetProperty(propertyName)??contract.GetInterfaces().Select(t=>t.GetProperty(propertyName)).FirstOrDefault(p=>p!=null);
   if(property==null||property.GetGetMethod()==null)throw new InvalidOperationException("Native configuration property is unavailable: "+propertyName);
   return property.GetValue(target,null);
  }

  /// <summary>生成官方连续 XML 参数；转义路径与语言文本，拒绝以参数注入附加标签。</summary>
  static string TaskCompiledXml(string tag,object value){
   return "<"+tag+">"+SecurityElement.Escape(Convert.ToString(value,CultureInfo.InvariantCulture))+"</"+tag+">";
  }

  /// <summary>在服务端复制编译输出前先只读检查工程和模拟器占用，避免覆盖正在使用的Bin。</summary>
  static object TaskCompiledPreviewPreflight(){
   RequireProject();
   if(DirtyViewCount()!=0)throw new InvalidOperationException("Save all project views before launching verified compiled outputs");
   TaskPreviewRejectExistingSimulators();
   return Obj("project",ProjectFile(),"simulators_running",false,"dirty_views",0,"hardware_io",false);
  }

  /// <summary>
  /// 从当前工程已验证的编译 Bin 启动官方离线 FCS/FSGUI，保留 temp 内全部记录。
  /// 服务端必须先核实 build_id 成功、source_unchanged 和工程完全匹配，再复制完整编译输出。
  /// 本方法不编译、不调用 Simulate/ClearRecord、不删除记录，也不允许传入程序路径、参数或工程路径。
  /// </summary>
  static object TaskPreviewLaunchCompiledOffline(){
   RequireProject();string project=ProjectFile(),directory=Path.GetDirectoryName(project);
   if(DirtyViewCount()!=0)throw new InvalidOperationException("Save all project views before validating and launching compiled outputs");
   string bin=Path.Combine(directory,"Bin"),dataFile=Path.Combine(bin,"fshmi.bin");Within(dataFile);
   if(!File.Exists(dataFile))throw new InvalidOperationException("The verified compiled fshmi.bin is missing");
   // FCS 与 FSGUI 采用官方全局运行环境；有其他实例时拒绝，不接管、关闭或终止既有进程。
   TaskPreviewRejectExistingSimulators();

   string appBin=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
   string fcsFile=Path.Combine(appBin,"FCS.exe"),guiFile=Path.Combine(appBin,"FSGUI.exe");
   if(!File.Exists(fcsFile)||!File.Exists(guiFile))throw new InvalidOperationException("The installed official FCS/FSGUI executable pair is unavailable");
   string installationRoot=Path.GetDirectoryName(appBin.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
   // 与官方读取链一致：按当前工程 Connection 文件加载设备模型，而不是使用另一个工程的全局副本。
   var nativeDirectory=Activator.CreateInstance(Native("Flexem.DirectoryName"),new object[]{directory});
   var connection=PageStatic("Flexem.Studio.Hmi.HmiPathManager","GetConnectionFileName",nativeDirectory);
   var model=PageStatic("Flexem.Studio.Hmi.Models.Device.HMIProjectModel","Load",connection);
   var services=Native("Flexem.Studio.Hmi.Services.HmiServices");
   var deviceService=services.GetProperty("DeviceConfigurationService",BindingFlags.Public|BindingFlags.Static).GetValue(null,null);
   const string deviceContract="Flexem.Studio.Hmi.Device.Configuration.IDeviceConfigurationService";
   const string providerContract="Flexem.Studio.Hmi.Device.Configuration.IHmiConfigurationProvider";
   const string hmiContract="Flexem.Studio.Hmi.Device.Configuration.IHmiConfiguration";
   var provider=TaskCompiledContractProperty(deviceService,deviceContract,"HmiProvider");
   var getConfig=Native(providerContract).GetMethods().Single(m=>m.Name=="GetConfigByUniqueKey"&&m.GetParameters().Length==2);
   var configuration=getConfig.Invoke(provider,new[]{Member(model,"UniqueKey"),Member(model,"ID")});
   if(configuration==null)throw new InvalidOperationException("The current compiled preview device configuration is unavailable");
   int width=Convert.ToInt32(TaskCompiledContractProperty(configuration,hmiContract,"PixelWidth"));
   int height=Convert.ToInt32(TaskCompiledContractProperty(configuration,hmiContract,"PixelHeight"));
   int color=Convert.ToInt32(Member(model,"AdjustedColor"));
   int usbCount=Convert.ToInt32(TaskCompiledContractProperty(configuration,hmiContract,"USBHostCount"));
   int ram=Convert.ToInt32(TaskCompiledContractProperty(configuration,hmiContract,"SimulateRam"));
   if(width<=0||height<=0||(color!=16&&color!=24)||usbCount<0||ram<=0)throw new InvalidOperationException("Native compiled preview configuration is invalid");
   var cultureOptions=Resolve("Flexem.Studio.Options.ICultureOptions");
   var language=TaskCompiledContractProperty(cultureOptions,"Flexem.Studio.Options.ICultureOptions","CurrentLanguage");
   string culture=Convert.ToString(Member(language,"Code"));

   string hmi=Path.Combine(directory,"temp","disk","hmi"),sd=Path.Combine(directory,"temp","disk","sd");
   string memory=Path.Combine(directory,"temp","ramdisk"),recipe=Path.Combine(hmi,"rcp"),device=Path.Combine(bin,"Device");
   var writableDirectories=new List<string>{hmi,sd,memory,recipe};string usb="";
   for(int index=1;index<=usbCount;index++){
    string path=Path.Combine(directory,"temp","disk","usb"+index);writableDirectories.Add(path);usb+=TaskCompiledXml("Usb"+index+"Dir",path);
   }
   // 仅补齐验证工程内的目录；Directory.CreateDirectory 保留已有文件，不运行官方清理流程。
   foreach(string path in writableDirectories){Within(path);Directory.CreateDirectory(path);}
   string fcsArguments=TaskCompiledXml("PlcDrvDir",device)+TaskCompiledXml("RunMode","SimPlc")+
    TaskCompiledXml("RcpDir",recipe)+TaskCompiledXml("MemFs",memory)+TaskCompiledXml("Debug","N");
   string guiArguments=TaskCompiledXml("UserPrjDat",dataFile)+TaskCompiledXml("PicResDir",Path.Combine(bin,"Images"))+
    TaskCompiledXml("TtfResDir",Path.Combine(bin,"Fonts"))+"<SysFrmBuf>"+TaskCompiledXml("XPixs",width)+TaskCompiledXml("YPixs",height)+TaskCompiledXml("ColorBits",color)+"</SysFrmBuf>"+
    TaskCompiledXml("HmiDir",hmi)+TaskCompiledXml("SdDir",sd)+usb+TaskCompiledXml("MemFs",memory)+
    TaskCompiledXml("MacroDir",Path.Combine(bin,"Macro"))+TaskCompiledXml("RcpDir",recipe)+TaskCompiledXml("Debug","N")+
    TaskCompiledXml("SysFontDir",Path.Combine(installationRoot,"Lib","Fonts"))+TaskCompiledXml("UICulture",culture)+TaskCompiledXml("RunMode","SimPlc")+
    TaskCompiledXml("PlcDrvDir",device)+TaskCompiledXml("AudResDir",Path.Combine(bin,"Audio"))+TaskCompiledXml("SimRamSize",ram)+
    TaskCompiledXml("SimExitTime",14400)+TaskCompiledXml("RcpInitDir",Path.Combine(bin,"Files")+Path.DirectorySeparatorChar);

   // 仅登记当前工程和实际启动归属；编译结果复用与截图不要求项目具有任何导航或公共页。
   // 请求提交后仍需实际模拟画面验收，不能把启动返回当成运行成功。
   taskPreviewExistingSimulatorIds=TaskPreviewSimulatorIds();taskPreviewSimulatorRequestedUtc=DateTime.UtcNow;
   taskPreviewSimulatorProject=project;taskPreviewCapturedSimulatorId=null;taskPreviewCapturedSimulatorStartUtc=null;
   // FCS 是后台辅助服务，隐藏其窗口以免后台辅助服务干扰编辑器；FSGUI 保持正常可见，供用户直接查看模拟画面。
   Process fcs=null,gui=null;
   try{
    fcs=Process.Start(new ProcessStartInfo(fcsFile,fcsArguments){UseShellExecute=false,WorkingDirectory=appBin,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
    if(fcs==null)throw new InvalidOperationException("The installed FCS process did not start");
    if(taskCompiledPreviewFcsProcess!=null)taskCompiledPreviewFcsProcess.Dispose();
    taskCompiledPreviewFcsProcess=fcs;taskCompiledPreviewFcsProject=project;
    gui=Process.Start(new ProcessStartInfo(guiFile,guiArguments){UseShellExecute=false,WorkingDirectory=appBin});
    if(gui==null)throw new InvalidOperationException("The installed FSGUI process did not start");
    if(taskCompiledPreviewGuiProcess!=null)taskCompiledPreviewGuiProcess.Dispose();
    taskCompiledPreviewGuiProcess=gui;
    // 先完整读取身份再发布，避免 StartTime 抛异常时产生半个成功身份。
    int guiId=gui.Id;DateTime guiStarted=gui.StartTime.ToUniversalTime();
    taskPreviewCapturedSimulatorId=guiId;taskPreviewCapturedSimulatorStartUtc=guiStarted;
   }catch{
    // 正常关闭请求不等于退出。GUI 仍存活时保留实际句柄与请求归属，供后续关闭工具使用；
    // 不提前关闭其 FCS。隐藏 FCS 没有 MainWindowHandle 时也通过同一 WM_CLOSE 路径处理。
    bool guiAlive=gui!=null&&!gui.HasExited;
    try{if(guiAlive)TaskCompiledRequestOwnedClose(gui);}catch{}
    if(gui==null||gui.HasExited){
     try{TaskCompiledRequestOwnedClose(fcs);}catch{}
     TaskCompiledClearFailedRequest();
    }else{
     // 此 ID 来自实际 Process.Start 返回对象，时间留空时截图入口仍核对父链、工程参数和请求时刻。
     taskPreviewCapturedSimulatorId=gui.Id;taskPreviewCapturedSimulatorStartUtc=null;
    }
    throw;
   }
   return Obj("status","compiled_offline_start_requested","project",project,"online",false,"debug","N",
    "fcs_process_id",fcs.Id,"process_id",taskPreviewCapturedSimulatorId,"data_file",dataFile,"working_directory",appBin,
    "records_cleared",false,"build_triggered",false,"runtime_verified",false,
    "requested_utc",taskPreviewSimulatorRequestedUtc.ToString("o"));
  }

  /// <summary>
  /// 只请求关闭本宿主实际创建且仍持有的FCS辅助进程，先确认GUI已退出。
  /// 隐藏辅助窗口不能依赖MainWindowHandle；按拥有进程的句柄及PID查找其窗口，
  /// 仅发送WM_CLOSE。没有任意PID输入，也不使用强制终止或按进程名批量关闭。
  /// </summary>
  static object TaskPreviewCloseCompiledHelper(){
   if(taskCompiledPreviewFcsProcess==null)return Obj("status","no_owned_helper","close_requested",false);
   var process=taskCompiledPreviewFcsProcess;
   // 共享 captured 字段可能被其他原生模拟请求更新；只根据本次实际 GUI 句柄判断，不依赖外部 PID 查找。
   if(taskCompiledPreviewGuiProcess!=null&&!taskCompiledPreviewGuiProcess.HasExited)
    throw new InvalidOperationException("Close the owned simulator before closing its helper");
   if(taskCompiledPreviewGuiProcess!=null){taskCompiledPreviewGuiProcess.Dispose();taskCompiledPreviewGuiProcess=null;}
   if(process.HasExited){process.Dispose();taskCompiledPreviewFcsProcess=null;return Obj("status","helper_exited","exit_verified",true);}
   int id=process.Id;bool requested=TaskCompiledRequestOwnedClose(process);
   return Obj("status",requested?"helper_close_requested":"owned_helper_has_no_window","close_requested",requested,
    "process_id",id,"project",taskCompiledPreviewFcsProject,"forced_termination",false,"exit_verified",process.HasExited);
  }
 }
}
