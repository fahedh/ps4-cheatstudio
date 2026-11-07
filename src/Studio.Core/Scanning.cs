using System.Buffers.Binary;
using System.Globalization;

namespace Studio.Core;

public sealed class BytePattern
{
    public byte?[] Bytes { get; }
    public BytePattern(IEnumerable<byte?> bytes)
    {
        Bytes = bytes.ToArray();
        if (Bytes.Length is 0 or > 4096 || Bytes.All(b => b is null)) throw new FormatException("A signature needs 1–4096 bytes and at least one fixed byte.");
    }
    public static BytePattern Parse(string text)
    {
        var parts = text.Replace('-', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Length > 2)
        {
            if (parts[0].Length % 2 != 0) throw new FormatException("Odd-length signature.");
            parts = Enumerable.Range(0, parts[0].Length / 2).Select(i => parts[0].Substring(i * 2, 2)).ToArray();
        }
        return new(parts.Select(p => p is "?" or "??" ? (byte?)null : p.Length == 2 ? Convert.ToByte(p, 16) : throw new FormatException("Use pairs of hex digits or ?? wildcards.")));
    }
    public bool Matches(byte[] data, int at)
    {
        if (at < 0 || at + Bytes.Length > data.Length) return false;
        for (var j = 0; j < Bytes.Length; j++) if (Bytes[j] is byte b && data[at + j] != b) return false;
        return true;
    }
    public IReadOnlyList<int> Find(byte[] data, int maxResults = 10000, CancellationToken ct = default)
    {
        if (maxResults < 1) throw new ArgumentOutOfRangeException(nameof(maxResults));
        var list = new List<int>();
        for (var i = 0; i <= data.Length - Bytes.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (Matches(data, i)) { if (list.Count == maxResults) throw new InvalidDataException("Too many signature matches; narrow the range or strengthen the signature."); list.Add(i); }
        }
        return list;
    }
    public override string ToString() => string.Join(" ", Bytes.Select(b => b?.ToString("X2") ?? "??"));
}

public enum ValueKind { Byte, Int16, Int32, Int64, Float, Double, Hex }
public enum ScanComparison { Exact, Unknown, Increased, Decreased, Changed, Unchanged }
public record ScanHit(ulong Address, byte[] Value)
{
    public string AddressHex => $"0x{Address:X}";
    public string Bytes => Hex.Format(Value);
}
public record ScanSession(Guid ConnectionId, GameIdentity Game, ulong Start, int Length, ValueKind Kind, List<ScanHit> Hits, DateTimeOffset Created);

public static class Values
{
    public static int Width(ValueKind k) => k switch { ValueKind.Byte => 1, ValueKind.Int16 => 2, ValueKind.Int32 or ValueKind.Float => 4, ValueKind.Int64 or ValueKind.Double => 8, _ => throw new ArgumentException("Hex scans require a byte sequence.") };
    public static byte[] Parse(string text, ValueKind k)
    {
        var c = CultureInfo.InvariantCulture;
        return k switch { ValueKind.Hex => Hex.Bytes(text), ValueKind.Byte => [byte.Parse(text, c)], ValueKind.Int16 => BitConverter.GetBytes(short.Parse(text, c)), ValueKind.Int32 => BitConverter.GetBytes(int.Parse(text, c)), ValueKind.Int64 => BitConverter.GetBytes(long.Parse(text, c)), ValueKind.Float => BitConverter.GetBytes(float.Parse(text, c)), ValueKind.Double => BitConverter.GetBytes(double.Parse(text, c)), _ => throw new ArgumentOutOfRangeException(nameof(k)) };
    }
    // Decimal preserves every signed 64-bit integer; doubles are compared as doubles rather than rounded integers.
    public static int Compare(byte[] a, byte[] b, ValueKind kind) => kind switch
    {
        ValueKind.Byte => a[0].CompareTo(b[0]), ValueKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt16LittleEndian(b)), ValueKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt32LittleEndian(b)), ValueKind.Int64 => BinaryPrimitives.ReadInt64LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt64LittleEndian(b)), ValueKind.Float => BitConverter.ToSingle(a).CompareTo(BitConverter.ToSingle(b)), ValueKind.Double => BitConverter.ToDouble(a).CompareTo(BitConverter.ToDouble(b)), _ => throw new ArgumentException("Increased/decreased only apply to numeric values.")
    };
}

