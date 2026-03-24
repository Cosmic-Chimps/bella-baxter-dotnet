using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace BellaBaxter.SourceGenerator.MSBuild
{
    /// <summary>
    /// MSBuild task that calls the Bella API to fetch secret metadata and writes
    /// bella-secrets.manifest.json into the project directory.
    ///
    /// Properties (set in the consuming .csproj):
    ///   BellaProject      — project slug (required)
    ///   BellaEnvironment  — environment slug (required)
    ///   BellaApiUrl       — base URL, e.g. https://bella.example.com (required)
    ///   BellaApiKey       — API key / Bearer token (required; pass from env var)
    ///   BellaManifestPath — output path; defaults to $(ProjectDir)bella-secrets.manifest.json
    ///
    /// Behaviour:
    ///   - If the API call succeeds  → writes/overwrites manifest, build continues.
    ///   - If unreachable + existing manifest exists → emits warning, uses cached copy.
    ///   - If unreachable + no manifest → emits error, build fails.
    /// </summary>
    public sealed class BellaSecretsManifestTask : Microsoft.Build.Utilities.Task
    {
        [Required] public string BellaProject { get; set; } = "";
        [Required] public string BellaEnvironment { get; set; } = "";
        [Required] public string BellaApiUrl { get; set; } = "";
        [Required] public string BellaApiKey { get; set; } = "";

        public string? BellaManifestPath { get; set; }

        public override bool Execute()
        {
            var manifestPath = string.IsNullOrWhiteSpace(BellaManifestPath)
                ? Path.Combine(Directory.GetCurrentDirectory(), "bella-secrets.manifest.json")
                : BellaManifestPath;

            try
            {
                var json = FetchManifest();
                File.WriteAllText(manifestPath, json);
                Log.LogMessage(MessageImportance.Normal,
                    $"[BellaBaxter] Manifest written to {manifestPath}");
                return true;
            }
            catch (Exception ex)
            {
                if (File.Exists(manifestPath))
                {
                    Log.LogWarning(
                        subcategory: null,
                        warningCode: "BELLA003",
                        helpKeyword: null,
                        file: null, lineNumber: 0, columnNumber: 0, endLineNumber: 0, endColumnNumber: 0,
                        message: $"[BellaBaxter] Could not reach Bella API ({ex.Message}). " +
                                 $"Using cached manifest at {manifestPath}.");
                    return true;
                }

                Log.LogError(
                    subcategory: null,
                    errorCode: "BELLA004",
                    helpKeyword: null,
                    file: null, lineNumber: 0, columnNumber: 0, endLineNumber: 0, endColumnNumber: 0,
                    message: $"[BellaBaxter] Could not reach Bella API and no cached manifest exists. " +
                             $"Error: {ex.Message}. " +
                             $"Tip: commit a bella-secrets.manifest.json for offline builds, or set BellaApiKey.");
                return false;
            }
        }

        private string FetchManifest()
        {
            var url = $"{BellaApiUrl.TrimEnd('/')}/api/v1/projects/{Uri.EscapeDataString(BellaProject)}" +
                      $"/environments/{Uri.EscapeDataString(BellaEnvironment)}/secrets?keys-only=true";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {BellaApiKey}");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromSeconds(15);

            var response = client.GetAsync(url).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            // Parse API response and reshape into manifest format
            var apiSecrets = JsonSerializer.Deserialize<ApiSecretsResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var manifest = new ManifestDto
            {
                Version     = "1",
                Project     = BellaProject,
                Environment = BellaEnvironment,
                FetchedAt   = DateTime.UtcNow.ToString("o"),
                Secrets     = System.Array.ConvertAll(
                    apiSecrets?.Secrets ?? Array.Empty<ApiSecretItem>(),
                    s => new ManifestSecretDto
                    {
                        Key         = s.Key ?? "",
                        Type        = s.Type ?? "String",
                        Description = s.Description
                    })
            };

            return JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true });
        }

        // ── DTOs for the Bella API response ──────────────────────────────────

        private sealed class ApiSecretsResponse
        {
            [JsonPropertyName("secrets")]
            public ApiSecretItem[]? Secrets { get; set; }

            [JsonPropertyName("items")]
            public ApiSecretItem[]? Items { get; set; }
        }

        private sealed class ApiSecretItem
        {
            [JsonPropertyName("key")]
            public string? Key { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }
        }

        // ── DTOs for the manifest file ────────────────────────────────────────

        private sealed class ManifestDto
        {
            [JsonPropertyName("version")]     public string Version     { get; set; } = "1";
            [JsonPropertyName("project")]     public string Project     { get; set; } = "";
            [JsonPropertyName("environment")] public string Environment { get; set; } = "";
            [JsonPropertyName("fetchedAt")]   public string FetchedAt   { get; set; } = "";
            [JsonPropertyName("secrets")]     public ManifestSecretDto[] Secrets { get; set; } = Array.Empty<ManifestSecretDto>();
        }

        private sealed class ManifestSecretDto
        {
            [JsonPropertyName("key")]         public string Key         { get; set; } = "";
            [JsonPropertyName("type")]        public string Type        { get; set; } = "String";
            [JsonPropertyName("description")] public string? Description { get; set; }
        }
    }
}
