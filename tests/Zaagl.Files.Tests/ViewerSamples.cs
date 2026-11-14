using System.Buffers.Binary;
using System.Text;

namespace Zaagl.Files.Tests;

/// <summary>Builders for viewer test files (also used by the API tests through a linked copy).</summary>
public static class ViewerSamples
{
    public const string MinimalGltfJson =
        "{\"asset\":{\"version\":\"2.0\"},\"scenes\":[{\"nodes\":[]}],\"buffers\":[{\"byteLength\":4}]}";

    public static byte[] Glb(string json, byte[]? bin = null, uint? declaredLength = null)
    {
        var jsonBytes = Pad(Encoding.UTF8.GetBytes(json), (byte)' ');
        var binBytes = bin is null ? null : Pad(bin, 0);
        var total = 12 + 8 + jsonBytes.Length + (binBytes is null ? 0 : 8 + binBytes.Length);
        var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        ms.Write("glTF"u8); U32(2); U32(declaredLength ?? (uint)total);
        U32((uint)jsonBytes.Length); ms.Write("JSON"u8); ms.Write(jsonBytes);
        if (binBytes is not null) { U32((uint)binBytes.Length); ms.Write("BIN\0"u8); ms.Write(binBytes); }
        return ms.ToArray();
    }

    private static byte[] Pad(byte[] data, byte with)
    {
        var padded = new byte[(data.Length + 3) / 4 * 4];
        Array.Fill(padded, with);
        data.CopyTo(padded, 0);
        return padded;
    }

    public static string Lottie(string extra = "", string layers = "[{\"ty\":4,\"ks\":{}}]") =>
        "{\"v\":\"5.7.4\",\"fr\":30,\"ip\":0,\"op\":60,\"w\":512,\"h\":512,\"nm\":\"qa\",\"ddd\":0,\"assets\":[]" + extra + ",\"layers\":" + layers + "}";

    /// <summary>An MPEG-1 Layer III frame header (128 kbit/s, 44.1 kHz) followed by silence.</summary>
    public static byte[] Mp3Frames()
    {
        var data = new byte[4180];
        for (var i = 0; i < data.Length; i += 418) { data[i] = 0xFF; data[i + 1] = 0xFB; data[i + 2] = 0x90; data[i + 3] = 0x64; }
        return data;
    }

    public static byte[] Wav()
    {
        var ms = new MemoryStream();
        ms.Write("RIFF"u8); ms.Write(BitConverter.GetBytes(36 + 800)); ms.Write("WAVEfmt "u8);
        ms.Write(BitConverter.GetBytes(16)); ms.Write(BitConverter.GetBytes((short)1)); ms.Write(BitConverter.GetBytes((short)1));
        ms.Write(BitConverter.GetBytes(8000)); ms.Write(BitConverter.GetBytes(16000)); ms.Write(BitConverter.GetBytes((short)2));
        ms.Write(BitConverter.GetBytes((short)16)); ms.Write("data"u8); ms.Write(BitConverter.GetBytes(800)); ms.Write(new byte[800]);
        return ms.ToArray();
    }
}
