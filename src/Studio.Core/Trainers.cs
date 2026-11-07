using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Studio.Core;

public static class TrainerCodec
{
    private static readonly HashSet<string> AuthorNames = ["Moder", "Author", "Creator"];
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
        var credits = r.TryGetProperty("credits", out var cr) ? cr.EnumerateArray().Select(c => c.GetString() ?? "").ToList() : [];
        return Validate(new(Required(r, "name"), Required(r, "id"), Required(r, "version"), Required(r, "process"), cheats, new([], [], credits)));
    }
    public static Trainer ReadShn(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var r = XDocument.Load(reader).Root ?? throw new InvalidDataException("Missing trainer root.");
        if (r.Name.LocalName != "Trainer") throw new InvalidDataException("Expected a SHN Trainer root.");
        RejectUnknown(r, ["Cheat", "Genres", "Items"]);
        if (r.Elements("Items").Any(i => i.HasElements || !string.IsNullOrWhiteSpace(i.Value))) throw new NotSupportedException("SHN Items contains operational commands that are not supported. The trainer was not imported.");
        foreach (var a in r.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
            if (a.Name.LocalName is not ("Game" or "Cusa" or "Version" or "Process" or "Moder" or "Author" or "Creator" or "Description"))
                throw new NotSupportedException($"Unsupported SHN Trainer attribute '{a.Name}'. Its behavior cannot be safely discarded.");
        string Attr(string name) => (string?)r.Attribute(name) ?? throw new InvalidDataException($"Missing {name}.");
        var cheats = new List<Cheat>();
        foreach (var c in r.Elements("Cheat"))
        {
            RejectAttributes(c, ["Text", "Description"]);
            RejectUnknown(c, ["Cheatline"]);
            var lines = new List<PatchLine>();
            foreach (var p in c.Elements("Cheatline"))
            {
                RejectUnknown(p, ["Section", "Offset", "ValueOn", "ValueOff", "Absolute", "PS5Offset"]);
                foreach (var group in p.Elements().GroupBy(e => e.Name))
                    if (group.Count() != 1 || group.First().HasElements || group.First().HasAttributes) throw new InvalidDataException($"SHN {group.Key} must be a single plain-text field.");
                if (p.HasAttributes) throw new NotSupportedException("Operational Cheatline attributes are not supported; no command was discarded.");
                string Value(string n) => p.Element(n)?.Value ?? throw new InvalidDataException($"Missing {n}.");
                var absoluteText = p.Element("Absolute")?.Value;
                if (absoluteText is not null && !bool.TryParse(absoluteText, out _)) throw new InvalidDataException("Absolute must be True or False; its addressing cannot be guessed.");
                lines.Add(new($"line-{lines.Count + 1}", int.Parse(Value("Section")), Hex.Address(Value("Offset")), Hex.Bytes(Value("ValueOff")), Hex.Bytes(Value("ValueOn")), [], bool.TryParse(absoluteText, out var a) && a, p.Element("PS5Offset")?.Value));
            }
            cheats.Add(new((string?)c.Attribute("Text") ?? "Unnamed cheat", (string?)c.Attribute("Description") ?? "", lines));
        }
        var authors = r.Attributes().Where(a => a.Name.Namespace == XNamespace.None && AuthorNames.Contains(a.Name.LocalName)).ToDictionary(a => a.Name.LocalName, a => a.Value);
        foreach (var genre in r.Elements("Genres")) { RejectUnknown(genre, []); RejectAttributes(genre, ["Name"]); }
        foreach (var item in r.Elements("Items")) RejectAttributes(item, []);
        var genres = r.Elements("Genres").Select(e => (string?)e.Attribute("Name") ?? "").ToList();
        return Validate(new(Attr("Game"), Attr("Cusa"), Attr("Version"), Attr("Process"), cheats, new(authors, genres, [], (string?)r.Attribute("Description"))));
    }
    public static Trainer Validate(Trainer t)
    {
        if (!Regex.IsMatch(t.TitleId, @"^CUSA\d{5}$") || !Regex.IsMatch(t.Version, @"^\d{2}\.\d{2}$") || string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Process)) throw new InvalidDataException("A trainer requires a name, CUSA12345 title ID, XX.YY version and process.");
        if (t.Cheats.Count is 0 or > 10000) throw new InvalidDataException("Invalid number of cheats.");
        if (t.Metadata is { } metadata && (metadata.Authors.Any(a => !AuthorNames.Contains(a.Key) || a.Value.Length > 2048) || metadata.Genres.Count > 100 || metadata.Credits.Count > 1000)) throw new InvalidDataException("Invalid trainer attribution metadata.");
        foreach (var c in t.Cheats)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) throw new InvalidDataException("Every cheat needs a nonempty name.");
            if (c.Patches.Count is 0 or > 10000) throw new InvalidDataException("A cheat must contain 1–10000 memory lines.");
            var ids = c.Patches.Select(p => p.Id).ToHashSet();
            if (ids.Count != c.Patches.Count) throw new InvalidDataException("Duplicate patch identifiers.");
            foreach (var p in c.Patches)
            {
                if (p.Section < 0 || p.Original.Length == 0 || p.Original.Length != p.Replacement.Length || p.Replacement.Length > 1048576 || p.Offset > ulong.MaxValue - (ulong)p.Replacement.Length) throw new InvalidDataException("Patch bytes must be nonempty, equal length, and within address bounds.");
                if (p.Dependencies.Any(d => !ids.Contains(d) || d == p.Id)) throw new InvalidDataException("Missing or self-referencing patch dependency.");
                if (p.Ps5Offset?.Length > 1024) throw new InvalidDataException("PS5Offset metadata exceeds 1024 characters. PS4 addressing was not inferred from it.");
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
        var credits = t.Metadata is { Credits.Count: > 0 } m ? m.Credits : t.Metadata is { Authors.Count: > 0 } a ? a.Authors.Values.ToList() : ["PS4 GoldHEN Cheat Studio"];
        return JsonSerializer.Serialize(new { name = t.Name, id = t.TitleId, version = t.Version, process = t.Process, mods = t.Cheats.Select(c => new { name = c.Name, description = c.Description, type = "checkbox", memory = OrderForExport(c).Select(p => new { offset = p.Offset.ToString("X"), on = Hex.Format(p.Replacement), off = Hex.Format(p.Original) }) }), credits }, new JsonSerializerOptions { WriteIndented = true });
    }
    public static string Shn(Trainer t)
    {
        Validate(t);
        var authors = t.Metadata is { Authors.Count: > 0 } m ? m.Authors : new Dictionary<string, string> { ["Moder"] = t.Metadata is { Credits.Count: > 0 } cr ? string.Join(", ", cr.Credits) : "PS4 GoldHEN Cheat Studio" };
        var root = new XElement("Trainer", new XAttribute("Game", t.Name), new XAttribute("Cusa", t.TitleId), new XAttribute("Version", t.Version), new XAttribute("Process", t.Process), authors.Select(a => new XAttribute(a.Key, a.Value)),
            t.Metadata?.Description is { } description ? new XAttribute("Description", description) : null,
            t.Metadata?.Genres.Select(g => new XElement("Genres", new XAttribute("Name", g))), new XElement("Items"),
            t.Cheats.Select(c => new XElement("Cheat", new XAttribute("Text", c.Name), new XAttribute("Description", c.Description), OrderForExport(c).Select(p => new XElement("Cheatline", new XElement("Section", p.Section), new XElement("Offset", p.Offset.ToString("X")), new XElement("ValueOn", Hex.Format(p.Replacement)), new XElement("ValueOff", Hex.Format(p.Original)), p.Absolute ? new XElement("Absolute", "True") : null, p.Ps5Offset is not null ? new XElement("PS5Offset", p.Ps5Offset) : null)))));
        using var output = new StringWriterWithEncoding();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 })) new XDocument(root).WriteTo(writer);
        return output.ToString();
    }
    private sealed class StringWriterWithEncoding : StringWriter { public override Encoding Encoding => Encoding.UTF8; }
    private static void RejectUnknown(XElement element, string[] allowed)
    {
        var unknown = element.Elements().FirstOrDefault(e => e.Name.Namespace != XNamespace.None || !allowed.Contains(e.Name.LocalName));
        if (unknown is not null) throw new NotSupportedException($"Unsupported SHN command '{unknown.Name}' inside {element.Name}. Only known non-executable metadata such as PS5Offset can be preserved without execution.");
    }
    private static void RejectAttributes(XElement element, string[] allowed)
    {
        var attribute = element.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !allowed.Contains(a.Name.LocalName)));
        if (attribute is not null) throw new NotSupportedException($"Unsupported SHN {element.Name} attribute '{attribute.Name}'; its behavior was not discarded.");
    }
    public static IReadOnlyList<PatchLine> OrderForExport(Cheat cheat)
    {
        var ordered = new List<PatchLine>(); var visited = new HashSet<string>();
        void Visit(PatchLine patch)
        {
            if (!visited.Add(patch.Id)) return;
            foreach (var id in patch.Dependencies) Visit(cheat.Patches.Single(p => p.Id == id));
            ordered.Add(patch);
        }
        foreach (var patch in cheat.Patches) Visit(patch);
        return ordered;
    }
    private static string Required(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new InvalidDataException($"Missing {name}.");
    private static string Optional(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";
}