public sealed class MemoryScanner(MemorySession session)
{
    public const int MaximumBytes = 64 * 1024 * 1024, MaximumHits = 500000;
    public Task<byte[]> SnapshotAsync(int pid, ulong start, int length, IProgress<double>? progress = null, CancellationToken ct = default) => session.ExclusiveAsync(async () =>
    {
        if (length is < 1 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(length), "Select a range of 1 byte to 64 MiB.");
        var maps = await session.Memory.GetMapsAsync(pid, ct);
        if (!maps.Any(m => m.Readable && m.Contains(start, length))) throw new InvalidDataException("The selected range is not inside a readable process region.");
        var output = new byte[length];
        for (var done = 0; done < length;)
        {
            ct.ThrowIfCancellationRequested(); var n = Math.Min(65536, length - done);
            var b = await session.Memory.ReadAsync(pid, checked(start + (ulong)done), n, ct);
            if (b.Length != n) throw new EndOfStreamException("Short process-memory read.");
            b.CopyTo(output, done); done += n; progress?.Report((double)done / length);
        }
        return output;
    }, ct);
    public async Task<IReadOnlyList<ulong>> PatternAsync(int pid, ulong start, int length, BytePattern pattern, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // One bounded snapshot keeps signatures intact across network chunk boundaries.
        var bytes = await SnapshotAsync(pid, start, length, progress, ct);
        return await Task.Run(() => pattern.Find(bytes, ct: ct).Select(i => start + (ulong)i).ToArray(), ct);
    }
    public async Task<ScanSession> InitialAsync(GameIdentity game, ulong start, int length, ValueKind kind, ScanComparison comparison, string value, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (comparison is not (ScanComparison.Exact or ScanComparison.Unknown)) throw new ArgumentException("Start with an exact or unknown-value scan.");
        var bytes = await SnapshotAsync(game.Pid, start, length, progress, ct);
        var expected = comparison == ScanComparison.Exact ? Values.Parse(value, kind) : new byte[Values.Width(kind)];
        var hits = await Task.Run(() =>
        {
            var list = new List<ScanHit>();
            for (var i = 0; i <= bytes.Length - expected.Length; i += expected.Length)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (comparison == ScanComparison.Unknown || bytes.AsSpan(i, expected.Length).SequenceEqual(expected))
                {
                    if (list.Count >= MaximumHits) throw new InvalidDataException("Too many values; narrow the scan range (maximum 500,000 results).");
                    list.Add(new(start + (ulong)i, bytes.AsSpan(i, expected.Length).ToArray()));
                }
            }
            return list;
        }, ct);
        return new(session.Memory.ConnectionId, game, start, length, kind, hits, DateTimeOffset.UtcNow);
    }
    public Task<ScanSession> RescanAsync(ScanSession previous, ScanComparison comparison, string value, CancellationToken ct = default) => session.ExclusiveAsync(async () =>
    {
        if (previous.ConnectionId != session.Memory.ConnectionId || !previous.Game.SameGame(await session.Memory.GetGameAsync(ct))) throw new InvalidDataException("This scan belongs to another game session. Start a new scan after reconnecting or restarting the game.");
        if (comparison == ScanComparison.Unknown) throw new ArgumentException("Unknown initial value is available only for a new scan.");
        var matches = new List<ScanHit>();
        var exact = comparison == ScanComparison.Exact ? Values.Parse(value, previous.Kind) : null;
        // Fetch each occupied page once, avoiding a round-trip per candidate.
        var maps = await session.Memory.GetMapsAsync(previous.Game.Pid, ct);
        foreach (var group in previous.Hits.GroupBy(h => h.Address / 65536))
        {
            ct.ThrowIfCancellationRequested();
            var first = group.Min(h => h.Address); var end = group.Max(h => checked(h.Address + (ulong)h.Value.Length));
            if (!maps.Any(m => m.Readable && m.Contains(first, checked((int)(end - first))))) throw new InvalidDataException("A scan region disappeared; start a new scan.");
            var bytes = await session.Memory.ReadAsync(previous.Game.Pid, first, checked((int)(end - first)), ct);
            foreach (var old in group)
            {
                var current = bytes.AsSpan((int)(old.Address - first), old.Value.Length).ToArray();
                var same = current.SequenceEqual(old.Value);
                var keep = comparison switch { ScanComparison.Exact => current.SequenceEqual(exact!), ScanComparison.Changed => !same, ScanComparison.Unchanged => same, ScanComparison.Increased => Values.Compare(current, old.Value, previous.Kind) > 0, ScanComparison.Decreased => Values.Compare(current, old.Value, previous.Kind) < 0, _ => false };
                if (keep) matches.Add(new(old.Address, current));
            }
        }
        return previous with { Hits = matches };
    }, ct);
}
