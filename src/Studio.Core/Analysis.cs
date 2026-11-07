using Iced.Intel;

namespace Studio.Core;

public record InstructionView(ulong Address, string Bytes, string Assembly, int Length, string Flow, ulong? BranchTarget)
{
    public string AddressHex => $"0x{Address:X}";
}
public record CodeCave(ulong Address, int Length, byte Fill)
{
    public string Explanation => $"0x{Address:X}: {Length} bytes of {Fill:X2}. Padding only; this does not prove the memory is unused.";
}

public static class CodeAnalysis
{
    public static List<Instruction> Decode(byte[] bytes, ulong address)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
        var output = new List<Instruction>();
        while (decoder.IP - address < (ulong)bytes.Length)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid) throw new InvalidDataException($"Cannot decode complete x86-64 instructions at 0x{instruction.IP:X}.");
            output.Add(instruction);
        }
        return output;
    }
    public static List<InstructionView> Disassemble(byte[] bytes, ulong address)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
        var formatter = new IntelFormatter(); var result = new List<InstructionView>();
        while (decoder.IP - address < (ulong)bytes.Length)
        {
            var i = decoder.Decode(); var output = new AssemblyOutput(); formatter.Format(i, output);
            var offset = checked((int)(i.IP - address));
            result.Add(new(i.IP, Hex.Format(bytes.AsSpan(offset, i.Length).ToArray()), i.IsInvalid ? "<invalid or truncated instruction>" : output.ToString(), i.Length, i.FlowControl.ToString(), IsBranch(i) ? i.NearBranchTarget : null));
        }
        return result;
    }
    public static bool IsBranch(Instruction i) => i.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
    public static BytePattern Signature(byte[] original)
    {
        var mask = original.Select(b => (byte?)b).ToArray();
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(original));
        while (decoder.IP < (ulong)original.Length)
        {
            var i = decoder.Decode(); if (i.IsInvalid) return new(original.Select(b => (byte?)b));
            var c = decoder.GetConstantOffsets(i); var start = (int)i.IP;
            if (i.IsIPRelativeMemoryOperand) for (var n = 0; n < c.DisplacementSize; n++) mask[start + c.DisplacementOffset + n] = null;
            if (IsBranch(i)) for (var n = 0; n < c.ImmediateSize; n++) mask[start + c.ImmediateOffset + n] = null;
        }
        return new(mask);
    }
    public static IReadOnlyList<CodeCave> FindCaves(byte[] bytes, ulong address, int minimumLength)
    {
        if (minimumLength < 1) throw new ArgumentOutOfRangeException(nameof(minimumLength));
        var caves = new List<CodeCave>();
        for (var i = 0; i < bytes.Length;)
        {
            var start = i; var fill = bytes[i++];
            if (fill is not (0 or 0x90 or 0xCC)) continue;
            while (i < bytes.Length && bytes[i] == fill) i++;
            if (i - start >= minimumLength) caves.Add(new(address + (ulong)start, i - start, fill));
        }
        return caves;
    }
    public static byte[] RelativeJump(ulong from, ulong to)
    {
        var delta = (Int128)to - ((Int128)from + 5);
        if (delta < int.MinValue || delta > int.MaxValue) throw new InvalidDataException("The target is outside the ±2 GiB rel32 jump range.");
        return [0xE9, .. BitConverter.GetBytes((int)delta)];
    }
    public static byte[] Nops(byte[] original, ulong address)
    {
        Decode(original, address); return Enumerable.Repeat((byte)0x90, original.Length).ToArray();
    }
    public static byte[] Relocate(byte[] patch, ulong source, ulong destination, IReadOnlyDictionary<ulong, ulong> mapping, bool code)
    {
        if (!code) return patch.ToArray();
        var instructions = Decode(patch, source);
        var boundaries = instructions.Select(i => i.IP).ToHashSet();
        for (var n = 0; n < instructions.Count; n++)
        {
            var i = instructions[n];
            if (IsBranch(i))
            {
                var target = i.NearBranchTarget;
                if (target >= source && target < source + (ulong)patch.Length)
                {
                    if (!boundaries.Contains(target)) throw new InvalidDataException($"Branch target 0x{target:X} lands inside an instruction.");
                    i.NearBranch64 = checked(destination + target - source);
                }
                else if (mapping.TryGetValue(target, out var relocated)) i.NearBranch64 = relocated;
                else throw new InvalidDataException($"Branch target 0x{target:X} has no verified relocation. Manual verification required.");
            }
            if (i.IsIPRelativeMemoryOperand)
            {
                if (mapping.TryGetValue(i.IPRelativeMemoryAddress, out var relocated)) i.MemoryDisplacement64 = relocated;
                else throw new InvalidDataException($"RIP-relative data target 0x{i.IPRelativeMemoryAddress:X} has no relocation. Manual verification required.");
            }
            i.IP = checked(destination + i.IP - source);
            instructions[n] = i;
        }
        var writer = new ByteWriter();
        if (!BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, destination), out var error, out _, BlockEncoderOptions.DontFixBranches)) throw new InvalidDataException(error);
        if (writer.Bytes.Count != patch.Length) throw new InvalidDataException("Relocation changes the patch size; an expanded patch needs manual design.");
        return writer.Bytes.ToArray();
    }
    private sealed class AssemblyOutput : FormatterOutput
    {
        private readonly System.Text.StringBuilder builder = new();
        public override void Write(string text, FormatterTextKind kind) => builder.Append(text);
        public override string ToString() => builder.ToString();
    }
    private sealed class ByteWriter : CodeWriter
    {
        public List<byte> Bytes { get; } = [];
        public override void WriteByte(byte value) => Bytes.Add(value);
    }
}

public record PortCandidate(string PatchId, ulong Address, int Score, bool ExactBytes, bool CodeCave, string Explanation, byte[] Expected,
    int Section = -1, InstructionEvidence? Evidence = null, CaveAssessment? Cave = null)
{
    public string AddressHex => $"0x{Address:X}";
    public string Status => "Generated candidate — not verified";
}
public record PortResult(List<PortCandidate> Candidates, List<PatchLine> ProposedPatches, List<string> Warnings)
{
    public List<string> NextSteps { get; init; } = [];
    public bool CanTest => ProposedPatches.Count > 0 && Warnings.Count == 0;
    public string Summary => CanTest ? "One candidate per line was found. Review and test it; matching bytes do not prove the cheat works." : "Manual verification required. " + string.Join(" ", Warnings);
}
public record PortSourceContext(ulong Start, byte[] Bytes);
