using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Links.Tests;

public class VectorGenTrace(ITestOutputHelper output)
{
    [Fact]
    public void GenerateAndTraceVectors()
    {
        var keyBytes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var perm = new FeistelPermutation(keyBytes);
        var gen = new CodeGenerator(perm);

        // ---- hand-trace n=0, length=5, round by round ----
        output.WriteLine("=== TRACE: length=5, n=0 ===");
        var subkey = HKDF.DeriveKey(HashAlgorithmName.SHA256, keyBytes, 32,
            salt: Encoding.UTF8.GetBytes("zaagl/codes"), info: Encoding.UTF8.GetBytes("perm/len5"));
        output.WriteLine("subkey = " + Convert.ToHexString(subkey));

        long l = 0, r = 0; // n=0 -> l=0, r=0 (h=15, mask=0x7FFF)
        const int h = 15;
        long mask = (1L << h) - 1;
        for (var i = 0; i < 10; i++)
        {
            Span<byte> msg = stackalloc byte[9];
            msg[0] = (byte)i;
            BinaryPrimitives.WriteUInt64BigEndian(msg[1..], (ulong)r);
            Span<byte> hash = stackalloc byte[32];
            HMACSHA256.HashData(subkey, msg, hash);
            var f = (long)BinaryPrimitives.ReadUInt64BigEndian(hash) & mask;
            var newL = r;
            var newR = l ^ f;
            output.WriteLine($"round {i}: F(r={r})={f} -> l={newL}, r={newR}");
            l = newL; r = newR;
        }
        var x5_0 = (l << h) | r;
        output.WriteLine($"after 10 rounds (before cycle-walk): x = {x5_0} (0x{x5_0:X})");
        output.WriteLine($"62^5 = {CodeLengths.PowerOf62(5)}, needs cycle-walk: {x5_0 >= CodeLengths.PowerOf62(5)}");

        var encoded0 = gen.Encode(5, 0);
        output.WriteLine($"Encode(length=5, n=0) = \"{encoded0}\"");
        var decoded0 = gen.Decode(5, encoded0);
        output.WriteLine($"Decode(\"{encoded0}\") = {decoded0} (expect 0, match={decoded0 == 0})");
        Assert.Equal(0L, decoded0);

        var encoded1 = gen.Encode(5, 1);
        output.WriteLine($"Encode(length=5, n=1) = \"{encoded1}\"");
        var decoded1 = gen.Decode(5, encoded1);
        output.WriteLine($"Decode(\"{encoded1}\") = {decoded1} (expect 1, match={decoded1 == 1})");
        Assert.Equal(1L, decoded1);
        Assert.NotEqual(encoded0, encoded1);

        // ---- full vector table ----
        output.WriteLine("");
        output.WriteLine("=== VECTORS ===");
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"keyBytes\": \"" + Convert.ToHexString(keyBytes) + "\",");
        sb.AppendLine("  \"generatorCaps\": {");
        for (var L = 5; L <= 10; L++)
            sb.AppendLine($"    \"{L}\": {CodeLengths.GeneratorCap(L)}{(L < 10 ? "," : "")}");
        sb.AppendLine("  },");
        sb.AppendLine("  \"vectors\": [");
        var rows = new List<string>();
        for (var L = 5; L <= 10; L++)
        {
            foreach (var n in new[] { 0L, 1L, 2L, 1000L, CodeLengths.GeneratorCap(L) - 1 })
            {
                var code = gen.Encode(L, n);
                var back = gen.Decode(L, code);
                Assert.Equal(n, back);
                output.WriteLine($"L={L} n={n} -> \"{code}\" (round-trip OK)");
                rows.Add($"    {{ \"length\": {L}, \"n\": {n}, \"code\": \"{code}\" }}");
            }
        }
        sb.AppendLine(string.Join(",\n", rows));
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "vectors.json");
        File.WriteAllText(path, sb.ToString());
        output.WriteLine("");
        output.WriteLine("Wrote vectors to: " + Path.GetFullPath(path));
    }
}
