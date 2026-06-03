using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core;

public static class RepoHashHelper {
    public static string ComputeRepoHash(string owner, string repoName) {
        var input = $"{owner}/{repoName}".ToLowerInvariant();
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexStringLower(hash)[..16];
    }
}
