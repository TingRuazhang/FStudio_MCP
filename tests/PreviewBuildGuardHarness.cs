using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

/// <summary>
/// 在独立进程加载真实宿主 DLL，验证预览入口先执行 activeBuild 所有权保护。
/// 只构造内存中的编译记录；没有启动 FStudio、打开工程、运行模拟器或向窗口发送消息。
/// </summary>
public static class PreviewBuildGuardHarness {
 static Type host;
 static int passed;
 const BindingFlags StaticPrivate=BindingFlags.Static|BindingFlags.NonPublic;

 /// <summary>反射调用真实分发入口，并解开异常；测试拒绝原因必须是所有权保护而非后续原生 API 缺失。</summary>
 static object Invoke(string name,params object[] args){
  try{return host.GetMethod(name,StaticPrivate).Invoke(null,args);}
  catch(TargetInvocationException error){throw error.InnerException;}
 }
 static Dictionary<string,object> Request(string operation){return new Dictionary<string,object>{{"op",operation},{"build_id","missing-probe"}};}
 static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);passed++;}

 /// <summary>预览必须在原生服务、保存和进程代码之前拒绝；精确检查原有 guard 的原因文本。</summary>
 static void RejectOwnedBuild(string operation){
  try{Invoke("OnUi",Request(operation));}
  catch(InvalidOperationException error){
   if(error.Message.StartsWith("A background build owns the project",StringComparison.Ordinal)){passed++;return;}
   throw;
  }
  throw new InvalidOperationException("Active build did not reject "+operation);
 }

 /// <summary>只读状态和原有取消特例需仍能进入编译分发；未知测试 ID 在查表处拒绝，不创建或取消真实任务。</summary>
 static void CheckExistingBuildDispatch(string operation){
  try{Invoke("OnUi",Request(operation));}
  catch(ArgumentException error){if(error.Message=="Unknown build_id"){passed++;return;}throw;}
  throw new InvalidOperationException("Expected build lookup refusal for "+operation);
 }

 public static int Main(string[] args){
  if(args.Length!=1){Console.Error.WriteLine("Usage: PreviewBuildGuardHarness.exe <host.dll>");return 1;}
  object original=null;FieldInfo active=null;
  try{
   host=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("FStudioMcp.VisibleHost",true);
   active=host.GetField("activeBuild",StaticPrivate);original=active.GetValue(null);
   var runType=host.GetNestedType("BackgroundBuildRun",BindingFlags.NonPublic);
   var run=Activator.CreateInstance(runType,true);
   // Finalized=false 是未完成编译的原生内存状态；该记录不加入 buildRuns，也不启动任何后台任务。
   runType.GetField("Finalized",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(run,false);
   active.SetValue(null,run);
   foreach(string op in new[]{"task_compiled_preview_preflight","task_simulate_compiled_offline","task_simulate_offline","task_compiled_helper_close","task_simulator_close","save_project","build_start"})RejectOwnedBuild(op);
   Check((bool)Invoke("IsReadOnly","task_render_canvas"),"Editor rendering should remain a read-only observation");
   Check((bool)Invoke("IsReadOnly","task_simulator_capture"),"Owned simulator capture should remain a read-only observation");
   Check(!(bool)Invoke("IsReadOnly","task_compiled_preview_preflight"),"Staging authorization must remain protected");
   CheckExistingBuildDispatch("build_status");CheckExistingBuildDispatch("build_cancel");
   // Handle(health) 是同步元数据入口，不调度 UI；即使没有启动宿主也可验证版本与实际声明能力。
   var health=(Dictionary<string,object>)Invoke("Handle",Request("health"));
   Check(Convert.ToString(health["version"])=="0.5.0","Host health must report the published API version");
   var features=(string[])health["features"];
   Check(features.Distinct().Count()==features.Length,"Host features must be unique");
   Check(new[]{"drawing_configure_modbus_rtu_address","drawing_configure_numeric_zoom","drawing_configure_numeric_limits","task_simulate_compiled_offline","task_simulator_capture","task_compiled_helper_close"}.All(features.Contains),"Published features must include the generic RTU/numeric/compiled preview operations");
   Console.WriteLine("Preview build ownership checks passed: "+passed);return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  finally{if(active!=null)active.SetValue(null,original);}
 }
}
