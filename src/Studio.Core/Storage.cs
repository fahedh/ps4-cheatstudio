using System.Text.Json;

namespace Studio.Core;

public static class LocalStore
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static async Task SaveAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Options), ct); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static async Task<T> LoadAsync<T>(string path, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > 128 * 1024 * 1024) throw new InvalidDataException("Local session exceeds the size limit.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), Options) ?? throw new InvalidDataException("Invalid local document.");
    }
}

public record DiagnosticEntry(DateTimeOffset Time, string Level, string Operation, string Message);
public sealed class DiagnosticsLog(string directory)
{
    private readonly object sync = new();
    private readonly List<DiagnosticEntry> entries = [];
    public event Action<DiagnosticEntry>? Added;
    public IReadOnlyList<DiagnosticEntry> Entries { get { lock (sync) return entries.ToArray(); } }
    public void Write(string operation, string message, string level = "Information")
    {
        var e = new DiagnosticEntry(DateTimeOffset.UtcNow, level, operation, message);
        lock (sync)
        {
            entries.Add(e); if (entries.Count > 5000) entries.RemoveAt(0);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "studio.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, JsonSerializer.Serialize(e) + Environment.NewLine);
        }
        Added?.Invoke(e);
    }
    public string Report(ConnectionSettings settings, DebuggerInfo? info, GameIdentity? game, bool simulation, string checkResults) => JsonSerializer.Serialize(new
    {
        application = "PS4 GoldHEN Cheat Studio 0.1.0", generated = DateTimeOffset.UtcNow,
        mode = simulation ? "Simulation only — no PS4 hardware validation" : "Live connection — hardware behavior requires user confirmation",
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        settings, debugger = info, runningGame = game, checks = checkResults,
        safety = "Read-only by default; patches require byte validation and explicit confirmation. Reports contain IP/settings/logs but no memory dumps.",
        entries = Entries
    }, LocalStore.Options);
}
