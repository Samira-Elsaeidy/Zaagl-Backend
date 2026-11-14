using System.Text;
using Zaagl.Modules.Files.Application;

namespace Zaagl.Files.Tests;

/// <summary>Phase 2 E2.2 (docs/phase2/viewers-3d-lottie-audio.md): content sniffing of viewer candidates and the full-file validation.</summary>
public sealed class ViewerTypeTests
{
    private static string? Sniff(byte[] head) => FileTypePolicy.Classify(head).Type?.Mime;

    [Fact]
    public void Viewer_candidates_are_recognised_by_their_bytes_only()
    {
        Assert.Equal("model/gltf-binary", Sniff(ViewerSamples.Glb(ViewerSamples.MinimalGltfJson)));
        Assert.Equal("model/gltf+json", Sniff(Encoding.UTF8.GetBytes("﻿  " + ViewerSamples.MinimalGltfJson)));
        Assert.Equal("application/json", Sniff(Encoding.UTF8.GetBytes(ViewerSamples.Lottie())));
        Assert.Equal("audio/mpeg", Sniff([.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("audio/mpeg", Sniff(ViewerSamples.Mp3Frames()));
        Assert.Equal("audio/wav", Sniff(ViewerSamples.Wav()));
        Assert.Equal("audio/flac", Sniff([.. "fLaC"u8, 0, 0, 0, 34]));
        Assert.Equal("audio/ogg", Sniff([.. "OggS"u8, 0, 2, 0, 0]));
        Assert.Equal("audio/aac", Sniff([0xFF, 0xF1, 0x50, 0x80, 0x02, 0x1F, 0xFC]));
        Assert.Equal("audio/mp4", Sniff([0, 0, 0, 0x20, .. "ftypM4A "u8, 0, 0, 0, 0]));

        // Not candidates: plain JSON, a glTF with the wrong version, a JPEG, an MPEG sync with a reserved bitrate.
        Assert.Null(Sniff("{\"name\":\"package\",\"version\":\"1.0.0\"}"u8.ToArray()));
        var badVersion = ViewerSamples.Glb(ViewerSamples.MinimalGltfJson);
        badVersion[4] = 1;
        Assert.Null(Sniff(badVersion));
        Assert.Equal("image/jpeg", Sniff([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]));
        Assert.Null(Sniff([0xFF, 0xFB, 0xF0, 0x64, 0, 0]));
    }

    private static async Task<ViewerVerdict> ValidateAsync(byte[] data, string mime)
    {
        var path = Path.Combine(Path.GetTempPath(), "zaagl-viewer-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, data);
        try { return await ViewerValidator.ValidateAsync(path, mime, data.Length, 1024 * 1024, CancellationToken.None); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Gltf_must_be_self_contained()
    {
        Assert.Equal(ViewerKind.Model3d, (await ValidateAsync(ViewerSamples.Glb(ViewerSamples.MinimalGltfJson, [1, 2, 3, 4]), "model/gltf-binary")).Viewer);

        var external = ViewerSamples.Glb("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"https://evil.example/b.bin\",\"byteLength\":4}]}");
        var verdict = await ValidateAsync(external, "model/gltf-binary");
        Assert.True(verdict.Reject);
        Assert.Equal("unsafe-content", verdict.Reason);
        var image = ViewerSamples.Glb("{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"tex.png\"}]}");
        Assert.True((await ValidateAsync(image, "model/gltf-binary")).Reject);
        var nested = ViewerSamples.Glb("{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"X\":{\"deep\":[{\"uri\":\"//cdn.example/x\"}]}}}");
        Assert.True((await ValidateAsync(nested, "model/gltf-binary")).Reject);

        // Malformed: no viewer, not rejected (served as a plain download).
        var lying = ViewerSamples.Glb(ViewerSamples.MinimalGltfJson, declaredLength: 9999);
        Assert.Equal(ViewerVerdict.None with { Reason = "length-mismatch" }, await ValidateAsync(lying, "model/gltf-binary"));
        Assert.Null((await ValidateAsync(ViewerSamples.Glb("{\"asset\":{\"version\":\"1.0\"}}"), "model/gltf-binary")).Viewer);

        var embedded = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"data:application/octet-stream;base64,AAAAAA==\",\"byteLength\":4}]}";
        Assert.Equal(ViewerKind.Model3d, (await ValidateAsync(Encoding.UTF8.GetBytes(embedded), "model/gltf+json")).Viewer);
        Assert.True((await ValidateAsync(Encoding.UTF8.GetBytes(embedded.Replace("data:application/octet-stream;base64,AAAAAA==", "file:///etc/passwd")), "model/gltf+json")).Reject);
    }

    [Fact]
    public async Task Lottie_must_have_no_expressions_and_only_embedded_assets()
    {
        Assert.Equal(ViewerKind.Lottie, (await ValidateAsync(Encoding.UTF8.GetBytes(ViewerSamples.Lottie()), "application/json")).Viewer);
        // A split position uses objects named "x"; that is not an expression.
        var split = ViewerSamples.Lottie(layers: "[{\"ty\":4,\"ks\":{\"p\":{\"s\":true,\"x\":{\"a\":0,\"k\":10},\"y\":{\"a\":0,\"k\":10}}}}]");
        Assert.Equal(ViewerKind.Lottie, (await ValidateAsync(Encoding.UTF8.GetBytes(split), "application/json")).Viewer);

        var expression = ViewerSamples.Lottie(layers: "[{\"ty\":4,\"ks\":{\"o\":{\"a\":0,\"k\":100,\"x\":\"var $bm_rt = fetch('https://evil.example')\"}}}]");
        Assert.True((await ValidateAsync(Encoding.UTF8.GetBytes(expression), "application/json")).Reject);
        var remoteAsset = ViewerSamples.Lottie().Replace("\"assets\":[]", "\"assets\":[{\"id\":\"i0\",\"w\":10,\"h\":10,\"u\":\"https://evil.example/\",\"p\":\"a.png\"}]");
        Assert.True((await ValidateAsync(Encoding.UTF8.GetBytes(remoteAsset), "application/json")).Reject);
        var embeddedAsset = ViewerSamples.Lottie().Replace("\"assets\":[]", "\"assets\":[{\"id\":\"i0\",\"w\":1,\"h\":1,\"e\":1,\"p\":\"data:image/png;base64,iVBORw0KGgo=\"}]");
        Assert.Equal(ViewerKind.Lottie, (await ValidateAsync(Encoding.UTF8.GetBytes(embeddedAsset), "application/json")).Viewer);

        // JSON that looked like Lottie but is not one loses the type (KeepType false), it is not rejected.
        var notLottie = await ValidateAsync("{\"layers\":[],\"fr\":\"x\",\"ip\":0,\"op\":1}"u8.ToArray(), "application/json");
        Assert.False(notLottie.Reject);
        Assert.False(notLottie.KeepType);
        Assert.Null(notLottie.Viewer);
        var broken = await ValidateAsync("{\"layers\":[,"u8.ToArray(), "application/json");
        Assert.False(broken.Reject);
        Assert.Null(broken.Viewer);
    }

    [Fact]
    public async Task Audio_is_accepted_by_its_magic()
    {
        Assert.Equal(ViewerKind.Audio, (await ValidateAsync(ViewerSamples.Wav(), "audio/wav")).Viewer);
        Assert.Equal(ViewerKind.Audio, (await ValidateAsync(ViewerSamples.Mp3Frames(), "audio/mpeg")).Viewer);
    }
}
