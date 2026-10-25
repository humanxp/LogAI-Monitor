// 宿主机资源占用：CPU / 内存 / 磁盘。
//
// 读的是容器里的 /proc 与 statvfs —— CPU 与内存来自宿主机 /proc（容器未做 cgroup
// 限制时就是整机数据），磁盘取归档目录所在的文件系统（/data 是宿主机的绑定挂载，
// 因此反映的是宿主机那块盘的剩余空间）。
//
// CPU 用 /proc/stat 的累计 jiffies 求两次采样的差值：第一次调用没有基线，就地
// 间隔 100ms 再采一次（只发生一次），之后每次都用上一次的采样点算，开销可忽略。

using System.Globalization;

namespace LogAI.Web.Api;

internal static class SystemMetrics
{
    private static readonly object Gate = new();
    private static long _prevIdle = -1;
    private static long _prevTotal = -1;
    private static string _diskPath = "/";

    private static (long Idle, long Total) ReadCpuStat()
    {
        try
        {
            using var reader = new StreamReader("/proc/stat");
            string? line = reader.ReadLine();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return (-1, -1);
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return (-1, -1);
            long total = 0;
            for (int i = 1; i < parts.Length && i < 9; i++)
                total += long.Parse(parts[i], CultureInfo.InvariantCulture);
            long idle = long.Parse(parts[4], CultureInfo.InvariantCulture);
            if (parts.Length > 5) idle += long.Parse(parts[5], CultureInfo.InvariantCulture);   // iowait
            return (idle, total);
        }
        catch
        {
            return (-1, -1);
        }
    }

    /// <summary>CPU 使用率（0-100）。无法读取时返回 -1。</summary>
    public static double CpuPercent()
    {
        lock (Gate)
        {
            var (idle, total) = ReadCpuStat();
            if (idle < 0) return -1;

            long baseIdle, baseTotal;
            if (_prevTotal < 0)
            {
                // 首次调用没有基线：隔 100ms 再采一次，用前后两个点算这一小段的使用率。
                baseIdle = idle;
                baseTotal = total;
                Thread.Sleep(100);
                (idle, total) = ReadCpuStat();
                if (idle < 0) return -1;
            }
            else
            {
                baseIdle = _prevIdle;
                baseTotal = _prevTotal;
            }

            long dIdle = idle - baseIdle;
            long dTotal = total - baseTotal;
            _prevIdle = idle;
            _prevTotal = total;
            if (dTotal <= 0) return 0;
            double used = 100.0 * (dTotal - dIdle) / dTotal;
            return Math.Clamp(used, 0, 100);
        }
    }

    /// <summary>内存总量与可用量（字节）。读取失败返回 (0,0)。</summary>
    public static (long Total, long Available) Memory()
    {
        try
        {
            long total = 0, available = 0;
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = ParseKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = ParseKb(line);
                if (total > 0 && available > 0) break;
            }
            return (total, available);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static long ParseKb(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long kb) ? kb * 1024 : 0;
    }

    /// <summary>磁盘总量与可用量（字节）。优先看归档目录所在的文件系统。</summary>
    public static (long Total, long Free) Disk()
    {
        foreach (string path in new[] { _diskPath, "/data", "/" })
        {
            try
            {
                var drive = new DriveInfo(path);
                if (!drive.IsReady) continue;
                if (drive.TotalSize <= 0) continue;
                _diskPath = path;
                return (drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch
            {
                // 换下一个候选路径
            }
        }
        return (0, 0);
    }
}
