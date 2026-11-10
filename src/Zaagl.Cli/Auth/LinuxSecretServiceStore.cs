namespace Zaagl.Cli.Auth;

/// <summary>
/// Linux Secret Service (libsecret). NOT IMPLEMENTED in this increment: libsecret's store function takes a C varargs
/// attribute list and a SecretSchema struct, which cannot be called safely through LibraryImport without a native
/// shim, and it could not be exercised from the Windows development machine. Until it is built and tested on a Linux
/// runner the store reports itself unavailable: nothing is written anywhere (never a plain file) and the user is told
/// to use ZAAGL_TOKEN or ZAAGL_API_KEY (CredentialStores.HeadlessGuidance).
/// </summary>
public sealed class LinuxSecretServiceStore : ICredentialStore
{
    public string Name => "Secret Service (not yet supported)";
    public byte[]? Read(string key) => null;
    public void Write(string key, byte[] secret) => throw new CredentialStoreUnavailableException(CredentialStores.HeadlessGuidance);
    public bool Delete(string key) => false;
}
