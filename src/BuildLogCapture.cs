using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FStudioMcp {
 // Read only the bounded interval appended during a build. The application log
 // is shared by native processes: temporal proximity does not prove ownership.
 internal sealed class BuildLogCapture {
  readonly string path;
  long offset,creation;
  string startError;
  const int Limit=262144;
  internal BuildLogCapture(string file) {
   path=file;
   try { var info=new FileInfo(path);if(info.Exists){offset=info.Length;creation=info.CreationTimeUtc.Ticks;} }
   catch(Exception e){startError=e.Message;}
  }
  internal Dictionary<string,object> Read() {
   var result=new Dictionary<string,object>{
    {"source","native_log_interval"},{"path",path},{"captured_utc",DateTime.UtcNow.ToString("o")},
    {"attribution","Shared application log; interval entries are not proven exclusive to this build or process."},
    {"start_offset",offset},{"text",""},{"error",startError},{"truncated",false}
   };
   if(startError!=null)return result;
   try {
    var info=new FileInfo(path);
    if(!info.Exists){result["status"]="missing";return result;}
    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
     long end=stream.Length;
     // Do not return pre-existing contents after rotation/truncation.
     if((creation!=0&&info.CreationTimeUtc.Ticks!=creation)||end<offset){result["status"]="rotated_or_truncated";return result;}
     result["end_offset"]=end;
     long count=end-offset;
     result["truncated"]=count>Limit;
     stream.Position=offset;
     byte[] bytes=new byte[(int)Math.Min(count,Limit)];int read=0,n;
     while(read<bytes.Length&&(n=stream.Read(bytes,read,bytes.Length-read))>0)read+=n;
     // Installed FStudio logging uses the Windows system ANSI code page.
     result["encoding"]=Encoding.Default.WebName;
     result["text"]=Encoding.Default.GetString(bytes,0,read);
     result["status"]="captured";
    }
   }catch(Exception e){result["status"]="unavailable";result["error"]=e.Message;}
   return result;
  }
 }
}
