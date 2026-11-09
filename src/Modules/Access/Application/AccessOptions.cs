using Zaagl.SharedKernel.Security;

namespace Zaagl.Modules.Access.Application;

/// <summary>Configuration section <c>Access</c> (ARC-004 §4.4). Grant keys come from the deployment secret store.</summary>
public sealed class AccessOptions
{
    public const string Section = "Access";

    /// <summary>Addendum §8.1.6: access grants live 10 minutes by default (technical default, configuration).</summary>
    public int GrantMinutes { get; set; } = 10;

    /// <summary>Rotating key set; the first entry signs, every entry verifies. Keys are base64, 32 bytes or more.</summary>
    public List<HmacKeyEntry> GrantKeys { get; set; } = [];
}

/// <summary>The grant key ring, built once at startup (singleton).</summary>
public sealed class AccessGrantKeyRing(HmacKeyRing ring)
{
    /// <summary>ARC-004 §4.2: the MAC domain of access grants (tickets use <c>zaagl/ticket/v1</c>).</summary>
    public const string Domain = "zaagl/grant/v1\n";

    public HmacKeyRing Ring { get; } = ring;
}
