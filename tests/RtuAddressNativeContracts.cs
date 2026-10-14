using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

/// <summary>
/// 在独立 32 位进程中验证 RTU 地址工具的关键原生类型约束。
/// 只调用原生身份构造器，并用相同可空属性类型测试宿主转换；不启动 FStudio，不保存工程，不打开串口或访问设备。
/// </summary>
class RtuAddressNativeContracts {
 // 使用与原生属性完全相同的可空值类型验证赋值，不伪造或替换原生工程模型。
 public class NullableRegisterProbe<T> where T:struct { public T? RegisterId {get;set;} }
 static readonly string Bin=@"C:\Program Files (x86)\Flexem\FStudio 3.x\Bin";
 static int checks;
 static void Require(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks++;}
 static object Value(object target,string name){return target.GetType().GetProperty(name).GetValue(target,null);}
 static void Main(string[] args){
  // 供应商异常的 ToString 可能再次访问未建立的全局服务，只输出逐层类型和消息定位独立进程限制。
  try{Run(args);}catch(Exception error){
   for(var current=error;current!=null;current=current.InnerException){
    Console.WriteLine("失败类型："+current.GetType().FullName);
    try{Console.WriteLine("失败消息："+current.Message);Console.WriteLine(current.StackTrace);}catch{Console.WriteLine("失败消息无法读取。");}
   }
   Environment.ExitCode=1;
  }
 }
 static void Run(string[] args){
  // 仅从已安装 FStudio 目录解析其依赖，避免将额外程序集复制到安装目录或修改宿主配置。
  AppDomain.CurrentDomain.AssemblyResolve+=(sender,eventArgs)=>{
   string file=Path.Combine(Bin,new AssemblyName(eventArgs.Name).Name+".dll");
   return File.Exists(file)?Assembly.LoadFrom(file):null;
  };
  var hmi=Assembly.LoadFrom(Path.Combine(Bin,"Flexem.Studio.Hmi.dll"));
  var models=Assembly.LoadFrom(Path.Combine(Bin,"Flexem.Studio.Hmi.Models.dll"));
  var bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));
  var host=bridge.GetType("FStudioMcp.VisibleHost",true);
  var integer=host.GetMethod("ModbusRtuInteger",BindingFlags.NonPublic|BindingFlags.Static);
  foreach(uint value in new uint[]{1,247})
   Require((uint)integer.Invoke(null,new object[]{new Dictionary<string,object>{{"station",value}},"station",(uint)1,(uint)247})==value,"合法站号回读");
  foreach(object bad in new object[]{0,248,-1,1.5,true,"1",null,Double.NaN,Double.PositiveInfinity}){
   bool rejected=false;
   try{integer.Invoke(null,new object[]{new Dictionary<string,object>{{"station",bad}},"station",(uint)1,(uint)247});}
   catch(TargetInvocationException error){rejected=error.InnerException is ArgumentException;}
   Require(rejected,"非法站号被拒绝");
  }
  // 与原生 AddressRawData 构造器一致，将驱动组合编号 65540 原样存入身份，不能使用解码工厂拆成 4。
  var identityType=hmi.GetType("Flexem.Studio.Hmi.Device.Configuration.RegisterIdentity",true);
  var widthType=hmi.GetType("Flexem.Studio.Hmi.Device.Configuration.AddressWidthType",true);
  var identity=Activator.CreateInstance(identityType,new object[]{(uint)65540,Enum.Parse(widthType,"Word")});
  Require(Convert.ToUInt32(Value(identity,"Id"))==65540,"4X身份保留驱动组合ID");
  Require(Convert.ToString(Value(identity,"AddressWidthType"))=="Word","4X身份字宽");
  var nativeSet=host.GetMethod("NativeSet",BindingFlags.NonPublic|BindingFlags.Static);
  var rawType=models.GetType("Flexem.Studio.Hmi.Models.Address.AddressRawData",true);
  var nativeProperty=rawType.GetProperty("RegisterId");
  Require(Nullable.GetUnderlyingType(nativeProperty.PropertyType)==identityType,"原生寄存器属性完整类型");
  var probeType=typeof(NullableRegisterProbe<>).MakeGenericType(identityType);
  Require(probeType.GetProperty("RegisterId").PropertyType==nativeProperty.PropertyType,"赋值验证类型与原生属性一致");
  var probe=Activator.CreateInstance(probeType);
  // 经宿主同一个 NativeSet/ConvertArg 通路验证完整装箱身份可写入 Nullable<RegisterIdentity>。
  // 原生 AddressRawData 的构造器依赖 FStudio IoC，故独立测试不宣称已验证其工程、撤销或通信行为。
  nativeSet.Invoke(null,new[]{probe,"RegisterId",identity});
  Require(Convert.ToUInt32(Value(Value(probe,"RegisterId"),"Id"))==65540,"完整nullable身份保留组合ID赋值");
  Require(Convert.ToString(Value(Value(probe,"RegisterId"),"AddressWidthType"))=="Word","可空身份宽度保持");
  Console.WriteLine("原生RTU组合身份构造器和宿主参数转换通过："+checks+"项；未打开工程、GUI或串口。编译有效性由实际MCP工程验收。");
 }
}
