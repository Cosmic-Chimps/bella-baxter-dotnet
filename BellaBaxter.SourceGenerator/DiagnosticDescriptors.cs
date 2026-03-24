using Microsoft.CodeAnalysis;

namespace BellaBaxter.SourceGenerator
{
    internal static class DiagnosticDescriptors
    {
        public static readonly DiagnosticDescriptor InvalidManifest = new DiagnosticDescriptor(
            id: "BELLA001",
            title: "Invalid bella-secrets.manifest.json",
            messageFormat: "Could not parse bella-secrets.manifest.json at '{0}'. Re-run the BellaSecretsManifestTask or check the file.",
            category: "BellaBaxter",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor EmptyManifest = new DiagnosticDescriptor(
            id: "BELLA002",
            title: "bella-secrets.manifest.json has no secrets",
            messageFormat: "The manifest at '{0}' contains no secrets. No BellaAppSecrets class was generated.",
            category: "BellaBaxter",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
    }
}
