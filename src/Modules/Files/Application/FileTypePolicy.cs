using System.Buffers.Binary;

namespace Zaagl.Modules.Files.Application;

public enum FileClass
{
    Other,
    Executable,
    EncryptedArchive,
}

/// <param name="Type">The detected type; null for executables and unknown content (served as octet-stream).</param>
public sealed record FileClassification(DetectedType? Type, FileClass Class);

/// <summary>
/// The interim PD-16 policy (ARC-009 section 9.2, PD-16 OPEN: configuration, not a decision). Detection reads only the first 64 KB of the
/// bytes; the declared type is never an input and the file name is used only as an extra check on the display name, never for a path.
/// Executables (PE, ELF, Mach-O, shebang scripts, .lnk, and a blocklisted extension) are refused for guests and Free; encrypted ZIP and
/// RAR archives are refused for everyone. 7z is not inspected here (its header flag lives at the tail); clamd AlertEncrypted is the backstop.
/// </summary>
public static class FileTypePolicy
{
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jar", ".scr", ".com", ".lnk", ".sh",
    };

    public static FileClassification Classify(ReadOnlySpan<byte> head)
    {
        if (IsExecutable(head)) return new(null, FileClass.Executable);
        if (IsEncryptedArchive(head)) return new(null, FileClass.EncryptedArchive);
        var media = MediaSniffer.Detect(head);
        if (media is not null) return new(media, FileClass.Other);
        if (head.Length >= 5 && head[..5].SequenceEqual("%PDF-"u8)) return new(new("application/pdf", ".pdf"), FileClass.Other);
        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04) return new(new("application/zip", ".zip"), FileClass.Other);
        if (head.Length >= 3 && head[0] == 0x1F && head[1] == 0x8B && head[2] == 0x08) return new(new("application/gzip", ".gz"), FileClass.Other);
        if (head.Length >= 6 && head[..6].SequenceEqual((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return new(new("application/x-7z-compressed", ".7z"), FileClass.Other);
        return new(null, FileClass.Other);
    }

    public static bool HasBlockedExtension(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return false;
        var dot = displayName.LastIndexOf('.');
        return dot >= 0 && BlockedExtensions.Contains(displayName[dot..].Trim());
    }

    public static bool IsExecutable(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 64 && head[0] == 'M' && head[1] == 'Z')
        {
            var pe = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(0x3C, 4));
            if (pe >= 0 && pe + 4 <= head.Length && head.Slice(pe, 4).SequenceEqual((ReadOnlySpan<byte>)[(byte)'P', (byte)'E', 0, 0])) return true;
        }
        if (head.Length >= 4 && head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F') return true;
        if (head.Length >= 4)
        {
            var magic = BinaryPrimitives.ReadUInt32BigEndian(head);
            if (magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE) return true;
            // 0xCAFEBABE is a universal Mach-O (a small architecture count) or a Java class (version >= 45).
            if (magic == 0xCAFEBABE && head.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(head[4..]) is > 0 and < 45) return true;
        }
        if (head.Length >= 2 && head[0] == '#' && head[1] == '!') return true;
        if (head.Length >= 8 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0])) return true; // .lnk
        return false;
    }

    public static bool IsEncryptedArchive(ReadOnlySpan<byte> head)
    {
        // ZIP: walk the local file headers inside the head; general purpose flag bit 0 means the entry is encrypted.
        var p = 0;
        for (var entries = 0; entries < 64 && p + 30 <= head.Length
            && head[p] == 0x50 && head[p + 1] == 0x4B && head[p + 2] == 0x03 && head[p + 3] == 0x04; entries++)
        {
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(p + 6, 2));
            if ((flags & 0x0001) != 0) return true;
            if ((flags & 0x0008) != 0) break; // sizes follow the data: cannot skip
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(p + 18, 4));
            if (compressed == 0xFFFFFFFF) break;
            var name = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(p + 26, 2));
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(p + 28, 2));
            var next = (long)p + 30 + name + extra + compressed;
            if (next > int.MaxValue) break;
            p = (int)next;
        }
        // RAR 5: archive encryption header (type 4) right after the signature.
        if (head.Length >= 8 + 4 + 2 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]))
        {
            // crc32 (4), header size (vint), header type (vint)
            var q = 12;
            while (q < head.Length && (head[q] & 0x80) != 0) q++;
            q++;
            return q < head.Length && head[q] == 4;
        }
        // RAR 4: main header flag 0x0080 (headers encrypted) or the first file header flag 0x0004 (password).
        if (head.Length >= 7 + 13 && head[..7].SequenceEqual((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]))
        {
            if (head[9] == 0x73 && (BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(10, 2)) & 0x0080) != 0) return true;
            var size = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(12, 2));
            var next = 7 + size;
            if (next >= 7 && next + 7 <= head.Length && head[next + 2] == 0x74
                && (BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(next + 3, 2)) & 0x0004) != 0) return true;
        }
        return false;
    }
}
