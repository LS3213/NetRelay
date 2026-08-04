using System.IO;
using Omnexa.Core;

namespace NetRelay.Services;

public static class ReleaseVersionPolicy
{
    public static bool IsStrictlyNewer(string candidateVersion, string currentVersion)
    {
        if (!SemanticVersion.TryParse(candidateVersion, out var candidate) ||
            !SemanticVersion.TryParse(currentVersion, out var current))
        {
            throw new InvalidDataException("更新版本不符合 SemVer 格式，无法安全比较。");
        }

        return candidate.CompareTo(current) > 0;
    }
}
