using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;

namespace FStudioMcp {
 internal static partial class VisibleHost {
  /// <summary>按原生 LoadProject 的目录长度限制提前拒绝，防止系统提示阻塞 UI 任务队列。</summary>
  static void RequireNativeProjectDirectory(string directory){
   if(Path.GetFullPath(directory).Length>90)throw new ArgumentException("FStudio project directory exceeds native 90-character limit; use a shorter workspace or project name");
  }
  /// <summary>将本次创建器的错误提示转换为任务异常，防止原生消息框阻塞后台队列。</summary>
  sealed class ProjectCreationMessages : RealProxy {
   /// <summary>绑定本次操作的消息接口，不替换应用全局服务。</summary>
   internal ProjectCreationMessages(Type contract):base(contract){}
   /// <summary>保留提示方法及异常参数，让客户端能够定位失败原因。</summary>
   public override IMessage Invoke(IMessage message){
    var call=(IMethodCallMessage)message;
    var detail=String.Join(" | ",call.Args.Select(x=>x==null?"":x.ToString()).ToArray());
    return new ReturnMessage(new InvalidOperationException("BACKGROUND_ONLY: project creation requested "+call.MethodName+": "+detail),call);
   }
  }
  /// <summary>复制创建器依赖，仅为本次调用注入不弹窗的消息服务；依赖结构不符时拒绝执行。</summary>
  static object BackgroundProjectCreator(){
   var shared=Resolve("Flexem.Studio.HMIWizard.HMIProjectCreator");
   var type=shared.GetType();
   var constructor=type.GetConstructors().Single();
   var fields=type.GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
   var arguments=constructor.GetParameters().Select(p=>p.ParameterType.FullName=="Flexem.Services.IMessageService"
    ?new ProjectCreationMessages(p.ParameterType).GetTransparentProxy()
    :fields.Single(f=>f.FieldType==p.ParameterType).GetValue(shared)).ToArray();
   return constructor.Invoke(arguments);
  }
  /// <summary>在工作区创建原生工程并打开；错误通过任务返回，已创建的失败目录保留供检查。</summary>
  static object CreateProject(Dictionary<string,object>a){
   string name=S(a,"name");
   if(name!=Path.GetFileName(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name=="."||name=="..")throw new ArgumentException("Invalid project name");
   string destination=Path.GetFullPath(Path.Combine(workspace,name));Within(destination);
   RequireNativeProjectDirectory(destination);
   if(Directory.Exists(destination)||File.Exists(destination))throw new ArgumentException("Destination already exists");
   string temporary=Path.Combine(stateDir,"input-"+Guid.NewGuid().ToString("N")+".cfg");
   try{
    File.WriteAllText(temporary,S(a,"template_xml"),new UTF8Encoding(false));
    var type=Native("Flexem.Studio.HMIWizard.HMIProjectInfo");
    var fetch=type.GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.FlattenHierarchy).Single(m=>m.Name=="FetchAsChild"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType==typeof(string));
    var info=fetch.Invoke(null,new object[]{temporary});
    if(Convert.ToString(Member(info,"Name"))!=S(a,"model"))throw new ArgumentException("Template model identity mismatch");
    var creator=BackgroundProjectCreator();var create=creator.GetType().GetMethod("Create");
    var step=Enum.Parse(create.GetParameters()[3].ParameterType,"Step1");
    string entry=Convert.ToString(create.Invoke(creator,new object[]{workspace.TrimEnd('\\'),name,info,step}));
    if(String.IsNullOrEmpty(entry)||!File.Exists(entry))throw new InvalidOperationException("Native project creator did not return a project");
    if(CurrentProject()!=null){var closed=ProjectService("CloseCurrentProject",true);if(!(bool)closed)throw new InvalidOperationException("Created project, but switch was cancelled: "+entry);}
    ClearRefs();ProjectService("OpenProjectWithPath",entry);
    if(!String.Equals(ProjectFile(),entry,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Created project could not be opened: "+entry);
    return Obj("project",entry,"native_created",true,"opened_in_editor",true);
   }finally{if(File.Exists(temporary))File.Delete(temporary);}
  }
 }
}
