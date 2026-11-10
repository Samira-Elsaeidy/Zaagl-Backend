using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Zaagl.Cli.Auth;

/// <summary>Windows Credential Manager (CredWriteW / CredReadW / CredDeleteW), per user, persisted for the local
/// machine (not roamed). CLI-004.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Name => "Windows Credential Manager";

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in CREDENTIAL credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);

    public byte[]? Read(string key)
    {
        if (!CredRead(key, CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new CredentialStoreUnavailableException($"{Name} refused the read (error {error}).");
        }
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            var blob = new byte[c.CredentialBlobSize];
            if (blob.Length > 0) Marshal.Copy(c.CredentialBlob, blob, 0, blob.Length);
            return blob;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Write(string key, byte[] secret)
    {
        var target = Marshal.StringToHGlobalUni(key);
        var user = Marshal.StringToHGlobalUni("zaagl");
        var blob = Marshal.AllocHGlobal(Math.Max(secret.Length, 1));
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var c = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(in c, 0))
                throw new CredentialStoreUnavailableException($"{Name} refused the write (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
            Marshal.FreeHGlobal(blob);
        }
    }

    public bool Delete(string key)
    {
        if (CredDelete(key, CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new CredentialStoreUnavailableException($"{Name} refused the delete (error {error}).");
    }
}
