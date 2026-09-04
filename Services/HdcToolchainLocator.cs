using System.IO;

namespace HarmonyFileManager.Services;

/// <summary>
/// 在本机定位 hdc.exe。探测顺序:
/// 1. appsettings.json 配置的 HdcPath;
/// 2. 工具自带/自动安装目录(程序目录\toolchains);
/// 3. OpenHarmony / HarmonyOS SDK 常见安装位置;
/// 4. DevEco Studio 自带 SDK;
/// 5. PATH 环境变量。
/// 不做全盘扫描,只探测已知目录模式,避免启动卡顿。
/// </summary>
public static class HdcToolchainLocator
{
    /// <summary>按优先级解析可用的 hdc.exe;找不到返回 null。</summary>
    public static string? Resolve(string? configuredPath)
    {
        if (Probe(configuredPath) is { } direct) return direct;

        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // 随工具分发或自动下载安装目录
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "toolchains", "hdc.exe"));

        // OpenHarmony / HarmonyOS SDK(%LOCALAPPDATA%\OpenHarmony\Sdk\12\toolchains\hdc.exe 等)
        AddSdkCandidates(candidates, Path.Combine(localAppData, "OpenHarmony", "Sdk"));
        AddSdkCandidates(candidates, Path.Combine(localAppData, "Huawei", "Sdk"));
        AddSdkCandidates(candidates, Path.Combine(programFiles, "Huawei", "Sdk"));
        AddSdkCandidates(candidates, Path.Combine(programFilesX86, "Huawei", "Sdk"));

        // DevEco Studio 自带 SDK(sdk\default\openharmony\toolchains\hdc.exe 等)
        AddSdkCandidates(candidates, Path.Combine(programFiles, "Huawei", "DevEco Studio", "sdk"));
        AddSdkCandidates(candidates, Path.Combine(programFilesX86, "Huawei", "DevEco Studio", "sdk"));
        AddSdkCandidates(candidates, Path.Combine(localAppData, "Huawei", "DevEco Studio", "sdk"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Probe(candidate) is { } found) return found;
        }

        return ProbeFromPath();
    }

    /// <summary>枚举某 SDK 根目录下的 toolchains\hdc.exe 候选:直接模式 + 版本子目录 + openharmony 内嵌结构。
    /// 版本子目录优先数字版本最大的,避免多版本并存时命中旧 SDK。</summary>
    private static void AddSdkCandidates(List<string> candidates, string sdkRoot)
    {
        if (!Directory.Exists(sdkRoot)) return;
        candidates.Add(Path.Combine(sdkRoot, "toolchains", "hdc.exe"));
        foreach (var versionDir in Directory.EnumerateDirectories(sdkRoot).OrderByDescending(VersionScore))
        {
            candidates.Add(Path.Combine(versionDir, "toolchains", "hdc.exe"));
            var ohRoot = Path.Combine(versionDir, "openharmony");
            if (!Directory.Exists(ohRoot)) continue;
            candidates.Add(Path.Combine(ohRoot, "toolchains", "hdc.exe"));
            foreach (var sub in Directory.EnumerateDirectories(ohRoot))
            {
                candidates.Add(Path.Combine(sub, "toolchains", "hdc.exe"));
            }
        }
    }

    /// <summary>目录名以数字开头时返回其数值(用于降序优先新版本);否则返回 -1(排在最后)。</summary>
    private static int VersionScore(string dir)
    {
        var name = Path.GetFileName(dir);
        var digits = new string(name.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var version) ? version : -1;
    }

    private static string? ProbeFromPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        foreach (var rawDir in pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0) continue;
            if (Probe(Path.Combine(dir, "hdc.exe")) is { } found) return found;
        }
        return null;
    }

    private static string? Probe(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : (File.Exists(path) ? path : null);
    }
}
