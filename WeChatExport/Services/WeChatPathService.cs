using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace WeChatExport.Services;

public class WeChatPathService
{
    private static readonly string[] CommonPaths = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "WeChat"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "WeChat"),
        @"C:\Program Files (x86)\Tencent\WeChat",
        @"C:\Program Files\Tencent\WeChat"
    };

    public IEnumerable<string> GetDefaultPaths() => CommonPaths;

    public string? DetectWeChatPath()
    {
        // Try registry first
        var registryPath = GetPathFromRegistry();
        if (!string.IsNullOrEmpty(registryPath) && IsValidWeChatPath(registryPath))
            return registryPath;

        // Scan common paths
        foreach (var path in CommonPaths)
        {
            if (IsValidWeChatPath(path))
                return path;
        }

        return null;
    }

    private string? GetPathFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Tencent\WeChat");
            return key?.GetValue("InstallPath") as string;
        }
        catch
        {
            return null;
        }
    }

    public bool IsValidWeChatPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return false;

        var wechatExe = Path.Combine(path, "WeChat.exe");
        return File.Exists(wechatExe);
    }
}
