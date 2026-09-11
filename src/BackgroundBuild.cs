using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FStudioMcp {
 internal static partial class VisibleHost {
  static readonly object buildGate=new object();
  static readonly Dictionary<string,BackgroundBuildRun> buildRuns=new Dictionary<string,BackgroundBuildRun>();
  static BackgroundBuildRun activeBuild;

  sealed class BackgroundBuildRun {
   internal string Id,Project,OutputDirectory,State="preparing",CreatedUtc,StartedUtc,CompletedUtc,Exception,ArtifactError,DiagnosticsError;
   internal string NativeTaskStatus="not_started";
   internal bool NativeTaskCompleted,CancelRequested,Finalized;
   internal bool? NativeResult;
   internal object SharedOperation,Operation,Monitor;
   internal Type OperationType;
   internal MethodInfo BuildAsync;
   internal CancellationTokenSource Cancellation=new CancellationTokenSource();
   internal Task<bool> Task;
   internal Dictionary<string,Dictionary<string,object>> Before,After;
   internal Dictionary<string,object> Diagnostics;
   internal BuildLogCapture LogCapture;
   internal EventHandler<FirstChanceExceptionEventArgs> ExceptionCapture;
   internal readonly List<string> NativeExceptions=new List<string>();
   internal readonly List<Dictionary<string,object>> RuleErrors=new List<Dictionary<string,object>>();
   internal Dictionary<string,object> LogInterval;
   internal readonly List<Dictionary<string,object>> Errors=new List<Dictionary<string,object>>();
   internal readonly List<Dictionary<string,object>> UiRequests=new List<Dictionary<string,object>>();
   internal readonly StringBuilder Output=new StringBuilder();
   internal int SuppressedBringToFront;
   public void ReportError(object error) {
    string message=null,exception=null;
    try{message=Convert.ToString(Member(error,"Message"));exception=Convert.ToString(Member(error,"Exception"));}
    catch(Exception e){message="Could not read native error";exception=e.ToString();}
    lock(buildGate)Errors.Add(Obj("message",message,"exception",exception,"utc",DateTime.UtcNow.ToString("o")));
   }
  }

  // This proxy belongs only to this build operation; it never replaces application services.
  sealed class BackgroundBuildMessages : RealProxy {
   readonly BackgroundBuildRun run;
   internal BackgroundBuildMessages(Type contract,BackgroundBuildRun owner):base(contract){run=owner;}
   public override IMessage Invoke(IMessage message) {
    var call=(IMethodCallMessage)message;
    try{
     lock(buildGate){
      switch(call.MethodName){
       case "AppendLine":run.Output.AppendLine(Convert.ToString(call.Args[0]));break;
       case "AppendText":run.Output.Append(Convert.ToString(call.Args[0]));break;
       case "Clear":run.Output.Clear();break;
       case "BringToFront":run.SuppressedBringToFront++;break;
       default:throw new NotSupportedException("Unexpected build output method: "+call.MethodName);
      }
     }
     return new ReturnMessage(null,new object[0],0,call.LogicalCallContext,call);
    }catch(Exception e){return new ReturnMessage(e,call);}
   }
  }

  sealed class BackgroundBuildErrorPad : RealProxy {
   readonly BackgroundBuildRun run;
   internal BackgroundBuildErrorPad(Type type,BackgroundBuildRun owner):base(type){run=owner;}
   void Add(object item){run.RuleErrors.Add(Obj("error_type",Convert.ToString(Member(item,"ErrorType")),"description",Member(item,"Description")));}
   public override IMessage Invoke(IMessage message){
    var call=(IMethodCallMessage)message;
    try{object result=null;lock(buildGate){switch(call.MethodName){
     case "BringToFront":run.SuppressedBringToFront++;break;
     case "get_HasErrors":result=run.RuleErrors.Any(e=>Convert.ToString(e["error_type"])=="Error");break;
     case "ClearErrors":run.RuleErrors.Clear();break;
     case "AddError":Add(call.Args[0]);break;
     case "ResetErrors":run.RuleErrors.Clear();if(call.Args[0]!=null)foreach(var item in (IEnumerable)call.Args[0])Add(item);break;
     default:throw new NotSupportedException(call.MethodName);
    }}return new ReturnMessage(result,new object[0],0,call.LogicalCallContext,call);
    }catch(Exception e){return new ReturnMessage(e,call);}
   }
  }

  // BuildAsync resolves a second compiler through the container. Isolate that
  // compiler too; replacing only the outer operation's output misses its UI.
  sealed class BackgroundBuildResolver : RealProxy {
   readonly BackgroundBuildRun run;readonly object original;
   internal BackgroundBuildResolver(Type type,BackgroundBuildRun owner):base(type){run=owner;original=Resolve(type.FullName);}
   object Dependency(Type type){
    switch(type.FullName){
     case "Flexem.Infrastructure.IDependencyResolver":return GetTransparentProxy();
     case "Flexem.Studio.Build.IBuildMessageService":return new BackgroundBuildMessages(type,run).GetTransparentProxy();
     case "Flexem.Studio.Workbench.IErrorPad":return new BackgroundBuildErrorPad(type,run).GetTransparentProxy();
     case "Flexem.Studio.Workbench.IAsynchronousWaitDialog":
     case "Flexem.Services.IMessageService":return new BackgroundBuildNoUi(type,run).GetTransparentProxy();
     default:return Resolve(type.FullName);
    }
   }
   public override IMessage Invoke(IMessage message){
    var call=(IMethodCallMessage)message;
    try{
     var method=(MethodInfo)call.MethodBase;
     var requested=method.IsGenericMethod?method.GetGenericArguments()[0]:call.Args.OfType<Type>().FirstOrDefault();
     object value;
     if(call.MethodName=="Resolve"&&requested!=null&&requested.FullName=="HB52bCSB8nBAlI1TVs.o9MkFLvKmwij5tJtId"&&call.ArgCount==0){
      var constructor=requested.GetConstructors().Single(c=>c.GetParameters().Length==8);
      value=constructor.Invoke(constructor.GetParameters().Select(p=>Dependency(p.ParameterType)).ToArray());
     }else if(call.MethodName=="Resolve"&&requested!=null&&call.ArgCount==0&&new[]{"Flexem.Studio.Workbench.IErrorPad","Flexem.Studio.Build.IBuildMessageService"}.Contains(requested.FullName))value=Dependency(requested);
     else value=method.Invoke(original,call.Args);
     return new ReturnMessage(value,call.Args,call.ArgCount,call.LogicalCallContext,call);
    }catch(Exception e){return new ReturnMessage(e is TargetInvocationException&&e.InnerException!=null?e.InnerException:e,call);}
   }
  }

  sealed class BackgroundBuildNoUi : RealProxy {
   readonly BackgroundBuildRun run;
   readonly string contract;
   internal BackgroundBuildNoUi(Type type,BackgroundBuildRun owner):base(type){contract=type.FullName;run=owner;}
   public override IMessage Invoke(IMessage message) {
    var call=(IMethodCallMessage)message;
    // The only read-only member on the installed wait-dialog interface.
    if(contract=="Flexem.Studio.Workbench.IAsynchronousWaitDialog"&&call.MethodName=="ShowDialogCount")return new ReturnMessage(0,new object[0],0,call.LogicalCallContext,call);
    var request=Obj("interface",contract,"method",call.MethodName,"utc",DateTime.UtcNow.ToString("o"),"action","blocked");
    lock(buildGate)run.UiRequests.Add(request);
    return new ReturnMessage(new InvalidOperationException("UI_REQUIRED: Background compilation blocked native UI request "+contract+"."+call.MethodName),call);
   }
  }

  static object BackgroundBuildOperation(Dictionary<string,object> a) {
   string op=S(a,"op");
   if(op=="build_diagnostics")return ReadNativeBuildDiagnostics();
   if(op=="build_start")return StartBackgroundBuild();
   if(op!="build_status"&&op!="build_cancel")throw new ArgumentException("Unknown background build operation: "+op);
   BackgroundBuildRun run;
   lock(buildGate)if(!buildRuns.TryGetValue(S(a,"build_id"),out run))throw new ArgumentException("Unknown build_id");
   Within(run.Project);
   if(op=="build_cancel"){
    lock(buildGate){
     if(run!=activeBuild||run.Finalized||run.NativeTaskCompleted)throw new InvalidOperationException("Only the current active build can be cancelled");
     if(!run.CancelRequested){
      run.CancelRequested=true;
      ThreadPool.QueueUserWorkItem(delegate {
       try{run.Cancellation.Cancel();}
       catch(ObjectDisposedException){}
       catch(Exception e){lock(buildGate)run.Errors.Add(Obj("message","Cancellation callback failed","exception",e.ToString()));}
      });
     }
    }
   }
   return BackgroundBuildSnapshot(run);
  }

  static string BuildDiagnosticsException(Exception error) {
   while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;
   return error.ToString();
  }

  static object BuildDiagnosticReference(object value) {
   // Preserve scalar values, but do not serialize native objects, call their ToString,
   // or retain live references in a completed build snapshot.
   if(value==null)return null;
   return Scalar(value.GetType())?Describe(value):Obj("type",value.GetType().FullName);
  }

  static object BuildDiagnosticLocation(object value) {
   // A Location has no scalar properties, so its type name alone cannot attribute an error to anything.
   // GetDescription is the same window and graphic text the native error list shows.
   var reference=BuildDiagnosticReference(value);
   var entry=reference as Dictionary<string,object>;
   if(entry==null)return reference;
   try{
    var text=CallNative(value,"GetDescription") as string;
    if(text!=null)entry["description"]=text.Length>300?text.Substring(0,300):text;
   }catch(Exception e){entry["description_error"]=BuildDiagnosticsException(e);}
   return entry;
  }

  static Dictionary<string,object> ReadNativeBuildDiagnostics() {
   dispatcher.VerifyAccess();RequireProject();
   var diagnostics=Obj("source","current_native_service_snapshot","captured_utc",DateTime.UtcNow.ToString("o"),"project",ProjectFile(),"attribution","Shared native service state; entries may predate or belong to another operation and are not proven exclusive to this build.","native_error_service","Flexem.Studio.Services.IErrorReportService.Errors");
   var errors=new List<Dictionary<string,object>>();var failures=new List<string>();
   try{
    var service=Resolve("Flexem.Studio.Services.IErrorReportService");
    // AddError locks the service while updating this list; the public Errors
    // collection is a separately refreshed UI projection and can omit entries.
    var internalField=service.GetType().GetField("_internalList",BindingFlags.NonPublic|BindingFlags.Instance);
    if(internalField!=null){
     object[] cached;
     lock(service){var list=internalField.GetValue(service) as IEnumerable;cached=list==null?new object[0]:list.Cast<object>().ToArray();}
     diagnostics["native_cached_errors"]=cached.Select(item=>Obj("error_type",Convert.ToString(Member(item,"ErrorType")),"description",Member(item,"Description"),"name",Member(item,"Name"),"mute",Member(item,"Mute"))).ToArray();
     diagnostics["native_cached_error_count"]=cached.Length;
     diagnostics["native_cached_error_scope"]="Shared internal error cache, including muted entries; not exclusive to this build.";
    }else diagnostics["native_cached_error_status"]="unavailable_in_native_version";
    var collection=Member(service,"Errors") as IEnumerable;
    if(collection==null)throw new InvalidOperationException("The native error service did not expose an Errors collection");
    // ErrorReportService refreshes this collection on its WPF dispatcher.
    foreach(var item in collection){
     try{
      errors.Add(Obj("error_type",Convert.ToString(Member(item,"ErrorType")),"description",Member(item,"Description"),"name",Member(item,"Name"),"role",Member(item,"Role"),"mute",Member(item,"Mute"),"source",BuildDiagnosticReference(Member(item,"Source")),"target",BuildDiagnosticReference(Member(item,"Target")),"location_source",BuildDiagnosticReference(Member(item,"LocationSource"))));
     }catch(Exception e){
      string error=BuildDiagnosticsException(e);failures.Add("native_error_entry: "+error);
      errors.Add(Obj("diagnostics_error",error));
     }
    }
   }catch(Exception e){failures.Add("native_error_service: "+BuildDiagnosticsException(e));}
   diagnostics["errors"]=errors.ToArray();diagnostics["native_error_count"]=errors.Count;
   try{
    // Read only an already-created pad. The Instance getter can create UI.
    var padType=Native("Flexem.Studio.Pads.ErrorList.ErrorListPad");
    var pad=padType.GetField("_instance",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
    var viewModel=pad==null?null:padType.GetField("_viewModel",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pad);
    var source=viewModel==null?null:Member(viewModel,"ErrorsSource") as IEnumerable;
    diagnostics["native_build_error_pad_status"]=source==null?"not_initialized":"available";
    diagnostics["native_build_errors"]=source==null?new object[0]:source.Cast<object>().Select(item=>Obj("error_type",Convert.ToString(Member(item,"ErrorType")),"description",Member(item,"Description"),"location",BuildDiagnosticLocation(Member(item,"Location")))).ToArray();
   }catch(Exception e){failures.Add("native_build_error_pad: "+BuildDiagnosticsException(e));}
   try{
    // Output() reads only OutputPad._instance and existing category text. It never
    // calls the lazy Instance getter, constructs a pad, or changes foreground UI.
    diagnostics["output"]=Output();
   }catch(Exception e){
    diagnostics["output"]=null;failures.Add("native_output: "+BuildDiagnosticsException(e));
   }
   diagnostics["diagnostics_error"]=failures.Count==0?null:String.Join(Environment.NewLine,failures);
   return diagnostics;
  }

  static object StartBackgroundBuild() {
   RequireProject();
   var project=Path.GetFullPath(ProjectFile());
   if(!File.Exists(project))throw new FileNotFoundException("Save the project before compiling",project);
   var contract=Native("Flexem.Studio.Build.Operations.IBuildOperation");
   object shared=null;
   try{shared=Resolve(contract.FullName);}catch{ /* Some installations expose only the concrete operation. */ }
   if(shared!=null&&(bool)contract.GetProperty("IsBuilding").GetValue(shared,null))throw new InvalidOperationException("FStudio is already building");
   var run=new BackgroundBuildRun{Id=Guid.NewGuid().ToString("N"),Project=project,OutputDirectory=Path.Combine(Path.GetDirectoryName(project),"Bin"),CreatedUtc=DateTime.UtcNow.ToString("o"),SharedOperation=shared,OperationType=contract};
   Within(run.OutputDirectory);
   lock(buildGate){
    if(activeBuild!=null&&!activeBuild.Finalized)throw new InvalidOperationException("An MCP build is already active");
    buildRuns.Add(run.Id,run);activeBuild=run;
   }
   // Fingerprinting never runs on the WPF dispatcher or on a status request.
   ThreadPool.QueueUserWorkItem(delegate {
    try{
     var before=FingerprintBuildOutputs(run.OutputDirectory);
     run.LogCapture=new BuildLogCapture(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Flexem","FStudio3","Logs","Log.txt"));
     lock(buildGate){run.Before=before;run.State="queued";}
     dispatcher.BeginInvoke(new Action(()=>InvokeBackgroundBuild(run)),DispatcherPriority.Background);
    }catch(Exception e){FinishBackgroundBuild(run,null,e,false);}
   });
   return BackgroundBuildSnapshot(run);
  }

  static void InvokeBackgroundBuild(BackgroundBuildRun run) {
   try{
    lock(buildGate)if(run.CancelRequested){FinishBackgroundBuild(run,null,null,true);return;}
    RequireProject();
    if(!String.Equals(Path.GetFullPath(ProjectFile()),run.Project,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The current project changed before build startup");
    if(run.SharedOperation!=null&&(bool)run.OperationType.GetProperty("IsBuilding").GetValue(run.SharedOperation,null))throw new InvalidOperationException("FStudio started another build before this build was dispatched");
    string[] dependencies={"Flexem.Studio.Build.IBuildMessageService","Flexem.Infrastructure.IDependencyResolver","Flexem.Studio.Workbench.IAsynchronousWaitDialog","Flexem.Services.IStringParser","Flexem.Services.IMessageService","Flexem.Services.ILoggingService","Flexem.Studio.Hmi.Options.IBuildOptions"};
    var dependencyTypes=dependencies.Select(Native).ToArray();
    var implementation=run.SharedOperation==null?run.OperationType.Assembly.GetTypes().Single(t=>!t.IsAbstract&&!t.IsInterface&&run.OperationType.IsAssignableFrom(t)&&t.GetConstructor(dependencyTypes)!=null):run.SharedOperation.GetType();
    var constructor=implementation.GetConstructor(dependencyTypes);
    if(constructor==null)throw new NotSupportedException("The installed build operation does not expose the verified dependency-injection constructor");
    var values=dependencies.Select((name,index)=>index==0?new BackgroundBuildMessages(dependencyTypes[0],run).GetTransparentProxy():(index==2||index==4)?new BackgroundBuildNoUi(dependencyTypes[index],run).GetTransparentProxy():Resolve(name)).ToArray();
    values[1]=new BackgroundBuildResolver(dependencyTypes[1],run).GetTransparentProxy();
    run.Operation=constructor.Invoke(values);
    var monitorType=Native("Flexem.Studio.Asynchronous.DummyProgressMonitor");
    run.Monitor=Activator.CreateInstance(monitorType);
    monitorType.GetProperty("ShowingDialog").SetValue(run.Monitor,false,null);
    monitorType.GetProperty("CancellationToken").SetValue(run.Monitor,run.Cancellation.Token,null);
    var errorType=Native("Flexem.Studio.Asynchronous.Error");
    var callbackType=typeof(Action<>).MakeGenericType(errorType);
    var parameter=Expression.Parameter(errorType,"error");
    var callback=Expression.Lambda(callbackType,Expression.Call(Expression.Constant(run),typeof(BackgroundBuildRun).GetMethod("ReportError"),Expression.Convert(parameter,typeof(object))),parameter).Compile();
    var fileType=Native("Flexem.FileName");
    var file=Activator.CreateInstance(fileType,new object[]{run.Project});
    run.BuildAsync=run.OperationType.GetMethod("BuildAsync",new Type[]{fileType,Native("Flexem.Studio.Asynchronous.IProgressMonitor"),callbackType});
    if(run.BuildAsync==null)throw new NotSupportedException("The installed BuildAsync signature differs from the verified contract");
    lock(buildGate){run.State="running";run.StartedUtc=DateTime.UtcNow.ToString("o");}
    // Native compilation sometimes replaces the original exception with a
    // generic window error. Retain bounded diagnostics during this build only.
    run.ExceptionCapture=(sender,args)=>{
     try{
      var exception=args.Exception;
      if(exception.TargetSite==null||exception.TargetSite.Module.Assembly.GetName().Name!="Flexem.Studio.Build")return;
      lock(buildGate){if(run.NativeExceptions.Count<16)run.NativeExceptions.Add(exception.ToString());}
     }catch{ /* Diagnostic capture must never affect native execution. */ }
    };
    AppDomain.CurrentDomain.FirstChanceException+=run.ExceptionCapture;
    var task=run.BuildAsync.Invoke(run.Operation,new object[]{file,run.Monitor,callback}) as Task<bool>;
    if(task==null)throw new InvalidOperationException("Native BuildAsync did not return Task<bool>");
    lock(buildGate){run.Task=task;run.NativeTaskStatus=task.Status.ToString();}
    task.ContinueWith(completed=>{
     if(completed.IsCanceled)FinishBackgroundBuild(run,null,null,true);
     else if(completed.IsFaulted)FinishBackgroundBuild(run,null,completed.Exception,false);
     else FinishBackgroundBuild(run,completed.Result,null,false);
    },CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
   }catch(Exception e){FinishBackgroundBuild(run,null,e,false);}
  }

  static void FinishBackgroundBuild(BackgroundBuildRun run,bool? result,Exception error,bool cancelled) {
   if(run.ExceptionCapture!=null){AppDomain.CurrentDomain.FirstChanceException-=run.ExceptionCapture;run.ExceptionCapture=null;}
   // This may be called from the dispatcher on startup failure; move disk IO to the pool.
   while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;
   var capturedError=error;
   lock(buildGate){
    run.NativeTaskCompleted=run.Task!=null&&run.Task.IsCompleted;
    run.NativeTaskStatus=run.Task==null?"not_started":run.Task.Status.ToString();
    run.NativeResult=result;run.Exception=capturedError==null?null:capturedError.ToString();run.State="collecting_outputs";
   }
   ThreadPool.QueueUserWorkItem(delegate {
    if(run.LogCapture!=null){var interval=run.LogCapture.Read();lock(buildGate)run.LogInterval=interval;}
    try{var after=FingerprintBuildOutputs(run.OutputDirectory);lock(buildGate)run.After=after;}
    catch(Exception e){lock(buildGate)run.ArtifactError=e.ToString();}
    finally{
     var monitor=run.Monitor as IDisposable;
     try{if(monitor!=null)monitor.Dispose();}catch(Exception e){lock(buildGate)run.Errors.Add(Obj("message","Progress monitor disposal failed","exception",e.ToString()));}
     QueueFinishedBuildDiagnostics(run,capturedError,cancelled);
    }
   });
  }

  static void QueueFinishedBuildDiagnostics(BackgroundBuildRun run,Exception nativeError,bool cancelled) {
   lock(buildGate)run.State="collecting_diagnostics";
   try{
    if(dispatcher.HasShutdownStarted||dispatcher.HasShutdownFinished)throw new InvalidOperationException("The FStudio dispatcher is shutting down; native diagnostics could not be captured");
    // Native Errors refresh is queued at Background priority. ContextIdle permits
    // already queued refreshes to run first, without a nested dispatcher pump.
    var operation=dispatcher.BeginInvoke(new Action(()=>{
     try{
      var diagnostics=ReadNativeBuildDiagnostics();
      diagnostics["native_log_interval"]=run.LogInterval;
      lock(buildGate)diagnostics["native_exception_interval"]=run.NativeExceptions.ToArray();
      lock(buildGate)diagnostics["build_rule_errors"]=run.RuleErrors.ToArray();
      lock(buildGate){run.Diagnostics=diagnostics;run.DiagnosticsError=diagnostics["diagnostics_error"] as string;}
     }catch(Exception e){lock(buildGate)run.DiagnosticsError=BuildDiagnosticsException(e);}
     finally{FinalizeBackgroundBuild(run,nativeError,cancelled);}
    }),DispatcherPriority.ContextIdle);
    EventHandler aborted=delegate {
     lock(buildGate)if(!run.Finalized)run.DiagnosticsError="The dispatcher aborted the native diagnostics snapshot";
     FinalizeBackgroundBuild(run,nativeError,cancelled);
    };
    operation.Aborted+=aborted;
    if(operation.Status==DispatcherOperationStatus.Aborted)aborted(operation,EventArgs.Empty);
   }catch(Exception e){
    lock(buildGate)run.DiagnosticsError=BuildDiagnosticsException(e);
    FinalizeBackgroundBuild(run,nativeError,cancelled);
   }
  }

  static void FinalizeBackgroundBuild(BackgroundBuildRun run,Exception nativeError,bool cancelled) {
   lock(buildGate){
    if(run.Finalized)return;
    // Diagnostic collection failures do not replace the native task/result or its
    // outcome. Keep global service diagnostics separate from per-build callbacks.
    run.State=run.UiRequests.Count>0?"ui_required":cancelled?"cancelled":nativeError!=null?"failed":"completed";
    run.CompletedUtc=DateTime.UtcNow.ToString("o");run.Finalized=true;if(activeBuild==run)activeBuild=null;
   }
   run.Cancellation.Dispose();
  }

  static Dictionary<string,Dictionary<string,object>> FingerprintBuildOutputs(string directory) {
   Within(directory);
   var result=new Dictionary<string,Dictionary<string,object>>(StringComparer.OrdinalIgnoreCase);
   if(!Directory.Exists(directory))return result;
   var pending=new Stack<string>();pending.Push(directory);
   while(pending.Count>0){
    var current=pending.Pop();Within(current);
    if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Build artifact directory is a reparse point: "+current);
    foreach(var child in Directory.GetDirectories(current))pending.Push(child);
    foreach(var file in Directory.GetFiles(current).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)){
     Within(file);var before=new FileInfo(file);
     if((before.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Build artifact is a reparse point: "+file);
     long length=before.Length,ticks=before.LastWriteTimeUtc.Ticks;string hash;
     using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
     var after=new FileInfo(file);
     if(!after.Exists||after.Length!=length||after.LastWriteTimeUtc.Ticks!=ticks)throw new IOException("Build artifact changed while fingerprinting: "+file);
     result[file]=Obj("file",file,"bytes",length,"modified_utc",new DateTime(ticks,DateTimeKind.Utc).ToString("o"),"sha256",hash);
    }
   }
   return result;
  }

  static object[] CompareBuildOutputs(BackgroundBuildRun run) {
   if(run.Before==null||run.After==null)return new object[0];
   return run.Before.Keys.Union(run.After.Keys,StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).Select(file=>{
    Dictionary<string,object> before,after;run.Before.TryGetValue(file,out before);run.After.TryGetValue(file,out after);
    string change=before==null?"created":after==null?"deleted":!Object.Equals(before["sha256"],after["sha256"])?"content_changed":!Object.Equals(before["modified_utc"],after["modified_utc"])?"rewritten_same_content":"unchanged";
    return (object)Obj("file",file,"before",before,"after",after,"change",change,"observed_change_during_build",change!="unchanged");
   }).ToArray();
  }

  static object BackgroundBuildSnapshot(BackgroundBuildRun run) {
   lock(buildGate)return Obj("build_id",run.Id,"status",run.State,"project",run.Project,"created_utc",run.CreatedUtc,"started_utc",run.StartedUtc,"completed_utc",run.CompletedUtc,"cancel_requested",run.CancelRequested,"native_task_status",run.Task==null?run.NativeTaskStatus:run.Task.Status.ToString(),"native_task_completed",run.Task!=null&&run.Task.IsCompleted,"native_build_result",run.NativeResult,"compiler_success",run.UiRequests.Count>0?(object)false:run.NativeResult,"errors",run.Errors.ToArray(),"exception",run.Exception,"ui_requests",run.UiRequests.ToArray(),"output",run.Output.ToString(),"diagnostics",run.Diagnostics,"diagnostics_error",run.DiagnosticsError,"output_directory",run.OutputDirectory,"output_files",CompareBuildOutputs(run),"before_fingerprints",run.Before==null?null:run.Before.Values.ToArray(),"artifact_verification",run.Before!=null&&run.After!=null?"snapshots_compared":run.Finalized||run.ArtifactError!=null?"limited":"pending","artifact_error",run.ArtifactError,"suppressed_output_bring_to_front",run.SuppressedBringToFront,"execution_mode",ExecutionMode,"visible_log",false,"native_dialog_behavior","injected_message_and_wait_services_blocked_other_internal_paths_unverified","note","Calls native BuildAsync using a local operation instance. Its injected output service only records text; message/wait UI requests fail with ui_required. Diagnostics are timestamped shared native service snapshots, not proven per-build errors. Task completion and the native boolean result are separate; unchanged pre-existing files do not prove a new build. No global service or option is replaced.");
  }
 }
}
