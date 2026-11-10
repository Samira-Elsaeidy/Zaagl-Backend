using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Infrastructure;

namespace Zaagl.Files.Tests;

/// <summary>The interim PD-16 type policy (ARC-009 9.2): what counts as an executable and as an encrypted archive.</summary>
public sealed class FileTypePolicyTests
{
    private static byte[] Pe()
    {
        var b = new byte[256];
        b[0] = (byte)'M'; b[1] = (byte)'Z'; b[0x3C] = 0x80; b[0x80] = (byte)'P'; b[0x81] = (byte)'E';
        return b;
    }

    [Fact]
    public void Executables_are_detected_by_their_bytes()
    {
        Assert.Equal(FileClass.Executable, FileTypePolicy.Classify(Pe()).Class);
        Assert.Equal(FileClass.Executable, FileTypePolicy.Classify([0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]).Class);
        Assert.Equal(FileClass.Executable, FileTypePolicy.Classify([0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1]).Class);
        Assert.Equal(FileClass.Executable, FileTypePolicy.Classify("#!/bin/sh\necho hi"u8.ToArray()).Class);
        Assert.Equal(FileClass.Executable, FileTypePolicy.Classify([0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2]).Class);   // universal Mach-O
        Assert.Equal(FileClass.Other, FileTypePolicy.Classify([0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 52]).Class);       // a Java class is not
        Assert.Equal(FileClass.Other, FileTypePolicy.Classify("MZ but no PE header"u8.ToArray()).Class);
    }

    [Fact]
    public void A_blocklisted_extension_on_the_display_name_counts_but_only_the_last_one()
    {
        Assert.True(FileTypePolicy.HasBlockedExtension("setup.EXE"));
        Assert.True(FileTypePolicy.HasBlockedExtension("run.sh"));
        Assert.False(FileTypePolicy.HasBlockedExtension("evil.exe.png"));
        Assert.False(FileTypePolicy.HasBlockedExtension("notes"));
        Assert.False(FileTypePolicy.HasBlockedExtension(null));
    }

    private static byte[] ZipEntry(ushort flags, byte[]? name = null, int dataLength = 4)
    {
        name ??= "a.txt"u8.ToArray();
        var b = new byte[30 + name.Length + dataLength];
        new byte[] { 0x50, 0x4B, 0x03, 0x04, 20, 0 }.CopyTo(b, 0);
        BitConverter.GetBytes(flags).CopyTo(b, 6);
        BitConverter.GetBytes((uint)dataLength).CopyTo(b, 18);
        BitConverter.GetBytes((uint)dataLength).CopyTo(b, 22);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(b, 26);
        name.CopyTo(b, 30);
        return b;
    }

    [Fact]
    public void Encrypted_zip_entries_are_found_even_after_a_plain_one()
    {
        Assert.Equal(FileClass.Other, FileTypePolicy.Classify(ZipEntry(0)).Class);
        Assert.Equal("application/zip", FileTypePolicy.Classify(ZipEntry(0)).Type!.Mime);
        Assert.Equal(FileClass.EncryptedArchive, FileTypePolicy.Classify(ZipEntry(1)).Class);
        Assert.Equal(FileClass.EncryptedArchive, FileTypePolicy.Classify(ZipEntry(0).Concat(ZipEntry(1)).ToArray()).Class);
    }

    [Fact]
    public void Encrypted_rar_archives_are_found_and_pdf_and_images_are_other()
    {
        // RAR 5 with an archive encryption header (type 4) right after the signature
        var rar5 = new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, 0, 0, 0, 0, 0x05, 0x04, 0, 0, 0, 0 };
        Assert.Equal(FileClass.EncryptedArchive, FileTypePolicy.Classify(rar5).Class);
        rar5[13] = 1; // main archive header
        Assert.Equal(FileClass.Other, FileTypePolicy.Classify(rar5).Class);
        Assert.Equal("application/pdf", FileTypePolicy.Classify("%PDF-1.7 ..."u8.ToArray()).Type!.Mime);
        Assert.Equal("image/png", FileTypePolicy.Classify([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]).Type!.Mime);
    }

    [Fact]
    public void Executables_are_blocked_for_guests_and_free_by_default_and_the_list_is_configuration()
    {
        var options = new UploadOptions();
        Assert.True(options.BlocksExecutablesFor(isGuest: true, isPaid: false));
        Assert.True(options.BlocksExecutablesFor(isGuest: false, isPaid: false));
        Assert.False(options.BlocksExecutablesFor(isGuest: false, isPaid: true));
        options.BlockExecutablesFor = "guest";
        Assert.False(options.BlocksExecutablesFor(isGuest: false, isPaid: false));
    }
}
