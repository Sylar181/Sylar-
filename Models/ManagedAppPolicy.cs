namespace HarmonyFileManager.Models;

public sealed class ManagedAppPolicy
{
    public required string BundleName { get; init; }
    public required string AbilityName { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>允许访问的沙箱根目录,键为界面展示的逻辑根名,值为沙箱绝对路径(如 /data/storage/el2/base/files)。</summary>
    public required Dictionary<string, string> AllowedRoots { get; init; }

    /// <summary>敏感路径片段,命中即拒绝(凭据、令牌、密钥、数据库、偏好设置等)。</summary>
    public required string[] BlockedPathFragments { get; init; }
}