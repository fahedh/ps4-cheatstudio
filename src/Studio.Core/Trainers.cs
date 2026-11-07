using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Studio.Core;

public static class TrainerCodec
{
    public static Trainer Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Trainer exceeds 16 MiB.");
        return Path.GetExtension(path).Equals(".shn", StringComparison.OrdinalIgnoreCase) ? ReadShn(File.ReadAllText(path)) : ReadJson(File.ReadAllText(path));
    }
    public static Trainer ReadJson(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var r = doc.RootElement; var cheats = new List<Cheat>();
        foreach (var mod in r.GetProperty("mods").EnumerateArray())
        {
            if (mod.TryGetProperty("type", out var type) && type.GetString() != "checkbox") throw new NotSupportedException("Only fixed checkbox memory patches are supported; scripts and sliders cannot be safely converted.");
            var patches = new List<PatchLine>();
            foreach (var m in mod.GetProperty("memory").EnumerateArray())
            {
                if (m.EnumerateObject().Any(p => p.Name is not ("offset" or "on" or "off"))) throw new NotSupportedException("This trainer contains extended memory commands; refusing to discard them.");
                patches.Add(new($"line-{patches.Count + 1}", 0, Hex.Address(Required(m, "offset")), Hex.Bytes(Required(m, "off")), Hex.Bytes(Required(m, "on")), []));
            }
            cheats.Add(new(Required(mod, "name"), Optional(mod, "description"), patches));
        }
        return Validate(new(Required(r, "name"), Required(r, "id"), Required(r, "version"), Required(r, "process"), cheats));
    }
    public static Trainer ReadShn(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var r = XDocument.Load(reader).Root ?? throw new InvalidDataException("Missing trainer root.");
        if (r.Name.LocalName != "Trainer") throw new InvalidDataException("Expected a SHN Trainer root.");
        if (r.Elements().Any(e => e.Name.LocalName is not ("Cheat" or "Genres" or "Items")) || r.Element("Items")?.HasElements == true) throw new NotSupportedException("Extended SHN trainer commands are not supported.");
        string Attr(string name) => (string?)r.Attribute(name) ?? throw new InvalidDataException($"Missing {name}.");
        var cheats = new List<Cheat>();
        foreach (var c in r.Elements("Cheat"))
        {
            var lines = new List<PatchLine>();
            foreach (var p in c.Elements("Cheatline"))
            {
                if (p.Elements().Any(e => e.Name.LocalName is not ("Section" or "Offset" or "ValueOn" or "ValueOff" or "Absolute"))) throw new NotSupportedException("Extended SHN commands are not supported.");
                string Value(string n) => p.Element(n)?.Value ?? throw new InvalidDataException($"Missing {n}.");
                var absoluteText = p.Element("Absolute")?.Value;
                if (absoluteText is not null && !bool.TryParse(absoluteText, out _)) throw new InvalidDataException("Absolute must be True or False; its addressing cannot be guessed.");
                lines.Add(new($"line-{lines.Count + 1}", int.Parse(Value("Section")), Hex.Address(Value("Offset")), Hex.Bytes(Value("ValueOff")), Hex.Bytes(Value("ValueOn")), [], bool.TryParse(absoluteText, out var a) && a));
            }
            cheats.Add(new((string?)c.Attribute("Text") ?? "Unnamed cheat", (string?)c.Attribute("Description") ?? "", lines));
        }
        return Validate(new(Attr("Game"), Attr("Cusa"), Attr("Version"), Attr("Process"), cheats));
    }
    public static Trainer Validate(Trainer t)
    {
        if (!Regex.IsMatch(t.TitleId, @"^CUSA\d{5}$") || !Regex.IsMatch(t.Version, @"^\d{2}\.\d{2}$") || string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Process)) throw new InvalidDataException("A trainer requires a name, CUSA12345 title ID, XX.YY version and process.");
        if (t.Cheats.Count is 0 or > 10000) throw new InvalidDataException("Invalid number of cheats.");
        foreach (var c in t.Cheats)
        {
            if (c.Patches.Count is 0 or > 10000) throw new InvalidDataException("A cheat must contain 1–10000 memory lines.");
            var ids = c.Patches.Select(p => p.Id).ToHashSet();
            if (ids.Count != c.Patches.Count) throw new InvalidDataException("Duplicate patch identifiers.");
            foreach (var p in c.Patches)
            {
                if (p.Section < 0 || p.Original.Length == 0 || p.Original.Length != p.Replacement.Length || p.Replacement.Length > 1048576 || p.Offset > ulong.MaxValue - (ulong)p.Replacement.Length) throw new InvalidDataException("Patch bytes must be nonempty, equal length, and within address bounds.");
                if (p.Dependencies.Any(d => !ids.Contains(d) || d == p.Id)) throw new InvalidDataException("Missing or self-referencing patch dependency.");
            }
            var visited = new HashSet<string>(); var active = new HashSet<string>();
            void Visit(string id)
            {
                if (active.Contains(id)) throw new InvalidDataException("Cyclic patch dependency.");
                if (!visited.Add(id)) return;
                active.Add(id); foreach (var d in c.Patches.Single(p => p.Id == id).Dependencies) Visit(d); active.Remove(id);
            }
            foreach (var id in ids) Visit(id);
        }
        return t;
    }
    public static string Json(Trainer t)
    {
        Validate(t);
        if (t.Cheats.SelectMany(c => c.Patches).Any(p => p.Section != 0 || p.Absolute)) throw new InvalidDataException("GoldHEN JSON requires main-module relative offsets. Resolve the section mapping before export.");
        return JsonSerializer.Serialize(new { name = t.Name, id = t.TitleId, version = t.Version, process = t.Process, mods = t.Cheats.Select(c => new { name = c.Name, description = c.Description, type = "checkbox", memory = c.Patches.Select(p => new { offset = p.Offset.ToString("X"), on = Hex.Format(p.Replacement), off = Hex.Format(p.Original) }) }), credits = new[] { "PS4 GoldHEN Cheat Studio" } }, new JsonSerializerOptions { WriteIndented = true });
    }
    public static string Shn(Trainer t)
    {
        Validate(t);
        var root = new XElement("Trainer", new XAttribute("Game", t.Name), new XAttribute("Cusa", t.TitleId), new XAttribute("Version", t.Version), new XAttribute("Process", t.Process), new XAttribute("Moder", "PS4 GoldHEN Cheat Studio"),
            t.Cheats.Select(c => new XElement("Cheat", new XAttribute("Text", c.Name), new XAttribute("Description", c.Description), c.Patches.Select(p => new XElement("Cheatline", new XElement("Section", p.Section), new XElement("Offset", p.Offset.ToString("X")), new XElement("ValueOn", Hex.Format(p.Replacement)), new XElement("ValueOff", Hex.Format(p.Original)), p.Absolute ? new XElement("Absolute", "True") : null)))));
        using var output = new StringWriterWithEncoding();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 })) new XDocument(root).WriteTo(writer);
        return output.ToString();
    }
    private sealed class StringWriterWithEncoding : StringWriter { public override Encoding Encoding => Encoding.UTF8; }
    private static string Required(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new InvalidDataException($"Missing {name}.");
    private static string Optional(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";
}
