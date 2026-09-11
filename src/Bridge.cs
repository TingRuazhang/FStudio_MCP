using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

internal static partial class Bridge
{
    static Assembly dtos;
    static Type helper, windowType;
    static MethodInfo load, save;
    static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16000000, RecursionLimit = 100 };
    static Dictionary<string, object> Obj(params object[] kv)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }
    static object Value(object o, string n)
    {
        if (o == null) return null;
        var p = o.GetType().GetProperty(n); return p == null ? null : p.GetValue(o, null);
    }
    static object PathValue(object o, string path)
    {
        foreach (var n in path.Split('.')) o = Value(o, n); return o;
    }
    static void Set(object o, string n, object v)
    {
        var p = o.GetType().GetProperty(n);
        if (p == null) throw new ArgumentException("Unsupported native property: " + o.GetType().Name + "." + n);
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (v != null && !t.IsInstanceOfType(v))
            v = t.IsEnum ? Enum.Parse(t, v.ToString()) : t == typeof(Guid) ? Guid.Parse(v.ToString()) : Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
        p.SetValue(o, v, null);
    }
    static object Child(object o, string n)
    {
        var p = o.GetType().GetProperty(n);
        if (p == null) throw new ArgumentException("Unsupported native child: " + n);
        var v = p.GetValue(o, null);
        if (v == null) { v = Activator.CreateInstance(p.PropertyType); p.SetValue(o, v, null); }
        return v;
    }
    static object MakePath(object o, string path)
    {
        foreach (var n in path.Split('.')) o = Child(o, n); return o;
    }
    static object New(string name) { return Activator.CreateInstance(dtos.GetType(name, true)); }
    static object Read(string file)
    {
        using (var s = File.OpenRead(file)) return load.MakeGenericMethod(windowType).Invoke(null, new object[] { s });
    }
    static void Write(string file, object page)
    {
        using (var s = new FileStream(file, FileMode.CreateNew, FileAccess.Write)) save.MakeGenericMethod(windowType).Invoke(null, new object[] { page, s });
    }
    static string S(Dictionary<string, object> d, string k, string fallback = null) { return d.ContainsKey(k) ? Convert.ToString(d[k], CultureInfo.InvariantCulture) : fallback; }
    static double N(Dictionary<string, object> d, string k, double fallback = 0) { return d.ContainsKey(k) ? Convert.ToDouble(d[k], CultureInfo.InvariantCulture) : fallback; }
    static object Position(object g)
    {
        return PathValue(g, "NormalInfo.Position") ?? PathValue(g, "DisplaySettings.Position") ?? Value(g, "DisplaySettings");
    }
    static object PositionSummary(object p)
    {
        return p == null ? null : Obj("x", Value(p, "X"), "y", Value(p, "Y"), "width", Value(p, "Width"), "height", Value(p, "Height"));
    }
    static object Summarize(object page)
    {
        var all = new List<object>();
        var widgets = Value(page, "Graphicses") as IList;
        if (widgets != null) foreach (var g in widgets)
        {
            var d = Obj("id", Value(g, "UniqueId").ToString(), "name", Value(g, "GraphicsName"), "type", g.GetType().Name,
                "z_index", Value(g, "ZIndex"), "bounds", PositionSummary(Position(g)));
            var texts = PathValue(g, "NormalInfo.StaticTextContentInfos") as IList;
            if (texts != null) d["texts"] = texts.Cast<object>().Select(x => Obj("language", Value(x, "LanguageSN"), "text", Value(x, "LabelContent"))).ToArray();
            var raw = PathValue(g, "NormalData.ReadAddress.Raw");
            if (raw != null) d["address"] = Obj("device_id", Convert.ToString(Value(raw, "DeviceId")), "register_id", Value(raw, "RegisterId"), "main_address", Value(raw, "MainAddress"), "station", Value(raw, "StationNo"));
            if (g.GetType().Name == "NumericCharInfoDto") d["decimals"] = PathValue(g, "NumericSetingData.DecimalValue");
            all.Add(d);
        }
        return Obj("id", Value(page, "Id"), "uuid", Value(page, "UniqueId").ToString(), "name", Value(page, "Name"),
            "width", Value(page, "Width"), "height", Value(page, "Height"), "component_count", all.Count, "widgets", all.ToArray());
    }
    static void SetPosition(object p, Dictionary<string, object> spec)
    {
        Set(p, "X", N(spec, "x")); Set(p, "Y", N(spec, "y")); Set(p, "Width", N(spec, "width")); Set(p, "Height", N(spec, "height"));
        Set(p, "MinWidth", 1); Set(p, "MinHeight", 1); Set(p, "MaxWidth", double.PositiveInfinity); Set(p, "MaxHeight", double.PositiveInfinity);
    }
    static void SetFont(object f, Dictionary<string, object> spec)
    {
        Set(f, "FontName", "Microsoft YaHei"); Set(f, "FontSize", N(spec, "font_size", 24)); Set(f, "FontColor", S(spec, "color", "#FF18334A"));
        Set(f, "FontType", "Graphic"); Set(f, "Version", 1); Set(f, "Alignment", "Center");
        Set(Child(f, "Advanced"), "HorizontalScaling", 100);
    }
    static object Widget(Dictionary<string, object> spec, Guid id, int z)
    {
        object g;
        var kind = S(spec, "kind");
        if (kind == "text")
        {
            g = New("Flexem.Studio.Components.Graphicses.StaticTextInfoDto"); Set(g, "IconKey", "Icon.FlexemStudio.StaticText");
            var n = Child(g, "NormalInfo"); Set(n, "LanguageSN", 1); Set(n, "UsingType", "Label"); Set(n, "DecisionLabelOverflow", true); Set(n, "Version", 2);
            SetPosition(Child(n, "Position"), spec);
            var texts = (IList)Child(n, "StaticTextContentInfos"); var item = Activator.CreateInstance(texts.GetType().GetGenericArguments()[0]);
            Set(item, "LanguageSN", 1); Set(item, "LabelContent", S(spec, "text")); Set(item, "Version", 2);
            var tp = Child(item, "Position"); Set(tp, "LeftRightPosition", "Center"); Set(tp, "TopBottomPosition", "Center");
            SetFont(Child(Child(item, "FontStyleData"), "Font"), spec); texts.Add(item);
        }
        else if (kind == "rectangle")
        {
            g = New("Flexem.Studio.Components.Graphicses.RectangleInfoDto"); Set(g, "IconKey", "Icon.FlexemStudio.Rectangle");
            var n = Child(g, "NormalInfo"); SetPosition(Child(n, "Position"), spec);
            var fill = Child(n, "Fill"); Set(fill, "UseFill", true); Set(fill, "BackgroundFillColor", S(spec, "color", "#FFEAF1F7"));
            var border = Child(n, "BorderStyle"); Set(border, "LineWidth", 1); Set(border, "LineColor", S(spec, "color", "#FFEAF1F7"));
        }
        else if (kind == "numeric_display")
        {
            g = New("Flexem.Studio.Components.Parts.NumericCharPart.NumericCharInfoDto"); Set(g, "IconKey", "Icon.FlexemStudio.TextDisplay"); Set(g, "Version", 6);
            SetPosition(MakePath(g, "DisplaySettings.Position"), spec); Set(Child(g, "DisplaySettings"), "Version", 4);
            var n = Child(g, "NormalData"); Set(n, "OperationAttrib", "NumericDisplay");
            var address = Child(n, "ReadAddress"); Set(address, "AddressType", "Word"); Set(address, "DataType", "UInt16");
            Set(address, "Speed", "Normal"); Set(address, "AddressLength", 1); Set(address, "Version", 5); Set(address, "OccupiedWord", 1);
            var raw = Child(address, "Raw"); Set(raw, "AddressWidth", "Word"); Set(raw, "DeviceId", S(spec, "device_id"));
            Set(raw, "RegisterId", N(spec, "register_id", 65791)); Set(raw, "MainAddress", N(spec, "address")); Set(raw, "StationNo", N(spec, "station", 1));
            Set(raw, "NewSubAddressIndexType", "Constant"); Set(raw, "Version", 4); Set(raw, "Length", 1);
            var number = Child(g, "NumericSetingData"); Set(number, "DataType", "UInt16"); Set(number, "DecimalValue", N(spec, "decimals")); Set(number, "IntegerValue", 5); Set(number, "Version", 1);
            SetFont(MakePath(g, "FontData.FontWithStyle.Font"), spec);
            var fp = MakePath(g, "FontData.FontPostion"); Set(fp, "HorizontalAlignmentType", "Center"); Set(fp, "VerticalAlignmentType", "Center");
        }
        else throw new ArgumentException("Unsupported widget kind: " + kind);
        Set(g, "UniqueId", id); Set(g, "GraphicsName", S(spec, "name")); Set(g, "ZIndex", z); Set(g, "ComponentId", z);
        return g;
    }
    static object Apply(Dictionary<string, object> a)
    {
        var page = Read(S(a, "input"));
        if (a.ContainsKey("page_name")) Set(page, "Name", S(a, "page_name"));
        if (a.ContainsKey("new_page_id"))
        {
            Set(page, "Id", N(a, "new_page_id")); Set(page, "UniqueId", S(a, "new_page_uuid"));
            ((IList)Child(page, "Graphicses")).Clear(); ((IList)Child(page, "Actions")).Clear();
        }
        var widgets = (IList)Child(page, "Graphicses");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var edits = a.ContainsKey("widgets") ? (IEnumerable)a["widgets"] : new object[0];
        foreach (Dictionary<string, object> spec in edits)
        {
            var name = S(spec, "name"); if (!keys.Add(name)) throw new ArgumentException("Duplicate widget key in request: " + name);
            var match = widgets.Cast<object>().Where(x => Convert.ToString(Value(x, "GraphicsName")) == name).ToArray();
            if (match.Length > 1) throw new ArgumentException("Existing widget key is ambiguous: " + name);
            var id = match.Length == 1 ? (Guid)Value(match[0], "UniqueId") : Guid.NewGuid();
            var z = match.Length == 1 ? Convert.ToInt32(Value(match[0], "ZIndex")) : widgets.Cast<object>().Select(x => Convert.ToInt32(Value(x, "ZIndex"))).DefaultIfEmpty(0).Max() + 1;
            var g = Widget(spec, id, z);
            if (match.Length == 1) widgets[widgets.IndexOf(match[0])] = g; else widgets.Add(g);
        }
        Write(S(a, "output"), page);
        var restored = Read(S(a, "output"));
        var before = json.Serialize(Summarize(page)); var after = json.Serialize(Summarize(restored));
        if (before != after) throw new InvalidDataException("Native roundtrip changed page content");
        return Summarize(restored);
    }
    [STAThread]
    static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var bin = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
                var file = Path.Combine(bin, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            dtos = Assembly.LoadFrom(Path.Combine(bin, "Flexem.Studio.Dtos.dll"));
            helper = Assembly.LoadFrom(Path.Combine(bin, "Flexem.Studio.Core.dll")).GetType("Flexem.Studio.Utility.DataContractSerializerHelper", true);
            windowType = dtos.GetType("Flexem.Studio.HMI.Window.HMIWindowInfoDto", true);
            load = helper.GetMethods().Single(x => x.Name == "LoadDto" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(Stream));
            save = helper.GetMethods().Single(x => x.Name == "SaveDto" && x.GetParameters().Length == 2 && x.GetParameters()[1].ParameterType == typeof(Stream));
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                try
                {
                    var req = json.Deserialize<Dictionary<string, object>>(line); var op = S(req, "op");
                    var a = req.ContainsKey("args") ? (Dictionary<string, object>)req["args"] : new Dictionary<string, object>();
                    object result;
                    if (op == "health") result = Obj("native_dll_loaded", true, "dto_version", dtos.GetName().Version.ToString(), "process_bits", IntPtr.Size * 8, "clr", Environment.Version.ToString(), "compiler_available", false);
                    else if (op == "read_page") result = Summarize(Read(S(a, "path")));
                    else if (op == "apply_page") result = Apply(a);
                    else if (op == "catalog") result = Catalog(a);
                    else if (op == "type_schema") result = TypeSchema(a);
                    else if (op == "document") result = Document(a);
                    else if (op == "clone_widget") result = CloneWidget(a);
                    else throw new ArgumentException("Unknown bridge operation: " + op);
                    Console.WriteLine(json.Serialize(Obj("ok", true, "data", result)));
                }
                catch (Exception ex)
                {
                    while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                    Console.WriteLine(json.Serialize(Obj("ok", false, "error", ex.GetType().Name + ": " + ex.Message)));
                }
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
