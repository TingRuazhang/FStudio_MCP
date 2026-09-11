using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;

internal static partial class Bridge
{
    static object CloneWidget(Dictionary<string, object> a) {
        var source = Read(S(a,"source")); var target = Read(S(a,"input"));
        var originals = ((System.Collections.IList)Child(source,"Graphicses")).Cast<object>().Where(g => Convert.ToString(Value(g,"UniqueId")) == S(a,"widget_id")).ToArray();
        if (originals.Length != 1) throw new ArgumentException("Expected one source widget UUID");
        var widget = originals[0];
        if (Value(widget,"Graphicses") is System.Collections.IList) throw new ArgumentException("Nested widget groups require explicit reference remapping and are not supported by clone");
        var widgets = (System.Collections.IList)Child(target,"Graphicses");
        if (widgets.Cast<object>().Any(g => Convert.ToString(Value(g,"GraphicsName")) == S(a,"name"))) throw new ArgumentException("Target widget name already exists");
        var z = widgets.Cast<object>().Select(g => Convert.ToInt32(Value(g,"ZIndex"))).DefaultIfEmpty(0).Max()+1;
        Set(widget,"UniqueId",Guid.NewGuid()); Set(widget,"GraphicsName",S(a,"name")); Set(widget,"ZIndex",z);
        if (widget.GetType().GetProperty("ComponentId") != null) Set(widget,"ComponentId",widgets.Cast<object>().Select(g => Convert.ToInt32(Value(g,"ComponentId"))).DefaultIfEmpty(0).Max()+1);
        widgets.Add(widget); Write(S(a,"output"),target);
        var reread = Read(S(a,"output"));
        if (json.Serialize(Summarize(target)) != json.Serialize(Summarize(reread))) throw new InvalidDataException("Clone native roundtrip mismatch");
        return Summarize(reread);
    }
    static CustomAttributeData Contract(MemberInfo t, string name) {
        return CustomAttributeData.GetCustomAttributes(t).FirstOrDefault(x => x.Constructor.DeclaringType.Name == name);
    }
    static object Attr(CustomAttributeData a, string name, object fallback) {
        if (a == null) return fallback;
        foreach (var n in a.NamedArguments) if (n.MemberInfo.Name == name) return n.TypedValue.Value;
        return fallback;
    }
    static Type[] NativeTypes() { return dtos.GetTypes().Where(t => t.IsPublic && (t.IsEnum || Contract(t, "DataContractAttribute") != null)).ToArray(); }
    static object TypeEntry(Type t) {
        var c = Contract(t, "DataContractAttribute");
        return Obj("type", t.FullName, "base_type", t.BaseType == null ? null : t.BaseType.FullName,
            "abstract", t.IsAbstract, "enum", t.IsEnum, "xml_name", Attr(c, "Name", t.Name),
            "xml_namespace", Attr(c, "Namespace", "http://schemas.datacontract.org/2004/07/" + t.Namespace));
    }
    static object Catalog(Dictionary<string, object> a) {
        string filter = S(a, "query", "");
        var matches = NativeTypes().Where(t => t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(t => t.FullName).ToArray();
        return Obj("total", matches.Length, "types", matches.Skip((int)N(a,"offset")).Take((int)N(a,"limit",100)).Select(TypeEntry).ToArray(), "status", "discovered_metadata_not_behavior_verified");
    }
    static object TypeSchema(Dictionary<string, object> a) {
        var t = NativeTypes().Single(x => x.FullName == S(a,"type"));
        return Obj("definition", TypeEntry(t), "enum_values", t.IsEnum ? Enum.GetNames(t) : null,
            "properties", t.GetProperties().Where(p => Contract(p,"DataMemberAttribute") != null).Select(p => {
                var c = Contract(p,"DataMemberAttribute"); var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                return Obj("property", p.Name, "type", p.PropertyType.FullName, "xml_name", Attr(c,"Name",p.Name), "order", Attr(c,"Order",-1),
                    "required", Attr(c,"IsRequired",false), "enum_values", pt.IsEnum ? Enum.GetNames(pt) : null);
            }).ToArray());
    }
    static object Document(Dictionary<string, object> a) {
        string input = S(a,"input"); string name, ns;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using (var reader = XmlReader.Create(input, settings)) { reader.MoveToContent(); name = reader.LocalName; ns = reader.NamespaceURI; }
        var matches = NativeTypes().Where(t => !t.IsEnum && !t.IsAbstract && (string)Attr(Contract(t,"DataContractAttribute"),"Name",t.Name) == name
            && (string)Attr(Contract(t,"DataContractAttribute"),"Namespace","http://schemas.datacontract.org/2004/07/"+t.Namespace) == ns).ToArray();
        if (matches.Length != 1) throw new ArgumentException("Root has no unique supported native DTO: " + name + " (" + matches.Length + ")");
        var type = matches[0]; object dto;
        using (var stream = File.OpenRead(input)) dto = load.MakeGenericMethod(type).Invoke(null,new object[]{stream});
        if (dto == null) throw new InvalidDataException("Native document is null");
        if (a.ContainsKey("output")) {
            using (var stream = new FileStream(S(a,"output"),FileMode.CreateNew,FileAccess.Write)) save.MakeGenericMethod(type).Invoke(null,new object[]{dto,stream});
            using (var stream = File.OpenRead(S(a,"output"))) if (load.MakeGenericMethod(type).Invoke(null,new object[]{stream}) == null) throw new InvalidDataException("Native reread failed");
        }
        return Obj("native_type",type.FullName,"xml_root",name,"native_deserialization_verified",true);
    }
}
