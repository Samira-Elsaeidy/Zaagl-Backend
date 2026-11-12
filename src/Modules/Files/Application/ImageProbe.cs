using System.Buffers.Binary;

namespace Zaagl.Modules.Files.Application;

/// <summary>What the header scan learned about a PNG, JPEG, GIF or WebP file. Dimensions are the stored ones (before EXIF orientation).</summary>
/// <param name="FrameCount">Frames when animated (counted up to the walk limit; null when the limit was hit or it is a still).</param>
public sealed record ImageHeader(string Mime, long Width, long Height, bool HasAlpha, bool Animated, int? FrameCount);

/// <summary>
/// Header-only probe (ARC-011 sections 3.1 and 6): reads chunk and marker headers by seeking, never decodes a pixel, never opens
/// the file for write (invariant I1). It is the decompression-bomb gate: the caller compares <c>Width * Height</c> with the
/// configured limit before any decoder is created. Every loop is bounded (marker, chunk and frame counts), so a crafted file cannot
/// make the probe spin. A file that does not parse yields null (fail closed to "no metadata", not to an error).
/// </summary>
public static class ImageProbe
{
    private const int MaxSegments = 4096;
    private const int MaxFramesWalked = 4096;

    public static ImageHeader? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.RandomAccess, BufferSize = 8192,
            });
            Span<byte> head = stackalloc byte[12];
            if (file.Read(head) < 6) return null;
            file.Position = 0;
            if (head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return Png(file);
            if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return Jpeg(file);
            if (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8)) return Gif(file);
            if (head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return WebP(file);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    // ---- PNG: IHDR, then chunks up to the first IDAT (tRNS and acTL come before it) --------------------------------

    private static ImageHeader? Png(FileStream f)
    {
        f.Position = 8;
        long width = 0, height = 0;
        var colorType = -1;
        var transparency = false;
        var animated = false;
        int? frames = null;
        Span<byte> buf = stackalloc byte[24];
        var h = buf[..8];
        for (var i = 0; i < MaxSegments; i++)
        {
            // Running out of bytes after a valid IHDR still tells the declared size (the decoder will then fail on the body).
            if (f.Read(h) < 8) { if (i == 0) return null; break; }
            var length = BinaryPrimitives.ReadUInt32BigEndian(h);
            var type = h.Slice(4, 4);
            if (i == 0)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) return null;
                var ihdr = buf.Slice(8, 13);
                if (f.Read(ihdr) < 13) return null;
                width = BinaryPrimitives.ReadUInt32BigEndian(ihdr);
                height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]);
                colorType = ihdr[9];
                f.Position += 4; // CRC
                continue;
            }
            if (type.SequenceEqual("IDAT"u8)) break;
            if (type.SequenceEqual("tRNS"u8)) transparency = true;
            if (type.SequenceEqual("acTL"u8) && length >= 8)
            {
                var actl = buf.Slice(8, 8);
                if (f.Read(actl) < 8) return null;
                var n = BinaryPrimitives.ReadUInt32BigEndian(actl);
                animated = n > 1;
                frames = n is > 1 and <= int.MaxValue ? (int)n : null;
                f.Position += (long)length - 8 + 4;
                continue;
            }
            f.Position += (long)length + 4;
            if (f.Position > f.Length) break;
        }
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue) return null;
        var alpha = colorType is 4 or 6 || transparency;
        return new ImageHeader("image/png", width, height, alpha, animated, animated ? frames : null);
    }

    // ---- JPEG: marker walk to the first start-of-frame ------------------------------------------------------------

    private static ImageHeader? Jpeg(FileStream f)
    {
        f.Position = 2;
        Span<byte> buf = stackalloc byte[24];
        var b = buf[..4];
        for (var i = 0; i < MaxSegments; i++)
        {
            int c;
            // Find the next marker: 0xFF followed by a non-0x00, non-0xFF byte.
            do { c = f.ReadByte(); } while (c is not (-1 or 0xFF));
            if (c == -1) return null;
            int marker;
            do { marker = f.ReadByte(); } while (marker == 0xFF);
            if (marker is -1) return null;
            if (marker == 0x00 || marker == 0x01 || marker is >= 0xD0 and <= 0xD9) continue; // standalone markers
            if (f.Read(b[..2]) < 2) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(b);
            if (length < 2) return null;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8) return null;
                var sof = buf.Slice(8, 5);
                if (f.Read(sof) < 5) return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(sof.Slice(1, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(sof.Slice(3, 2));
                if (width == 0 || height == 0) return null; // height 0 means a DNL marker follows: not supported, no thumbnail
                return new ImageHeader("image/jpeg", width, height, false, false, null);
            }
            if (marker == 0xDA) return null; // start of scan before any frame header
            f.Position += length - 2;
            if (f.Position > f.Length) return null;
        }
        return null;
    }

    // ---- GIF: logical screen, then blocks (frames, graphic control extensions) -------------------------------------

    private static ImageHeader? Gif(FileStream f)
    {
        f.Position = 6;
        Span<byte> buf = stackalloc byte[24];
        var lsd = buf[..7];
        if (f.Read(lsd) < 7) return null;
        long width = BinaryPrimitives.ReadUInt16LittleEndian(lsd);
        long height = BinaryPrimitives.ReadUInt16LittleEndian(lsd[2..]);
        if (width == 0 || height == 0) return null;
        if ((lsd[4] & 0x80) != 0) f.Position += 3L * (1 << ((lsd[4] & 7) + 1));
        var frames = 0;
        var transparent = false;
        var capped = false;
        for (var i = 0; i < MaxSegments * 64; i++)
        {
            var intro = f.ReadByte();
            if (intro is -1 or 0x3B) break;
            if (intro == 0x21) // extension
            {
                var label = f.ReadByte();
                if (label == 0xF9) // graphic control extension
                {
                    var size = f.ReadByte();
                    if (size >= 4)
                    {
                        var gce = buf.Slice(8, 4);
                        if (f.Read(gce) < 4) break;
                        if ((gce[0] & 1) != 0) transparent = true;
                        f.Position += size - 4;
                    }
                    else f.Position += Math.Max(0, size);
                }
                if (!SkipSubBlocks(f)) break;
            }
            else if (intro == 0x2C) // image descriptor
            {
                var d = buf.Slice(8, 9);
                if (f.Read(d) < 9) break;
                if ((d[8] & 0x80) != 0) f.Position += 3L * (1 << ((d[8] & 7) + 1));
                f.ReadByte(); // LZW minimum code size
                if (!SkipSubBlocks(f)) break;
                frames++;
                if (frames >= MaxFramesWalked) { capped = true; break; }
            }
            else break;
        }
        var animated = frames > 1;
        return new ImageHeader("image/gif", width, height, transparent, animated, animated && !capped ? frames : null);
    }

    private static bool SkipSubBlocks(FileStream f)
    {
        while (true)
        {
            var size = f.ReadByte();
            if (size < 0) return false;
            if (size == 0) return true;
            f.Position += size;
            if (f.Position > f.Length) return false;
        }
    }

    // ---- WebP: RIFF chunks (VP8X canvas and flags, VP8 and VP8L frame headers, ANMF frames) ------------------------

    private static ImageHeader? WebP(FileStream f)
    {
        f.Position = 12;
        Span<byte> buf = stackalloc byte[24];
        var h = buf[..8];
        long width = 0, height = 0;
        var alpha = false;
        var animatedFlag = false;
        var haveDims = false;
        var frames = 0;
        var capped = false;
        for (var i = 0; i < MaxSegments; i++)
        {
            if (f.Read(h) < 8) break;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            var padded = (long)size + (size & 1);
            var type = h[..4];
            var dataStart = f.Position;
            if (type.SequenceEqual("VP8X"u8) && size >= 10)
            {
                var x = buf.Slice(8, 10);
                if (f.Read(x) < 10) return null;
                alpha |= (x[0] & 0x10) != 0;
                animatedFlag = (x[0] & 0x02) != 0;
                width = 1 + (x[4] | (x[5] << 8) | (x[6] << 16));
                height = 1 + (x[7] | (x[8] << 8) | (x[9] << 16));
                haveDims = true;
            }
            else if (type.SequenceEqual("VP8L"u8) && size >= 5)
            {
                var l = buf.Slice(8, 5);
                if (f.Read(l) < 5 || l[0] != 0x2F) return null;
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(l[1..]);
                if (!haveDims)
                {
                    width = 1 + (bits & 0x3FFF);
                    height = 1 + ((bits >> 14) & 0x3FFF);
                    haveDims = true;
                }
                alpha |= ((bits >> 28) & 1) != 0;
            }
            else if (type.SequenceEqual("VP8 "u8) && size >= 10)
            {
                var v = buf.Slice(8, 10);
                if (f.Read(v) < 10 || v[3] != 0x9D || v[4] != 0x01 || v[5] != 0x2A) return null;
                if (!haveDims)
                {
                    width = BinaryPrimitives.ReadUInt16LittleEndian(v[6..]) & 0x3FFF;
                    height = BinaryPrimitives.ReadUInt16LittleEndian(v[8..]) & 0x3FFF;
                    haveDims = true;
                }
            }
            else if (type.SequenceEqual("ALPH"u8)) alpha = true;
            else if (type.SequenceEqual("ANMF"u8))
            {
                frames++;
                if (frames >= MaxFramesWalked) { capped = true; break; }
            }
            f.Position = dataStart + padded;
            if (f.Position > f.Length) break;
        }
        if (!haveDims || width <= 0 || height <= 0) return null;
        var animated = animatedFlag && (frames > 1 || capped);
        return new ImageHeader("image/webp", width, height, alpha, animated, animated && !capped ? frames : null);
    }
}
