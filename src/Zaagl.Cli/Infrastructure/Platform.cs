using System.Diagnostics;
using System.Runtime.InteropServices;
using Zaagl.Cli.Config;

namespace Zaagl.Cli.Infrastructure;

public static class PlatformInfo
{
    public static string OsName =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : "other";

    public static string Arch => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>Display data sent with the device-code request (at most 32 characters, ARC-014 §2.1).</summary>
    public static string DeviceOs => Truncate($"{OsName} {Arch}", 32);

    /// <summary>Display name for the device-session list (at most 64 characters).</summary>
    public static string DeviceName(Func<string, string?> env)
    {
        string name;
        try { name = Environment.MachineName; }
        catch (InvalidOperationException) { name = "unknown"; }
        return Truncate(TextFormat.Safe(name), 64);
    }

    public static string Version =>
        System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(PlatformInfo).Assembly)
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string UserAgent => $"zaagl-cli/{Version} ({OsName}; {Arch})";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Opens the verification page. Only http(s) URLs are ever handed to the OS, and never through a shell.</summary>
public interface IBrowserLauncher
{
    /// <summary>True when a browser was (probably) opened.</summary>
    bool TryOpen(Uri url);

    /// <summary>False over SSH or without a display, where opening a browser makes no sense.</summary>
    bool CanOpen(Func<string, string?> env);
}

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public bool CanOpen(Func<string, string?> env)
    {
        if (!string.IsNullOrEmpty(env("SSH_CONNECTION")) || !string.IsNullOrEmpty(env("SSH_TTY"))) return false;
        if (OperatingSystem.IsLinux())
            return !string.IsNullOrEmpty(env("DISPLAY")) || !string.IsNullOrEmpty(env("WAYLAND_DISPLAY"));
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }

    public bool TryOpen(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) return false;
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
                psi = new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true };
            else if (OperatingSystem.IsMacOS())
                psi = new ProcessStartInfo("open") { UseShellExecute = false, ArgumentList = { url.AbsoluteUri } };
            else
                psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { url.AbsoluteUri } };

            psi.RedirectStandardOutput = !psi.UseShellExecute;
            psi.RedirectStandardError = !psi.UseShellExecute;
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Cross-process mutual exclusion (CLI-006): parallel <c>zaagl</c> processes must not refresh the same
/// refresh token, or the server's reuse detection revokes the whole session.</summary>
public interface ICrossProcessLock
{
    Task<IDisposable> AcquireAsync(string name, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// A lock file opened with <see cref="FileShare.None"/>: a sharing lock on Windows, an advisory <c>flock</c> on Unix
/// (.NET maps FileShare.None to LOCK_EX). The OS drops it when the process dies, so a crash cannot leave it stuck.
/// The file holds no data and no secret.
/// </summary>
public sealed class FileCrossProcessLock(string directory) : ICrossProcessLock
{
    public async Task<IDisposable> AcquireAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        ConfigPaths.EnsureDirectory(directory);
        var path = Path.Combine(directory, name + ".lock");
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (waited.Elapsed < timeout)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new CliException(ExitCodes.Unexpected,
                    "Another zaagl process is busy refreshing the sign-in. Try again in a moment.", "lock-timeout");
            }
        }
    }
}
