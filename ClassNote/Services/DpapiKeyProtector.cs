using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassNote.Services;

/// <summary>
/// 基于 Windows DPAPI（CryptProtectData / CryptUnprotectData，CurrentUser 作用域）
/// 的敏感字符串加解密，用于本地 API Key 落盘保护。
/// 说明：加密结果绑定当前 Windows 用户；换用户/换机器后无法解密（这是 DPAPI 的设计语义，
/// 也意味着迁移机器需重新配置 Key）。解密失败时返回空串，由上层提示用户重新配置。
/// </summary>
internal static class DpapiKeyProtector
{
    private const string Prefix = "dpapi:";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>加密明文 Key，返回带前缀的 Base64（空输入返回空串）。</summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        if (plainText.StartsWith(Prefix, StringComparison.Ordinal)) return plainText; // 已加密

        var inBlob = CreateBlob(Encoding.UTF8.GetBytes(plainText));
        var outBlob = new DATA_BLOB();
        var entropy = new DATA_BLOB(); // 可选熵：不使用
        try
        {
            if (!CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                throw new InvalidOperationException("CryptProtectData 失败，错误码 " + Marshal.GetLastWin32Error());
            var cipher = ReadBlob(outBlob);
            return Prefix + Convert.ToBase64String(cipher);
        }
        finally
        {
            FreeBlob(inBlob);
            FreeBlob(outBlob);
        }
    }

    /// <summary>解密存储值。带前缀则解密；无前缀视为旧版明文原样返回；解密失败返回空串。</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // 旧版明文，兼容读取

        try
        {
            var cipher = Convert.FromBase64String(stored.Substring(Prefix.Length));
            var inBlob = CreateBlob(cipher);
            var outBlob = new DATA_BLOB();
            var entropy = new DATA_BLOB(); // 可选熵：不使用
            try
            {
                if (!CryptUnprotectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                    return ""; // 解密失败（换用户/机器）→ 视为无 Key
                return Encoding.UTF8.GetString(ReadBlob(outBlob));
            }
            finally
            {
                FreeBlob(inBlob);
                FreeBlob(outBlob);
            }
        }
        catch
        {
            return "";
        }
    }

    private static DATA_BLOB CreateBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length };
        blob.pbData = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] ReadBlob(DATA_BLOB blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero) return Array.Empty<byte>();
        var data = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, data, 0, blob.cbData);
        return data;
    }

    private static void FreeBlob(DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            LocalFree(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }
}
