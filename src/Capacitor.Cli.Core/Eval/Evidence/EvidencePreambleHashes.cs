using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The evidence route's prompt resources by file name, each hashed over its exact embedded text, compared
/// against the manifest's declared treatment, so any edit to a resource is a treatment change.</summary>
public static class EvidencePreambleHashes {
    public static readonly IReadOnlyList<string> Resources = [
        EvidencePromptBlocks.QuestionTemplateResource, EvidencePromptBlocks.OneShotPreambleResource, EvidencePromptBlocks.RetrospectivePreambleResource
    ];

    public static Dictionary<string, string> Compute() =>
        Resources.ToDictionary(name => name, name => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(EmbeddedResources.Load(name)))), StringComparer.Ordinal);
}
