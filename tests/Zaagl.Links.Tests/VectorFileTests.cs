using System.Text.Json;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Links.Tests;

/// <summary>
/// Reads the committed vectors.json (generated once by VectorGenTrace.GenerateAndTraceVectors against
/// the production algorithm, hand-trace-verified for L=5 n=0 and n=1) and re-asserts every row against
/// a fresh FeistelPermutation built from the same raw key bytes. This is the regression guard: if the
/// algorithm, bit widths, round count, PRF encoding or key derivation ever changes, this test fails
/// immediately - matching the spec's intent for a committed vector file (spec §5.2).
/// </summary>
public class VectorFileTests
{
    private sealed record VectorRow(int Length, long N, string Code);
    private sealed record VectorFile(string KeyBytes, Dictionary<string, long> GeneratorCaps, List<VectorRow> Vectors);

    private static VectorFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<VectorFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("vectors.json could not be parsed.");
    }

    [Fact]
    public void Generator_caps_match_the_committed_file()
    {
        var file = Load();
        foreach (var (lengthText, expectedCap) in file.GeneratorCaps)
            Assert.Equal(expectedCap, CodeLengths.GeneratorCap(int.Parse(lengthText)));
    }

    [Fact]
    public void Every_committed_vector_still_encodes_and_decodes_the_same_way()
    {
        var file = Load();
        var keyBytes = Convert.FromHexString(file.KeyBytes);
        var codes = new CodeGenerator(new FeistelPermutation(keyBytes));

        Assert.NotEmpty(file.Vectors);
        foreach (var row in file.Vectors)
        {
            var actual = codes.Encode(row.Length, row.N);
            Assert.Equal(row.Code, actual);
            Assert.Equal(row.N, codes.Decode(row.Length, actual));
        }
    }
}
