using System.Globalization;
using System.Text.RegularExpressions;

namespace Studio.Core;

public static class Hex
{
    public static byte[] Bytes(string text)
    {
        var clean = Regex.Replace(text.Trim(), @"[\s\-]", "");
        if (clean.Length == 0 || clean.Length % 2 != 0 || clean.Length > 2 * 1024 * 1024)
            throw new FormatException("Supply a nonempty, even-length hexadecimal byte sequence (maximum 1 MiB).");
        return Convert.FromHexString(clean);
    }
    public static ulong Address(string text)
    {
        var value = text.Trim(); if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return ulong.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }
    public static string Format(byte[] bytes) => Convert.ToHexString(bytes);
}

public record ProcessEntry(int Pid, string Name);
public record GameIdentity(int Pid, string Name, string TitleId, string Version, string ProcessName)
{
    public bool IsGame => Pid > 0 && Regex.IsMatch(TitleId, @"^CUSA\d{5}$") && ProcessName == "eboot.bin";
    public bool HasKnownVersion => Regex.IsMatch(Version, @"^\d{2}\.\d{2}$");
    public bool SameGame(GameIdentity other) => Pid == other.Pid && TitleId == other.TitleId && Version == other.Version && ProcessName == other.ProcessName;
}
public record MemoryRegion(int Index, ulong Start, ulong End, ulong Offset, ulong Protection, string Name)
{
    public ulong Size => End - Start;
    public string Address => $"0x{Start:X}";
    public string Permissions => $"{((Protection & 1) != 0 ? 'r' : '-')}{((Protection & 2) != 0 ? 'w' : '-')}{((Protection & 4) != 0 ? 'x' : '-')}";
    public bool Readable => (Protection & 1) != 0;
    public bool Executable => (Protection & 4) != 0;
    public bool Contains(ulong address, int length) => length > 0 && address >= Start && address < End && (ulong)length <= End - address;
}
public record DebuggerInfo(string Protocol, string Brand, string Capability, ushort Firmware, ushort Platform)
{
    // fw.c returns major * 100 + minor, despite the BCD example in PROTOCOL.md.
    public string FirmwareDisplay => Firmware == ushort.MaxValue ? "Unavailable" : $"{Firmware / 100}.{Firmware % 100:00}";
    public bool VerifiedWrites => System.Version.TryParse(Capability, out var c) && c >= new Version(1, 1);
    public bool IsNg => Brand.StartsWith("ps4debug-NG by Pharaoh2k v", StringComparison.Ordinal);
}

public interface IProcessMemory
{
    Task<GameIdentity> GetGameAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRegion>> GetMapsAsync(int pid, CancellationToken ct = default);
    Task<byte[]> ReadAsync(int pid, ulong address, int length, CancellationToken ct = default);
    Task WriteAsync(int pid, ulong address, byte[] bytes, CancellationToken ct = default);
    Guid ConnectionId { get; }
}

/// <summary>All high-level operations in a workspace share this gate, including patch/rollback and scans.</summary>
public sealed class MemorySession(IProcessMemory memory)
{
    public IProcessMemory Memory { get; } = memory;
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await action(); } finally { gate.Release(); }
    }
}

public record ConnectionSettings
{
    public string Host { get; init; } = "192.168.8.188";
    public int DebugPort { get; init; } = 744;
    public int PayloadPort { get; init; } = 9090;
    public int FtpPort { get; init; } = 2121;
    public int InterruptPort { get; init; } = 755;
    public int TimeoutSeconds { get; init; } = 8;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Contains('/') || new[] { DebugPort, PayloadPort, FtpPort, InterruptPort }.Any(p => p is < 1 or > 65535) || TimeoutSeconds is < 1 or > 120)
            throw new ArgumentException("Enter a hostname or IP, valid TCP ports, and a timeout from 1 to 120 seconds.");
    }
}

public record PatchLine(string Id, int Section, ulong Offset, byte[] Original, byte[] Replacement, string[] Dependencies, bool Absolute = false, string? Ps5Offset = null, bool IsCodeCave = false, bool CaveUseApproved = false)
{
    public string OffsetHex => $"0x{Offset:X}";
    public string OriginalHex => Hex.Format(Original);
    public string ReplacementHex => Hex.Format(Replacement);
}
public record Cheat(string Name, string Description, List<PatchLine> Patches);
public record TrainerMetadata(Dictionary<string, string> Authors, List<string> Genres, List<string> Credits, string? Description = null);
public record Trainer(string Name, string TitleId, string Version, string Process, List<Cheat> Cheats, TrainerMetadata? Metadata = null);
public record VerificationRecord(DateTimeOffset Time, string TitleId, string Version, string Process, string Cheat, string Outcome, string Notes);
public record GameProfile
{
    public string Name { get; init; } = "New game";
    public string TitleId { get; init; } = "CUSA00000";
    public string Version { get; init; } = "01.00";
    public string Process { get; init; } = "eboot.bin";
    public List<MemoryRegion> Sections { get; init; } = [];
    public List<Trainer> Trainers { get; init; } = [];
    public List<PortCandidate> Candidates { get; init; } = [];
    public List<VerificationRecord> Verifications { get; init; } = [];
    public List<string> ScanSessions { get; init; } = [];
}
