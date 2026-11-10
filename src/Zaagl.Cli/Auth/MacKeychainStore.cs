using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Zaagl.Cli.Auth;

/// <summary>
/// macOS Keychain through the Security.framework generic-password functions. The secret is passed in memory, never in
/// argv (unlike <c>security add-generic-password -w</c>). UNVERIFIED: written on Windows and never run on macOS.
/// These SecKeychain* functions are deprecated by Apple but still shipped; the SecItem* API (needs CoreFoundation
/// dictionaries) is the follow-up if they disappear.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacKeychainStore : ICredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Service = "zaa.gl cli";
    private const int ErrSecItemNotFound = -25300;

    public string Name => "macOS Keychain";

    [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static partial int FindGenericPassword(
        IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        out uint passwordLength, out IntPtr passwordData, out IntPtr item);

    [LibraryImport(Security, EntryPoint = "SecKeychainAddGenericPassword")]
    private static partial int AddGenericPassword(
        IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        uint passwordLength, byte[] passwordData, IntPtr item);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    private static partial int ModifyData(IntPtr item, IntPtr attrList, uint length, byte[] data);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemDelete")]
    private static partial int ItemDelete(IntPtr item);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemFreeContent")]
    private static partial int FreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void CFRelease(IntPtr cf);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static int Find(string key, out uint length, out IntPtr data, out IntPtr item)
    {
        var service = Utf8(Service);
        var account = Utf8(key);
        return FindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out length, out data, out item);
    }

    public byte[]? Read(string key)
    {
        var status = Find(key, out var length, out var data, out var item);
        if (status == ErrSecItemNotFound) return null;
        if (status != 0) throw new CredentialStoreUnavailableException($"{Name} refused the read (status {status}).");
        try
        {
            var blob = new byte[length];
            if (length > 0) Marshal.Copy(data, blob, 0, blob.Length);
            return blob;
        }
        finally
        {
            FreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public void Write(string key, byte[] secret)
    {
        int status;
        var find = Find(key, out _, out var data, out var item);
        if (find == 0)
        {
            FreeContent(IntPtr.Zero, data);
            status = ModifyData(item, IntPtr.Zero, (uint)secret.Length, secret);
            CFRelease(item);
        }
        else
        {
            var service = Utf8(Service);
            var account = Utf8(key);
            status = AddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                (uint)secret.Length, secret, IntPtr.Zero);
        }
        if (status != 0) throw new CredentialStoreUnavailableException($"{Name} refused the write (status {status}).");
    }

    public bool Delete(string key)
    {
        var find = Find(key, out _, out var data, out var item);
        if (find == ErrSecItemNotFound) return false;
        if (find != 0) throw new CredentialStoreUnavailableException($"{Name} refused the delete (status {find}).");
        FreeContent(IntPtr.Zero, data);
        var status = ItemDelete(item);
        CFRelease(item);
        if (status != 0) throw new CredentialStoreUnavailableException($"{Name} refused the delete (status {status}).");
        return true;
    }
}
