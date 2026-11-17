using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ProbeDesktop.Core;

/// <summary>DPAPI CurrentUser protection. Never falls back to plaintext on another platform.</summary>
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("生产凭据保护仅支持 Windows 当前用户 DPAPI。");
        Blob input = default, output = default;
        try
        {
            input.Length = value.Length;
            input.Data = Marshal.AllocHGlobal(Math.Max(value.Length, 1));
            Marshal.Copy(value, 0, input.Data, value.Length);
            bool success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success)
                throw new CryptographicException("Windows 当前用户数据保护操作失败。", new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Length < 0 || output.Length > 64 * 1024 * 1024)
                throw new CryptographicException("Windows 数据保护返回了无效长度。");
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroNative(input.Data, input.Length);
            if (input.Data != IntPtr.Zero) Marshal.FreeHGlobal(input.Data);
            ZeroNative(output.Data, Math.Max(output.Length, 0));
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private static void ZeroNative(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero) return;
        for (int i = 0; i < length; i++) Marshal.WriteByte(pointer, i, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
