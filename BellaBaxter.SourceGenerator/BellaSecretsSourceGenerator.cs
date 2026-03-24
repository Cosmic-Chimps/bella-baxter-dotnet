using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace BellaBaxter.SourceGenerator
{
    /// <summary>
    /// Roslyn incremental source generator.
    ///
    /// Trigger: an AdditionalFile named "bella-secrets.manifest.json" is present.
    ///
    /// MSBuild properties consumed (via CompilerVisibleProperty):
    ///   BellaSecretsClassName   → defaults to "BellaAppSecrets"
    ///   BellaSecretsNamespace   → defaults to root namespace of the consuming project
    /// </summary>
    [Generator]
    public sealed class BellaSecretsSourceGenerator : IIncrementalGenerator
    {
        private const string ManifestFileName = "bella-secrets.manifest.json";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // 1. Source: the manifest file from AdditionalFiles
            var manifestProvider = context.AdditionalTextsProvider.Where(f =>
                Path.GetFileName(f.Path) == ManifestFileName
            );

            // 2. Source: MSBuild properties
            var classNameProvider = context.AnalyzerConfigOptionsProvider.Select(
                (opts, _) =>
                {
                    opts.GlobalOptions.TryGetValue(
                        "build_property.BellaSecretsClassName",
                        out var v
                    );
                    return string.IsNullOrWhiteSpace(v) ? "BellaAppSecrets" : v!.Trim();
                }
            );

            var namespaceProvider = context.AnalyzerConfigOptionsProvider.Select(
                (opts, _) =>
                {
                    opts.GlobalOptions.TryGetValue(
                        "build_property.BellaSecretsNamespace",
                        out var ns
                    );
                    // Only use an explicitly-set namespace; don't default to RootNamespace.
                    // Top-level statement programs live in the global namespace, so a
                    // namespace wrapper would make the generated class invisible without
                    // an extra "using" directive.
                    return string.IsNullOrWhiteSpace(ns) ? "" : ns!.Trim();
                }
            );

            // 3. Combine all three
            var combined = manifestProvider.Combine(classNameProvider).Combine(namespaceProvider);

            // 4. Register source output
            context.RegisterSourceOutput(
                combined,
                (spc, pair) =>
                {
                    var ((manifestFile, className), namespaceName) = pair;

                    var json = manifestFile.GetText(spc.CancellationToken)?.ToString();
                    if (string.IsNullOrWhiteSpace(json))
                        return;

                    SecretsManifest manifest;
                    try
                    {
                        manifest = ManifestParser.Parse(json!);
                    }
                    catch
                    {
                        spc.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.InvalidManifest,
                                Location.None,
                                manifestFile.Path
                            )
                        );
                        return;
                    }

                    if (manifest.Secrets.Count == 0)
                    {
                        spc.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.EmptyManifest,
                                Location.None,
                                manifestFile.Path
                            )
                        );
                        return;
                    }

                    var source = CodeEmitter.Emit(manifest, className, namespaceName);
                    spc.AddSource($"{className}.g.cs", SourceText.From(source, Encoding.UTF8));
                }
            );
        }
    }
}
