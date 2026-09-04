using HarmonyFileManager.Models;

namespace HarmonyFileManager.Services;

public sealed class PathPolicyService
{
    public bool IsAllowed(ManagedAppPolicy policy, string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/').TrimEnd('/');
        var inAllowedRoot = policy.AllowedRoots.Values.Any(root => normalized.StartsWith(root.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        var containsBlockedFragment = policy.BlockedPathFragments.Any(fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        return inAllowedRoot && !containsBlockedFragment;
    }

    public void EnsureAllowed(ManagedAppPolicy policy, string remotePath)
    {
        if (!IsAllowed(policy, remotePath))
        {
            throw new UnauthorizedAccessException("该路径不在维护白名单中或属于敏感数据。");
        }
    }
}