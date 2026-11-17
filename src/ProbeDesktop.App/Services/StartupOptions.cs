using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ProbeDesktop.App.Services;

public sealed record StartupOptions(string DataDirectory, bool IsCustomDataDirectory);

public static class StartupParser
{
    public const string DataDirSwitch = "--data-dir";

    // 常见云同步目录名称。数据目录（含密钥密文）不应放进这些位置。
    private static readonly string[] CloudFolderNames =
        ["OneDrive", "云端", "Dropbox", "iCloudDrive", "iCloud Drive", "Google Drive", "GoogleDrive", "坚果云", "BaiduNetdisk", "百度网盘"];

    public static bool TryParse(string[] args, out StartupOptions? options, out string error)
    {
        options = null;
        error = "";
        string? custom = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, DataDirSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (custom is not null)
                {
                    error = "参数 --data-dir 只能指定一次。";
                    return false;
                }

                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    error = "参数 --data-dir 后需要一个绝对本地路径。";
                    return false;
                }

                custom = args[++i];
            }
            else
            {
                error = "无法识别的启动参数。仅支持：--data-dir <绝对本地路径>";
                return false;
            }
        }

        if (custom is null)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                error = "无法定位 %LOCALAPPDATA% 目录。";
                return false;
            }

            options = new StartupOptions(Path.Combine(local, "ProbeDesktop"), false);
            return true;
        }

        if (!TryValidateCustomDirectory(custom, out var full, out error))
        {
            return false;
        }

        options = new StartupOptions(full, true);
        return true;
    }

    private static bool TryValidateCustomDirectory(string raw, out string full, out string error)
    {
        full = "";
        error = "";
        var text = raw.Trim().Trim('"');

        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
        {
            error = "--data-dir 不支持网络共享（UNC）或设备路径，请使用本地磁盘的绝对路径。";
            return false;
        }

        if (!Path.IsPathFullyQualified(text))
        {
            error = "--data-dir 必须是绝对本地路径，例如 D:\\ProbeTest\\data";
            return false;
        }

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                error = "--data-dir 路径无效。";
                return false;
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            {
                error = "--data-dir 必须位于本地磁盘，不支持网络驱动器或光驱。";
                return false;
            }

            if (string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                error = "--data-dir 不能是磁盘根目录，请指定一个专用文件夹。";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or System.Security.SecurityException)
        {
            error = "--data-dir 路径无效或无法访问。";
            return false;
        }

        if (IsInsideCloudFolder(full))
        {
            error = "--data-dir 位于云同步文件夹内（如 OneDrive、云端等）。为避免密钥密文被同步，请选择本地非同步目录。";
            return false;
        }

        return true;
    }

    private static bool IsInsideCloudFolder(string fullPath)
    {
        var segments = fullPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            foreach (var name in CloudFolderNames)
            {
                if (segment.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || segment.StartsWith(name + " -", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var normalized = Path.TrimEndingDirectorySeparator(root);
                if (fullPath.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>按数据目录区分的单实例互斥体。名称为规范化路径的稳定 SHA-256 摘要。</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    public static string MutexNameFor(string dataDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return @"Local\ProbeDesktop-" + Convert.ToHexString(hash, 0, 16);
    }

    public static SingleInstanceGuard? TryAcquire(string dataDirectory)
    {
        var mutex = new Mutex(false, MutexNameFor(dataDirectory));
        try
        {
            if (mutex.WaitOne(0, false))
            {
                return new SingleInstanceGuard(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出，当前线程已获得所有权。
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 非持有线程释放时忽略；进程退出时系统会回收。
        }

        mutex.Dispose();
    }
}
