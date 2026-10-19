using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop;

internal static class PrivateStorage
{
    public static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("DEEPSEEK_DESKTOP_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekDesktop"));
    public static string SettingsPath => Path.Combine(Root, "settings.dpapi");

    public static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法识别当前 Windows 用户。");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(user);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    public static Settings Load()
    {
        SecureDirectory(Root);
        if (!File.Exists(SettingsPath)) return new Settings();
        var clear = Protect(File.ReadAllBytes(SettingsPath), false);
        try { return JsonSerializer.Deserialize<Settings>(clear) ?? throw new IOException("设置文件为空。"); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static void Save(Settings value)
    {
        value.Validate(false);
        SecureDirectory(Root);
        var clear = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var encrypted = Protect(clear, true);
            var temp = SettingsPath + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, SettingsPath, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static void CleanRuntime(string? path)
    {
        if (path == null || !Directory.Exists(path)) return;
        // Only directories created by this app; do not traverse reparse points.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("运行目录不能是目录链接。");
        Directory.Delete(path, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Protect(byte[] input, bool encrypt)
    {
        var source = new Blob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            bool ok = encrypt
                ? CryptProtectData(ref source, "DeepSeek Desktop settings", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 凭据加解密失败；请使用保存设置时的 Windows 账号。");
            var bytes = new byte[output.Length];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            for (int i = 0; i < source.Length; i++) Marshal.WriteByte(source.Data, i, 0);
            Marshal.FreeHGlobal(source.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}
