using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FStudioMcp {
 internal static partial class VisibleHost {
  const string NativeWindowServiceType="Flexem.Studio.HMI.Window.HMIWindowService";
  const string NativeWindowModelType="Flexem.Studio.HMI.Window.HMIWindowInfo";
  static object AddLocalSampling(Dictionary<string,object>a) {
   RequireProject();string name=S(a,"name").Trim();if(name.Length==0||name.Any(Char.IsControl))throw new ArgumentException("Invalid sampling name");
   foreach(string key in new[]{"address","channels","cycle_ms"}){
    object value=a[key];if(value==null||value is bool||value is string)throw new ArgumentException(key+" must be an integer");
    double n=Convert.ToDouble(value);double max=key=="channels"?16:UInt32.MaxValue;
    if(Double.IsNaN(n)||Double.IsInfinity(n)||n!=Math.Truncate(n)||n<(key=="address"?0:1)||n>max)throw new ArgumentException("Invalid "+key);
   }
   uint address=Convert.ToUInt32(a["address"]);int channels=Convert.ToInt32(a["channels"]);
   if((ulong)address+(uint)channels-1>UInt32.MaxValue)throw new ArgumentException("Sampling address range overflow");
   var config=Member(Member(CurrentProject(),"HMIConfigInfo"),"DataSampleConfigInfo");var collection=Member(config,"DataSampleInfos");
   var existing=((IEnumerable)collection).Cast<object>().ToArray();
   if(existing.Any(x=>Convert.ToString(Member(Member(x,"PropertyInfo"),"Description"))==name))throw new ArgumentException("A sampling group with this name already exists");
   var item=PageStatic("Flexem.Studio.HMI.DataSampleInfo","CreateAsChild");var properties=Member(item,"PropertyInfo");var settings=Member(item,"ChannelConfigInfo");
   NativeSet(properties,"Description",name);NativeSet(properties,"UseSerialAddress",true);NativeSet(properties,"DataSampleMode","Cycle");NativeSet(properties,"DataSampleCycle",Convert.ToUInt32(a["cycle_ms"]));NativeSet(properties,"DataSampleUnit","Millisecond");NativeSet(properties,"SamplingPeriodType","Constant");NativeSet(properties,"IsPauseControl",false);NativeSet(properties,"IsClearControl",false);NativeSet(properties,"IsValidOnOpenWindow",false);NativeSet(Member(properties,"HistoryDataSaveInfo"),"Target","UnSave");
   var start=Member(properties,"StartingAddress");NativeSet(start,"DataType","UInt16");
   var native=PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateWord",Member(PageServices(),"DeviceService"),Member(start,"DataType"),true);var raw=Member(native,"Raw");
   NativeSet(start,"DeviceId",Member(raw,"DeviceId"));NativeSet(start,"RegisterId",Member(Member(raw,"RegisterId"),"Id"));NativeSet(start,"MainAddress",address);
   var rows=Member(settings,"ChannelInfos");if(((IEnumerable)rows).Cast<object>().Any())throw new InvalidOperationException("Native sampling factory changed: expected empty channels");
   NativeSet(settings,"EachPipeNum",1);
   for(int i=0;i<channels;i++){
    var channel=PageStatic("Flexem.Studio.HMI.DataSampleChannelInfo","CreateAsChild");NativeSet(channel,"ChannelId",i+1);NativeSet(channel,"ChannelType","UInt16");NativeSet(channel,"WordCount",1);NativeSet(channel,"OccupiedWordCount",1);NativeSet(channel,"MainAddress",address+(uint)i);NativeSet(channel,"Comment",name+" "+(i+1));CallNative(rows,"Add",channel);
   }
   // The collection's OnCollectionChanged maintains its private sequence ids.
   string file=Path.GetFullPath(Convert.ToString(Member(config,"FileName")));Within(file);byte[] original=File.Exists(file)?File.ReadAllBytes(file):null;
   try{CallNative(collection,"Add",item);CallNative(config,"SaveToFile",file);}
   catch(Exception error){try{if(((IEnumerable)collection).Cast<object>().Any(x=>Object.ReferenceEquals(x,item)))CallNative(collection,"Remove",item);if(original!=null)File.WriteAllBytes(file,original);else if(File.Exists(file))File.Delete(file);}catch(Exception rollback){throw new AggregateException("Sampling insertion and rollback failed",error,rollback);}throw;}
   return Obj("sampling",Describe(item),"sample_id",Member(item,"Id").ToString(),"name",name,"address",address,"channels",channels,"cycle_ms",Member(properties,"DataSampleCycle"),"native_undo_transaction",false,"persistence","native_configuration_saved","hardware_io",false);
  }
  static object AddPageTransmission(Dictionary<string,object>a) {
   var view=DesignerView();var page=Member(view,"Model");RequireBasicPage(page);
   string dataType=a.ContainsKey("data_type")?S(a,"data_type"):"Word";
   string direction=a.ContainsKey("direction")?S(a,"direction"):"OneWay";
   string conflict=a.ContainsKey("conflict_priority")?S(a,"conflict_priority"):"SourceAddress";
   if(!new[]{"Word","Bit"}.Contains(dataType)||!new[]{"OneWay","TwoWay"}.Contains(direction)||!new[]{"SourceAddress","TargetAddress"}.Contains(conflict))throw new ArgumentException("Invalid transmission mode");
   bool controlled=a.ContainsKey("direction_control_address");
   if((controlled||a.ContainsKey("conflict_priority"))&&direction!="TwoWay")throw new ArgumentException("Direction control and conflict priority require TwoWay");
   if(controlled&&a.ContainsKey("conflict_priority"))throw new ArgumentException("Choose direction control or conflict priority, not both");
   var numericKeys=new List<string>{"trigger_address","source_address","target_address","count","cycle"};if(controlled)numericKeys.Add("direction_control_address");
   foreach(string key in numericKeys){
    object value=a[key];if(value==null||value is bool||value is string)throw new ArgumentException(key+" must be an integer");
    double n=Convert.ToDouble(value);bool positive=key=="count"||key=="cycle";
    if(Double.IsNaN(n)||Double.IsInfinity(n)||n!=Math.Truncate(n)||n<(positive?1:0)||n>(positive?Int32.MaxValue:UInt32.MaxValue))throw new ArgumentException("Invalid "+key);
   }
   ulong count=Convert.ToUInt64(a["count"]),source=Convert.ToUInt64(a["source_address"]),target=Convert.ToUInt64(a["target_address"]);
   if(source+count-1>UInt32.MaxValue||target+count-1>UInt32.MaxValue)throw new ArgumentException("Address range overflow");
   if(source<target+count&&target<source+count)throw new ArgumentException("Source and target ranges must not overlap");
   var notices=new Dictionary<string,Dictionary<string,object>>();
   foreach(string key in new[]{"before_bit","after_bit","before_word","after_word"}){
    if(!a.ContainsKey(key))continue;
    var notice=a[key] as Dictionary<string,object>;
    if(notice==null||notice.Count!=2||!notice.ContainsKey("address")||!notice.ContainsKey("value"))throw new ArgumentException(key+" requires only address and value");
    object address=notice["address"];if(address==null||address is bool||address is string)throw new ArgumentException("Invalid notice address");
    double n=Convert.ToDouble(address);if(Double.IsNaN(n)||Double.IsInfinity(n)||n!=Math.Truncate(n)||n<0||n>UInt32.MaxValue)throw new ArgumentException("Invalid notice address");
    object value=notice["value"];
    if(key.EndsWith("_bit")){if(!(value is bool))throw new ArgumentException("Bit notice value must be boolean");}
    else{
     if(value==null||value is bool||value is string)throw new ArgumentException("Word notice value must be UInt16");
     n=Convert.ToDouble(value);if(Double.IsNaN(n)||Double.IsInfinity(n)||n!=Math.Truncate(n)||n<0||n>UInt16.MaxValue)throw new ArgumentException("Word notice value must be UInt16");
    }
    notices.Add(key,notice);
   }
   var item=PageStatic("Flexem.Studio.Components.Parts.DataTransmissionPart.DataTransmissionItemInfo","CreateAsChild");
   var scope=new DrawingDraft{Model=item,Page=page};
   var trigger=Member(item,"TriggerAndBreakInfo");var settings=Member(item,"DataTransmissionSetInfo");
   NativeSet(trigger,"TriggerCondition","BitStatus");NativeSet(trigger,"TriggerMode","OffToOn");NativeSet(trigger,"CloseCondition","WindowsClose");NativeSet(trigger,"ExecutionCycle",Convert.ToInt32(a["cycle"]));NativeSet(trigger,"EnableHighSpeedTimer",false);NativeSet(trigger,"AutoReset",false);
   NativeSet(settings,"DirectionType",direction);NativeSet(settings,"DataType",dataType);NativeSet(settings,"RegisterNumberType","Constant");NativeSet(settings,"NumberConstant",Convert.ToInt32(a["count"]));NativeSet(settings,"IsUseTransportDatatype",false);
   NativeSet(settings,"TransmissionTwoWayType",controlled?"DataTransmissionDirectionControl":"DataConflictDecisionWay");NativeSet(settings,"ConflictDecisionWayType",conflict);
   var addresses=new List<object>();
   foreach(var pair in new[]{new object[]{Member(trigger,"TriggerBitAddress"),"trigger_address"},new object[]{Member(item,"SourceAddress"),"source_address"},new object[]{Member(item,"TargetAddress"),"target_address"}}){
    var handle=(Dictionary<string,object>)DescribeOwned(pair[0],scope);
    addresses.Add(ConfigureLocalAddress(Obj("ref",S(handle,"$ref"),"address",a[(string)pair[1]]),view));
   }
   if(controlled){
    var handle=(Dictionary<string,object>)DescribeOwned(Member(settings,"DirectionControlAddress"),scope);
    addresses.Add(ConfigureLocalAddress(Obj("ref",S(handle,"$ref"),"address",a["direction_control_address"]),view));
   }
   var noticeInfo=Member(item,"DataTransmissionNoticeInfo");
   NativeSet(noticeInfo,"IsTriggerMacroBefore",false);NativeSet(noticeInfo,"IsTriggerMacroAfter",false);
   foreach(string key in new[]{"before_bit","after_bit","before_word","after_word"}){
    bool bit=key.EndsWith("_bit");string stage=key.StartsWith("before")?"Before":"After";
    NativeSet(noticeInfo,"IsNotice"+(bit?"Bit":"Word")+"Address"+stage,notices.ContainsKey(key));
    if(!notices.ContainsKey(key))continue;
    var notice=notices[key];var address=Member(noticeInfo,stage+(bit?"Bit":"Word")+"Address");
    if(!bit)NativeSet(address,"DataType","UInt16");
    var handle=(Dictionary<string,object>)DescribeOwned(address,scope);
    addresses.Add(ConfigureLocalAddress(Obj("ref",S(handle,"$ref"),"address",notice["address"]),view));
    NativeSet(noticeInfo,(bit?"IsSetOn":"WriteValue")+stage,bit?notice["value"]:(object)Convert.ToDouble(notice["value"]));
   }
   var collection=Member(Member(page,"DataTransmissionListInfo"),"DataTransmission");int index=((IEnumerable)collection).Cast<object>().Count();
   var group=BeginModelChange(view,"MCP: add page data transmission");
   try{CallNative(collection,"Add",item);CallNative(group,"Commit");}
   catch(Exception error){try{CallNative(group,"Abort");}catch(Exception rollback){throw new AggregateException("Data transmission insertion and rollback failed",error,rollback);}throw;}
   scope.CommittedView=view;
   return Obj("transmission",DescribeOwned(item,view),"index",index,"page_uuid",PageUuid(page).ToString(),"addresses",addresses.ToArray(),"count",Member(settings,"NumberConstant"),"data_type",Member(settings,"DataType").ToString(),"direction",Member(settings,"DirectionType").ToString(),"native_undo_transaction",true,"graphic_inserted",false,"hardware_io",false,"persistence","save_project_to_persist");
  }
  static object RemovePageTransmission(Dictionary<string,object>a) {
   var view=DesignerView();var page=Member(view,"Model");RequireBasicPage(page);
   var item=Ref(a);
   if(item.GetType().FullName!="Flexem.Studio.Components.Parts.DataTransmissionPart.DataTransmissionItemInfo"||!Object.ReferenceEquals(OwningView(S(a,"ref")),view))throw new ArgumentException("Expected a transmission handle from the active page");
   var collection=Member(Member(page,"DataTransmissionListInfo"),"DataTransmission");var before=((IEnumerable)collection).Cast<object>().ToArray();
   int index=Array.FindIndex(before,x=>Object.ReferenceEquals(x,item));
   if(index<0)throw new ArgumentException("Transmission is no longer in the active page collection");
   var group=BeginModelChange(view,"MCP: remove page data transmission");
   try{
    CallNative(collection,"Remove",item);
    var after=((IEnumerable)collection).Cast<object>().ToArray();
    var expected=before.Where(x=>!Object.ReferenceEquals(x,item)).ToArray();
    if(after.Length!=expected.Length||after.Where((x,i)=>!Object.ReferenceEquals(x,expected[i])).Any())throw new InvalidOperationException("Native removal changed unexpected collection entries");
    CallNative(group,"Commit");
   }catch(Exception error){try{CallNative(group,"Abort");}catch(Exception rollback){throw new AggregateException("Transmission removal and rollback failed",error,rollback);}throw;}
   return Obj("removed",true,"index",index,"remaining_count",before.Length-1,"page_uuid",PageUuid(page).ToString(),"native_undo_transaction",true,"persistence","save_project_to_persist","hardware_io",false);
  }
  static object RemovePageTimer(Dictionary<string,object>a) {
   var view=DesignerView();var page=Member(view,"Model");RequireBasicPage(page);
   var item=Ref(a);
   if(item.GetType().FullName!="Flexem.Studio.Components.Parts.TimerPart.TimerItemInfo"||!Object.ReferenceEquals(OwningView(S(a,"ref")),view))throw new ArgumentException("Expected a timer handle from the active page");
   var collection=Member(Member(page,"TimerList"),"Timers");var before=((IEnumerable)collection).Cast<object>().ToArray();
   int index=Array.FindIndex(before,x=>Object.ReferenceEquals(x,item));
   if(index<0)throw new ArgumentException("Timer is no longer in the active page collection");
   var group=BeginModelChange(view,"MCP: remove page timer");
   try{
    CallNative(collection,"Remove",item);
    if(((IEnumerable)collection).Cast<object>().Any(x=>Object.ReferenceEquals(x,item)))throw new InvalidOperationException("Native collection did not remove the requested timer");
    CallNative(group,"Commit");
   }catch(Exception error){try{CallNative(group,"Abort");}catch(Exception rollback){throw new AggregateException("Timer removal and rollback failed",error,rollback);}throw;}
   return Obj("removed",true,"index",index,"remaining_count",((IEnumerable)collection).Cast<object>().Count(),"page_uuid",PageUuid(page).ToString(),"native_undo_transaction",true,"persistence","save_project_to_persist","hardware_io",false);
  }
  static object AddPageTimer(Dictionary<string,object>a) {
   var view=DesignerView();var page=Member(view,"Model");RequireBasicPage(page);
   foreach(string key in new[]{"trigger_address","target_address","cycle"}){
    object value=a[key];if(value==null||value is bool||value is string)throw new ArgumentException(key+" must be an integer");
    double number=Convert.ToDouble(value);double max=key=="cycle"?Int32.MaxValue:UInt32.MaxValue;
    if(Double.IsNaN(number)||Double.IsInfinity(number)||number!=Math.Truncate(number)||number<(key=="cycle"?1:0)||number>max)throw new ArgumentException("Invalid "+key);
   }
   string action=S(a,"action");if(!new[]{"SetON","SetOFF","CycleSwitch"}.Contains(action))throw new ArgumentException("Unsupported timer bit action");
   var item=PageStatic("Flexem.Studio.Components.Parts.TimerPart.TimerItemInfo","CreateAsChild");
   // Configure an unattached child first. It is a page-owned draft, never an
   // IGraphic/TimerTool: the installed menu edits the page's TimerList.
   var scope=new DrawingDraft{Model=item,Page=page};
   var trigger=Member(item,"TimerTriggerAndBreakInfo");var function=Member(item,"TimerFunctionInfo");
   NativeSet(trigger,"TriggerCondition","BitStatus");NativeSet(trigger,"TriggerMode","OffToOn");NativeSet(trigger,"CloseCondition","WindowsClose");NativeSet(trigger,"ExecutionCycle",Convert.ToInt32(a["cycle"]));NativeSet(trigger,"EnableHighSpeedTimer",false);NativeSet(trigger,"AutoReset",false);
   NativeSet(function,"RunMacro",false);NativeSet(function,"StatusSetting",true);NativeSet(function,"SettingBitAndWord","BitSetting");NativeSet(function,"SettingWay",action);NativeSet(function,"IsAudioPlay",false);
   var source=(Dictionary<string,object>)DescribeOwned(Member(trigger,"TriggerBitAddress"),scope);
   var target=(Dictionary<string,object>)DescribeOwned(Member(function,"BitAddress"),scope);
   var sourceAddress=ConfigureLocalAddress(Obj("ref",S(source,"$ref"),"address",a["trigger_address"]),view);
   var targetAddress=ConfigureLocalAddress(Obj("ref",S(target,"$ref"),"address",a["target_address"]),view);
   var collection=Member(Member(page,"TimerList"),"Timers");int index=((IEnumerable)collection).Cast<object>().Count();
   var group=BeginModelChange(view,"MCP: add page timer");
   try{CallNative(collection,"Add",item);CallNative(group,"Commit");}
   catch(Exception error){try{CallNative(group,"Abort");}catch(Exception rollback){throw new AggregateException("Page timer insertion and rollback failed",error,rollback);}throw;}
   scope.CommittedView=view;
   return Obj("timer",DescribeOwned(item,view),"index",index,"page_uuid",PageUuid(page).ToString(),"trigger",sourceAddress,"target",targetAddress,"cycle",Member(trigger,"ExecutionCycle"),"native_cycle_description",Member(item,"ExecutionCycle"),"action",action,"native_undo_transaction",true,"dirty",Member(view,"IsDirty"),"persistence","save_project_to_persist","graphic_inserted",false,"hardware_io",false);
  }

  static object PageStatic(string type,string method,params object[] args) {
   var choices=Native(type).GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m=>m.Name==method&&!m.ContainsGenericParameters&&m.GetParameters().Length==args.Length&&m.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(x=>x)).ToArray();
   if(choices.Length!=1)throw new InvalidOperationException("Native page method is missing or ambiguous: "+type+"."+method);
   return choices[0].Invoke(null,args);
  }
  static object PageServices(){return Member(Member(Resolve("Flexem.Studio.Hmi.Project.IHmiProjectService"),"CurrentProject"),"Services");}
  static object PageWindowService(){return Member(PageServices(),"WindowService");}
  static object[] PageInformation(object manager){return ((IEnumerable)CallNative(manager,"GetAllWindows")).Cast<object>().ToArray();}
  static Guid PageUuid(object settings){return (Guid)Member(settings,"UniqueId");}
  static string PageFile(object settings){var file=Path.GetFullPath(Convert.ToString(Member(settings,"ConfigFileName")));Within(file);return file;}
  static object OpenPageView(Guid uuid) {
   var workbench=Resolve("Flexem.Studio.Workbench.IWorkbench");
   return ((IEnumerable)Member(workbench,"ViewContentCollection")).Cast<object>().FirstOrDefault(view=>view.GetType().FullName=="Flexem.Studio.HMI.Window.HMIWindowViewContent"&&PageUuid(Member(view,"Model"))==uuid);
  }
  static object PageModel(object service,object settings,out object view) {
   Guid uuid=PageUuid(settings);view=OpenPageView(uuid);
   if(view!=null)return Member(view,"Model");
   if((bool)CallNative(service,"IsWindowOpened",uuid))throw new InvalidOperationException("Page is open but its designer model could not be found; refusing to load a stale disk copy");
   return PageStatic(NativeWindowModelType,"Load",PageFile(settings));
  }
  static void RequireBasicPage(object settings) {
   if(Convert.ToString(Member(settings,"Type"))!="Basic")throw new NotSupportedException("Only business Basic pages are supported by this operation");
   int id=Convert.ToInt32(Member(settings,"Id"));
   if(id<0||id>=29000)throw new InvalidOperationException("Reserved or system page ids cannot be changed");
   PageFile(settings);
  }
  static string PageName(Dictionary<string,object>a) {
   string name=S(a,"name").Trim();
   if(name.Length==0||name.Any(Char.IsControl))throw new ArgumentException("Page name must be nonempty and contain no control characters");
   return name;
  }
  static int PageDimension(Dictionary<string,object>a,string name,int fallback) {
   if(!a.ContainsKey(name))return fallback;
   if(a[name] is bool||a[name] is string||a[name]==null)throw new ArgumentException(name+" must be a positive integer");
   double number=Convert.ToDouble(a[name]);
   if(Double.IsNaN(number)||Double.IsInfinity(number)||number<1||number>Int32.MaxValue||number!=Math.Truncate(number))throw new ArgumentException(name+" must be a positive integer");
   return (int)number;
  }
  static Dictionary<string,object> PageDescription(object service,object information) {
   var registered=Member(information,"Settings");var view=OpenPageView(PageUuid(registered));
   var settings=view==null?registered:Member(view,"Model");
   var uuid=PageUuid(settings);
   return Obj("id",Member(settings,"Id"),"uuid",uuid.ToString(),"name",Member(settings,"Name"),"type",Convert.ToString(Member(settings,"Type")),"width",Member(settings,"Width"),"height",Member(settings,"Height"),"file",PageFile(settings),"opened",(bool)CallNative(service,"IsWindowOpened",uuid),"dirty",view==null?false:Member(view,"IsDirty"));
  }
  static string BackupPageFile(string file) {
   Within(file);if(!File.Exists(file))throw new FileNotFoundException("The page file is missing",file);
   string directory=Path.Combine(Path.GetDirectoryName(ProjectFile()),".mcp-backups","pages",DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+"-"+Guid.NewGuid().ToString("N"));
   Within(directory);Directory.CreateDirectory(directory);
   string backup=Path.Combine(directory,Path.GetFileName(file));File.Copy(file,backup,false);return backup;
  }
  static void PublishPageSettings(object manager,object model) {
   var information=CallNative(manager,"GetWindow",PageUuid(model));
   var fixedSettings=Activator.CreateInstance(Native("Flexem.Studio.HMI.Window.WindowSettingsFixed"),new object[]{model});
   NativeSet(information,"Settings",fixedSettings);
  }
  static object DeepCopyPageDto(object source) {
   var dto=CallNative(source,"SaveTo");var helper=Native("Flexem.Studio.Utility.DataContractSerializerHelper");
   var save=helper.GetMethods(BindingFlags.Public|BindingFlags.Static).Single(m=>m.Name=="SaveDto"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==2&&m.GetParameters()[1].ParameterType==typeof(Stream));
   var load=helper.GetMethods(BindingFlags.Public|BindingFlags.Static).Single(m=>m.Name=="LoadDto"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType==typeof(Stream));
   using(var stream=new MemoryStream()){
    save.MakeGenericMethod(dto.GetType()).Invoke(null,new object[]{dto,stream});
    using(var read=new MemoryStream(stream.ToArray()))return load.MakeGenericMethod(dto.GetType()).Invoke(null,new object[]{read});
   }
  }
  static void BackupOpenPageModel(object model,string backup) {
   // DeleteWindowB auto-saves before closing: preserve the current model as well as the old disk file.
   Within(backup);File.Copy(backup,backup+".disk-original",false);
   var dto=CallNative(model,"SaveTo");var helper=Native("Flexem.Studio.Utility.DataContractSerializerHelper");
   var save=helper.GetMethods(BindingFlags.Public|BindingFlags.Static).Single(m=>m.Name=="SaveDto"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==2&&m.GetParameters()[1].ParameterType==typeof(Stream));
   using(var stream=new FileStream(backup,FileMode.Create,FileAccess.Write))save.MakeGenericMethod(dto.GetType()).Invoke(null,new object[]{dto,stream});
  }
  static void RefreshCopiedPageIdentities(object node,List<Dictionary<string,object>> identities) {
   var old=(Guid)Member(node,"UniqueId");var fresh=Guid.NewGuid();NativeSet(node,"UniqueId",fresh);
   identities.Add(Obj("old_uuid",old.ToString(),"new_uuid",fresh.ToString(),"type",node.GetType().FullName));
   var childrenProperty=node.GetType().GetProperty("Graphicses",BindingFlags.Public|BindingFlags.Instance);
   if(childrenProperty==null)return;
   var children=childrenProperty.GetValue(node,null) as IEnumerable;
   if(children!=null)foreach(var child in children)RefreshCopiedPageIdentities(child,identities);
  }
  static object RegisterBasicPage(object service,object manager,object model,List<Dictionary<string,object>> identities) {
   RequireBasicPage(model);string file=PageFile(model);Guid uuid=PageUuid(model);
   if(File.Exists(file)||(bool)CallNative(manager,"Exists",uuid))throw new IOException("Refusing to overwrite an existing page: "+file);
   try{
    CallNative(model,"SaveToFile");
    PageStatic(NativeWindowServiceType,"CopyWindow",model,false);
   }catch(Exception e){
    // Only remove the newly created orphan; never roll back a page already registered by native code.
    if(!(bool)CallNative(manager,"Exists",uuid)&&File.Exists(file))File.Delete(file);
    throw new InvalidOperationException("Native page creation did not finish. Page path: "+file,e);
   }
   if(!(bool)CallNative(manager,"Exists",uuid)||!File.Exists(file))throw new InvalidOperationException("Native page registration/file verification failed: "+file);
   var result=Obj("page",PageDescription(service,CallNative(manager,"GetWindow",uuid)),"saved",true,"opened_automatically",false,"supported_mutation_kinds",new[]{"Basic"},"execution_mode",ExecutionMode);
   if(identities!=null){result["refreshed_identities"]=identities.ToArray();result["external_resource_address_and_page_references"]="preserved";}
   return result;
  }

  static object NativePageOperation(Dictionary<string,object>a) {
   RequireProject();string op=S(a,"op");var service=PageWindowService();var manager=Member(service,"WindowManager");
   if(op=="page_timer_add")return AddPageTimer(a);
   if(op=="page_transmission_add")return AddPageTransmission(a);
   if(op=="page_transmission_remove")return RemovePageTransmission(a);
   if(op=="page_timer_remove")return RemovePageTimer(a);
   if(op=="pages_list")return Obj("project",ProjectFile(),"pages",PageInformation(manager).Select(info=>PageDescription(service,info)).OrderBy(p=>Convert.ToString(p["type"])).ThenBy(p=>Convert.ToInt32(p["id"])).ToArray(),"supported_mutation_kinds",new[]{"Basic"},"execution_mode",ExecutionMode);
   if(op=="page_create"){
    if(a.ContainsKey("kind")&&S(a,"kind")!="Basic")throw new NotSupportedException("Only kind Basic is supported for background page creation");
    string name=PageName(a);var basic=Enum.Parse(Native("Flexem.Studio.Hmi.Window.WindowType"),"Basic");
    int id=Convert.ToInt32(PageStatic(NativeWindowServiceType,"GetVacantWindowId",basic));
    if(id<0||id>=29000)throw new InvalidOperationException("No non-reserved Basic page id is available");
    string directory=Path.GetFullPath(Convert.ToString(PageStatic(NativeWindowServiceType,"GetConfigDir",basic)));Within(directory);
    var model=PageStatic(NativeWindowModelType,"CreateAsChild",basic,name,id,directory);
    NativeSet(model,"UseCustomDescription",true);NativeSet(model,"Name",name);
    int width=PageDimension(a,"width",Convert.ToInt32(Member(model,"Width"))),height=PageDimension(a,"height",Convert.ToInt32(Member(model,"Height")));
    if(width>Convert.ToInt32(Member(model,"MaxWidthValue"))||height>Convert.ToInt32(Member(model,"MaxHeightValue")))throw new ArgumentException("Page size exceeds the native model bounds");
    NativeSet(model,"Width",width);NativeSet(model,"Height",height);
    return RegisterBasicPage(service,manager,model,null);
   }
   if(op=="page_add_language"){
    string culture=S(a,"culture");
    var languageService=Member(PageServices(),"LanguageConfigurationService");
    object firstFont=null;
    foreach(var c in (IEnumerable)Member(languageService,"Configurations")){
     if(firstFont==null)firstFont=Member(c,"FontInfo");
     var existing=Member(Member(c,"CultureInfo"),"Name");
     if(existing!=null&&Convert.ToString(existing).Equals(culture,StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("Language already exists: "+culture);
    }
    if(firstFont==null)throw new InvalidOperationException("No existing language font to reuse");
    var cultureInfo=new System.Globalization.CultureInfo(culture);
    var addMethod=languageService.GetType().GetMethod("AddItem",BindingFlags.Public|BindingFlags.Instance);
    if(addMethod==null)throw new InvalidOperationException("AddItem not found on LanguageConfigurationService");
    var added=addMethod.Invoke(languageService,new[]{cultureInfo,firstFont});
    return Obj("culture",culture,"added",added,"items_after",((IEnumerable)CallNative(languageService,"GetLanguageItems")).Cast<object>().Count());
   }
   if(op=="page_sampling_list"){
    object dsCfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"DataSampleConfigInfo");
    var groups=(IEnumerable)Member(dsCfg,"DataSampleInfos");
    var list=new List<object>();
    foreach(var g in groups){
     var props=Member(g,"PropertyInfo");
     list.Add(Obj("sample_id",Member(g,"Id").ToString(),"name",Member(props,"Description"),"cycle_ms",Member(props,"DataSampleCycle")));
    }
    return Obj("groups",list.ToArray());
   }
   if(op=="page_sampling_remove"){
    string id=S(a,"sample_id");
    object dsCfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"DataSampleConfigInfo");
    var groups=Member(dsCfg,"DataSampleInfos");
    object target=null;
    foreach(var g in (IEnumerable)groups){if(string.Equals(Member(g,"Id").ToString(),id,StringComparison.OrdinalIgnoreCase)){target=g;break;}}
    if(target==null)throw new ArgumentException("Sampling group not found: "+id);
    string fileName=Path.GetFullPath(Convert.ToString(Member(dsCfg,"FileName")));
    CallNative(groups,"Remove",target);
    CallNative(dsCfg,"SaveToFile",fileName);
    return Obj("removed",id,"remaining",((IEnumerable)Member(dsCfg,"DataSampleInfos")).Cast<object>().Count());
   }
   if(op=="page_sampling_update"){
    string id=S(a,"sample_id");
    object dsCfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"DataSampleConfigInfo");
    var groups=Member(dsCfg,"DataSampleInfos");
    object target=null;
    foreach(var g in (IEnumerable)groups){if(string.Equals(Member(g,"Id").ToString(),id,StringComparison.OrdinalIgnoreCase)){target=g;break;}}
    if(target==null)throw new ArgumentException("Sampling group not found: "+id);
    var props=Member(target,"PropertyInfo");
    if(a.ContainsKey("name"))NativeSet(props,"Description",S(a,"name"));
    if(a.ContainsKey("cycle_ms"))NativeSet(props,"DataSampleCycle",Convert.ToUInt32(a["cycle_ms"]));
    string fileName=Path.GetFullPath(Convert.ToString(Member(dsCfg,"FileName")));
    CallNative(dsCfg,"SaveToFile",fileName);
    return Obj("updated",id,"name",Member(props,"Description"),"cycle_ms",Member(props,"DataSampleCycle"));
   }
   if(op=="page_add_monitor_register"){
    string alias=S(a,"alias");
    object cfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"MonitoringRegisterConfigInfo");
    var collection=Member(cfg,"MonitoringRegisters");
    var item=CallNative(collection,"AddNew");
    NativeSet(item,"Alias",alias);
    if(a.ContainsKey("id"))NativeSet(item,"ID",Convert.ToInt32(a["id"]));
    var addr=Member(item,"Address");
    NativeSet(addr,"DataType","UInt16");
    var addrFactory=PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateWord",Member(PageServices(),"DeviceService"),Member(addr,"DataType"),true);
    var addrRaw=Member(addrFactory,"Raw");
    NativeSet(addr,"DeviceId",Member(addrRaw,"DeviceId"));
    NativeSet(addr,"RegisterId",Member(Member(addrRaw,"RegisterId"),"Id"));
    NativeSet(addr,"MainAddress",Convert.ToUInt32(a.ContainsKey("address")?a["address"]:7100));
    string mrFile=Path.GetFullPath(Convert.ToString(Member(cfg,"FileName")));
    CallNative(cfg,"SaveToFile",mrFile);
    return Obj("alias",alias,"address",Member(addr,"MainAddress"),"count_after",((IEnumerable)Member(cfg,"MonitoringRegisters")).Cast<object>().Count(),"file",mrFile);
   }
   if(op=="page_set_extended_setting"){
    object cfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"ExtendedPropertyInfo");
    var changed=new List<string>();
    if(a.ContainsKey("audit_trail")){NativeSet(cfg,"IsEnableAuditTrail",Convert.ToBoolean(a["audit_trail"]));changed.Add("IsEnableAuditTrail");}
    if(a.ContainsKey("record_login_success")){NativeSet(cfg,"IsRecordUserLoginSuccess",Convert.ToBoolean(a["record_login_success"]));changed.Add("IsRecordUserLoginSuccess");}
    if(a.ContainsKey("record_login_failed")){NativeSet(cfg,"IsRecordUserLoginFailed",Convert.ToBoolean(a["record_login_failed"]));changed.Add("IsRecordUserLoginFailed");}
    if(a.ContainsKey("jpeg_quality")){NativeSet(cfg,"JpegQuality",Convert.ToInt32(a["jpeg_quality"]));changed.Add("JpegQuality");}
    if(changed.Count==0)throw new ArgumentException("No supported settings provided");
    string extFile=Path.GetFullPath(Convert.ToString(Member(cfg,"FileName")));
    CallNative(cfg,"SaveToFile",extFile);
    return Obj("changed",changed.ToArray(),"file",extFile);
   }
   if(op=="page_configure_switch_action"){
    object model;
    if(a.ContainsKey("draft_id")){
     string draftKey=S(a,"draft_id");
     if(!drawingDrafts.ContainsKey(draftKey))throw new ArgumentException("Unknown or expired draft: "+draftKey);
     model=drawingDrafts[draftKey].Model;
    }else{
     model=Ref(a);
    }
    if(model.GetType().FullName!="Flexem.Studio.Components.Parts.SwitchPart.SwitchInfo")throw new ArgumentException("Expected a native SwitchInfo model");
    string actionType=S(a,"action_type");
    var swSettings=Member(model,"SwitchSettings");
    var actions=Member(swSettings,"ActionList");
    if(actions==null)throw new InvalidOperationException("Switch action list is not initialized");
    if(((IEnumerable)actions).Cast<object>().Any())throw new InvalidOperationException("This initializer requires an empty action list");
    string dataTypeName;
    switch(actionType){
     case "DataTransfer":dataTypeName="Flexem.Studio.Hmi.Models.Components.Parts.SwitchNew.DataTransferSwitchActions.DataTransferSwitchActionData";break;
     case "RecipeTransfer":dataTypeName="Flexem.Studio.Hmi.Models.Components.Parts.SwitchNew.RecipeTransferSwitchActions.RecipeTransferSwitchActionData";break;
     case "Print":dataTypeName="Flexem.Studio.Hmi.Models.Components.Parts.SwitchNew.FunctionKeyActions.PrintFuncActionData";break;
     case "WindowOper":dataTypeName="Flexem.Studio.Hmi.Models.Components.Parts.SwitchNew.WindowOperSwitchActions.WindowOperSwitchActionData";break;
     case "Word":dataTypeName="Flexem.Studio.Hmi.Models.Components.Parts.SwitchNew.WordSwitchActions.WordSwitchActionData";break;
     default:throw new ArgumentException("Unsupported action_type: "+actionType);
    }
    var action=Activator.CreateInstance(Native(dataTypeName));
    var actionName=action.GetType().FullName;
    NativeSet(action,"ExecutionCondition","Action");
    NativeSet(action,"Action","KeyDown");
    var capForName=Member(action,"TransferCapacity");
    if(capForName!=null){NativeSet(capForName,"Type","Constant");NativeSet(capForName,"Constant",1);NativeSet(capForName,"Enable",true);}
    var devicesSvc=Member(PageServices(),"DeviceService");
    if(actionType=="DataTransfer"){
     NativeSet(action,"Function","DataTransfer");
     var dtEnum=Enum.Parse(Native("Flexem.Studio.Hmi.DataType"),"UInt16");
     var srcFactory=PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateWord",devicesSvc,dtEnum,true);
     NativeSet(srcFactory,"DataType","UInt16");
     NativeSet(Member(srcFactory,"Raw"),"MainAddress",Convert.ToUInt32(a.ContainsKey("source_address")?a["source_address"]:900));
     NativeSet(action,"SourceAddress",srcFactory);
     var dstFactory=PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateWord",devicesSvc,dtEnum,true);
     NativeSet(dstFactory,"DataType","UInt16");
     NativeSet(Member(dstFactory,"Raw"),"MainAddress",Convert.ToUInt32(a.ContainsKey("target_address")?a["target_address"]:920));
     NativeSet(action,"TargetAddress",dstFactory);
    }else if(actionType=="RecipeTransfer"){
     NativeSet(action,"Function","FormulaTransfer");
     var regDtEnum=Enum.Parse(Native("Flexem.Studio.Hmi.DataType"),"UInt16");
     var regFactory=PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateWord",devicesSvc,regDtEnum,true);
     NativeSet(regFactory,"DataType","UInt16");
     NativeSet(Member(regFactory,"Raw"),"MainAddress",Convert.ToUInt32(a.ContainsKey("source_address")?a["source_address"]:930));
     NativeSet(action,"Address",regFactory);
    }
    NativeSet(swSettings,"UseSwitch",true);
    CallNative(actions,"Add",action);
    return Obj("action",DescribeOwned(action,null),"action_type",actionType,"class",actionName,"action_count",((IEnumerable)actions).Cast<object>().Count());
   }
   if(op=="page_add_plc_control"){
    string controlType=S(a,"control_type");
    if(!new[]{"SwitchBasicWindow","ReportCurWindowID","BGLightControl","ProcessMacro","SoundControl","PrintScreen","ForceBuzzer","CaptureScreen"}.Contains(controlType))
     throw new ArgumentException("Unsupported control_type: "+controlType);
    object cfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"PLCControlConfigInfo");
    var collection=Member(cfg,"PLCControlInfos");
    foreach(var existing in (IEnumerable)collection){
     if(string.Equals(Convert.ToString(Member(existing,"ControlType")),controlType,StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("A PLC control of this type already exists: "+controlType);
    }
    var item=CallNative(collection,"AddNew");
    NativeSet(item,"ControlType",controlType);
    if(a.ContainsKey("index"))NativeSet(item,"Index",Convert.ToByte(a["index"]));
    if(a.ContainsKey("effective_window")){
     Guid g;if(!Guid.TryParse(S(a,"effective_window"),out g))throw new ArgumentException("Invalid effective_window");
     NativeSet(item,"EffectiveWindowsId",g);
    }
    // 触发地址为编译必需（TriggerAddress 只读，但其对象成员可写）
    var trigger=Member(item,"TriggerAddress");
    var addrFactory=PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateBit",Member(PageServices(),"DeviceService"));
    var addrRaw=Member(addrFactory,"Raw");
    NativeSet(trigger,"DeviceId",Member(addrRaw,"DeviceId"));
    NativeSet(trigger,"RegisterId",Member(Member(addrRaw,"RegisterId"),"Id"));
    NativeSet(trigger,"MainAddress",Convert.ToUInt32(a.ContainsKey("trigger_address")?a["trigger_address"]:700100));
    string plcFile=Path.GetFullPath(Convert.ToString(Member(cfg,"FileName")));
    CallNative(cfg,"SaveToFile",plcFile);
    return Obj("control_type",controlType,"trigger_address",Member(trigger,"MainAddress"),"count_after",((IEnumerable)Member(cfg,"PLCControlInfos")).Cast<object>().Count(),"file",plcFile);
   }
   if(op=="page_set_home_window"){
    string homeUuid=S(a,"uuid");
    Guid homeId;if(!Guid.TryParse(homeUuid,out homeId))throw new ArgumentException("Invalid uuid");
    object cfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"GlobalConfigInfo");
    NativeSet(cfg,"HomeWindowId",homeId);
    if(a.ContainsKey("init_window")&&Convert.ToBoolean(a["init_window"]))NativeSet(cfg,"InitWindow",homeId);
    string cfgFile=Path.GetFullPath(Convert.ToString(Member(cfg,"FileName")));
    CallNative(cfg,"SaveToFile",cfgFile);
    return Obj("home_window",homeUuid,"init_window",Member(cfg,"InitWindow"),"file",cfgFile);
   }
   if(op=="page_set_global_setting"){
    object cfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"GlobalConfigInfo");
    var changed=new List<string>();
    if(a.ContainsKey("show_network_icon")){NativeSet(cfg,"ShowNetworkIcon",Convert.ToBoolean(a["show_network_icon"]));changed.Add("ShowNetworkIcon");}
    if(a.ContainsKey("backlit_enabled")){NativeSet(cfg,"BacklitTurnDownIsEnabled",Convert.ToBoolean(a["backlit_enabled"]));changed.Add("BacklitTurnDownIsEnabled");}
    if(a.ContainsKey("backlit_time")){NativeSet(cfg,"BacklitTurnDownTime",Convert.ToInt32(a["backlit_time"]));changed.Add("BacklitTurnDownTime");}
    if(a.ContainsKey("backlit_ratio")){NativeSet(cfg,"BacklitTurnDownRatio",Convert.ToInt32(a["backlit_ratio"]));changed.Add("BacklitTurnDownRatio");}
    if(changed.Count==0)throw new ArgumentException("No supported settings provided");
    string cfgFile=Path.GetFullPath(Convert.ToString(Member(cfg,"FileName")));
    CallNative(cfg,"SaveToFile",cfgFile);
    return Obj("changed",changed.ToArray(),"file",cfgFile);
   }
   if(op=="page_add_user_permission"){
    string name=S(a,"name");
    string userName=name.Trim();
    if(userName.Length==0)throw new ArgumentException("name must not be empty");
    object cfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"UserPermissionConfigInfo");
    var collection=Member(cfg,"UserPermissionInfos");
    foreach(var existing in (IEnumerable)collection){
     if(string.Equals(Convert.ToString(Member(existing,"Name")),userName,StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("User already exists: "+userName);
    }
    var item=CallNative(collection,"AddNew");
    NativeSet(item,"Name",userName);
    if(a.ContainsKey("password"))NativeSet(item,"Password",S(a,"password"));
    if(a.ContainsKey("logout_time"))NativeSet(item,"LogoutTime",Convert.ToInt32(a["logout_time"]));
    if(a.ContainsKey("full_permission"))NativeSet(item,"IsFullPermission",Convert.ToBoolean(a["full_permission"]));
    // 权限字段必须完整，否则用户权限浏览控件编译报 source null
    string permText=a.ContainsKey("permission_text")?S(a,"permission_text"):"基本权限";
    NativeSet(item,"PermissionDetail","1:"+permText);
    NativeSet(item,"IsFullPermission",a.ContainsKey("full_permission")?Convert.ToBoolean(a["full_permission"]):true);
    foreach(var sibling in (IEnumerable)collection){
     var siblingDescs=Member(sibling,"PermissionDescriptions");
     if(siblingDescs!=null){NativeSet(item,"PermissionDescriptions",siblingDescs);break;}
    }
    string permFile=Path.GetFullPath(Convert.ToString(Member(cfg,"FileName")));
    CallNative(cfg,"SaveToFile",permFile);
    return Obj("user",userName,"count_after",((IEnumerable)Member(cfg,"UserPermissionInfos")).Cast<object>().Count(),"file",permFile);
   }
   if(op=="page_add_plan_task"){
    string description=S(a,"description");
    object cfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"PlanTaskConfigInfo");
    var collection=Member(cfg,"PlanTaskInfos");
    var item=CallNative(collection,"AddNew");
    NativeSet(item,"Description",description);
    if(a.ContainsKey("enable"))NativeSet(item,"IsEnableControl",Convert.ToBoolean(a["enable"]));
    if(a.ContainsKey("days"))NativeSet(item,"StartDays",Convert.ToByte(a["days"]));
    // 触发地址为编译必需：默认用本地字地址对（Start/Enable）
    // 计划任务触发用位地址
    var startAddress=Member(item,"StartAddress");
    NativeSet(startAddress,"DataType","Bool");
    var startNative=PageStatic("Flexem.Studio.Hmi.Models.Address.AddressData","CreateBit",Member(PageServices(),"DeviceService"));
    var startRaw=Member(startNative,"Raw");
    NativeSet(startAddress,"DeviceId",Member(startRaw,"DeviceId"));
    NativeSet(startAddress,"RegisterId",Member(Member(startRaw,"RegisterId"),"Id"));
    NativeSet(startAddress,"MainAddress",Convert.ToUInt32(a.ContainsKey("start_address")?a["start_address"]:700000));
    var enableAddress=Member(item,"EnableAddress");
    NativeSet(enableAddress,"DataType","Bool");
    NativeSet(enableAddress,"DeviceId",Member(startRaw,"DeviceId"));
    NativeSet(enableAddress,"RegisterId",Member(Member(startRaw,"RegisterId"),"Id"));
    NativeSet(enableAddress,"MainAddress",Convert.ToUInt32(a.ContainsKey("enable_address")?a["enable_address"]:700001));
    string levelFile=Path.GetFullPath(Convert.ToString(Member(cfg,"FileName")));
    CallNative(cfg,"SaveToFile",levelFile);
    return Obj("description",description,"start_address",Member(startAddress,"MainAddress"),"enable_address",Member(enableAddress,"MainAddress"),"count_after",((IEnumerable)Member(cfg,"PlanTaskInfos")).Cast<object>().Count(),"file",levelFile);
   }
   if(op=="page_add_user_level"){
    int level=Convert.ToInt32(a["level"]);
    if(level<1||level>255)throw new ArgumentException("level must be 1..255");
    var cfg=Member(Member(CurrentProject(),"HMIConfigInfo"),"UserLevelConfigInfo");
    var collection=Member(cfg,"UserLevelInfos");
    foreach(var existing in (IEnumerable)collection){
     if(Convert.ToInt32(Member(existing,"Level"))==level)throw new ArgumentException("User level already exists: "+level);
    }
    var created=CallNative(collection,"AddUserLevelInfo",level);
    if(a.ContainsKey("password"))NativeSet(created,"Password",S(a,"password"));
    if(a.ContainsKey("description"))NativeSet(created,"Description",S(a,"description"));
    string levelFile=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProjectFile()),"HMI","System","UserLevelConfigInfo.cfg"));
    CallNative(cfg,"SaveToFile",levelFile);
    return Obj("level",level,"description",Member(created,"Description"),"password_set",a.ContainsKey("password"),"count_after",((IEnumerable)Member(cfg,"UserLevelInfos")).Cast<object>().Count(),"file",levelFile);
   }
   if(op!="page_open"&&op!="page_rename"&&op!="page_copy"&&op!="page_delete")throw new ArgumentException("Unknown native page operation: "+op);
   Guid uuid;if(!Guid.TryParse(S(a,"uuid"),out uuid))throw new ArgumentException("uuid must be a page UUID returned by pages_list");
   var information=CallNative(manager,"GetWindow",uuid);var settings=Member(information,"Settings");RequireBasicPage(settings);
   if(op=="page_open"){
    CallNative(service,"OpenOrActiveWindow",uuid,false);
    if(!(bool)CallNative(service,"IsWindowOpened",uuid))throw new InvalidOperationException("Native service did not open the requested page");
    return Obj("page",PageDescription(service,information),"show_settings",false,"execution_mode",ExecutionMode);
   }
   if(op=="page_delete")return DeleteBasicPage(service,manager,information);
   object view;var source=PageModel(service,settings,out view);string newName=PageName(a);
   if(op=="page_copy"){
    var dto=DeepCopyPageDto(source);var identities=new List<Dictionary<string,object>>();RefreshCopiedPageIdentities(dto,identities);
    var basic=Enum.Parse(Native("Flexem.Studio.Hmi.Window.WindowType"),"Basic");
    int id=Convert.ToInt32(PageStatic(NativeWindowServiceType,"GetVacantWindowId",basic));
    if(id<0||id>=29000)throw new InvalidOperationException("No non-reserved Basic page id is available");
    NativeSet(dto,"Id",id);NativeSet(dto,"Name",newName);NativeSet(dto,"UseCustomDescription",true);
    string directory=Path.GetFullPath(Convert.ToString(PageStatic(NativeWindowServiceType,"GetConfigDir",basic)));Within(directory);
    var copy=PageStatic(NativeWindowModelType,"FetchAsChild",dto,directory);
    NativeSet(copy,"Id",id);NativeSet(copy,"Name",newName);NativeSet(copy,"UseCustomDescription",true);
    return RegisterBasicPage(service,manager,copy,identities);
   }
   if(view!=null){
    var transaction=BeginModelChange(view,"MCP: rename page");
    try{NativeSet(source,"UseCustomDescription",true);NativeSet(source,"Name",newName);CallNative(transaction,"Commit");}
    catch{CallNative(transaction,"Abort");throw;}
    return Obj("page",PageDescription(service,information),"saved",false,"native_undo_transaction",true,"persistence","save_project_to_persist","execution_mode",ExecutionMode);
   }
   string file=PageFile(source),backup=BackupPageFile(file);
   try{NativeSet(source,"UseCustomDescription",true);NativeSet(source,"Name",newName);CallNative(source,"SaveToFile");PublishPageSettings(manager,source);}
   catch(Exception e){File.Copy(backup,file,true);NativeSet(information,"Settings",settings);throw new InvalidOperationException("Closed-page rename failed; restored page file from "+backup,e);}
   return Obj("page",PageDescription(service,information),"saved",true,"backup",backup,"native_undo_transaction",false,"execution_mode",ExecutionMode);
  }

  static object DeleteBasicPage(object service,object manager,object information) {
   var settings=Member(information,"Settings");RequireBasicPage(settings);Guid uuid=PageUuid(settings);
   var businessPages=PageInformation(manager).Select(info=>Member(info,"Settings")).Where(p=>Convert.ToString(Member(p,"Type"))=="Basic"&&Convert.ToInt32(Member(p,"Id"))>=0&&Convert.ToInt32(Member(p,"Id"))<29000).ToArray();
   if(businessPages.Length<=1)throw new InvalidOperationException("Cannot delete the last business Basic page");
   var global=Member(PageServices(),"GlobalConfigurationService");
   if(global==null)throw new InvalidOperationException("Cannot verify protected startup/home pages");
   foreach(var property in new[]{"InitWindow","HomeWindowId"})if((Guid)Member(global,property)==uuid)throw new InvalidOperationException("Cannot delete the configured "+property+" page");
   if((bool)Member(global,"ScreensaverIsEnabled")&&(Guid)Member(global,"ScreensaverWindow")==uuid)throw new InvalidOperationException("Cannot delete the active screensaver page");
   foreach(var property in new[]{"PublicWindow","DropDownWindow","RightDownWindow"}){
    var protectedPage=Member(manager,property);
    if(protectedPage!=null&&PageUuid(Member(protectedPage,"Settings"))==uuid)throw new InvalidOperationException("Cannot delete the protected "+property+" page");
   }
   string file=PageFile(settings),backup=BackupPageFile(file);
   var openView=OpenPageView(uuid);if(openView!=null)BackupOpenPageModel(Member(openView,"Model"),backup);
   bool deleted=(bool)PageStatic(NativeWindowServiceType,"DeleteWindowB",settings);
   if(!deleted||(bool)CallNative(manager,"Exists",uuid)||File.Exists(file))throw new InvalidOperationException("Native page deletion was not confirmed; backup retained at "+backup);
   return Obj("deleted",true,"uuid",uuid.ToString(),"id",Member(settings,"Id"),"file",file,"backup",backup,"backup_includes_open_model",openView!=null,"native_confirmation_ui",false,"execution_mode",ExecutionMode);
  }
 }
}
