// Minimal JSON parser for SecretsManifest — no external dependencies.
// Targets netstandard2.0 so we can't use System.Text.Json.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BellaBaxter.SourceGenerator
{
    internal static class ManifestParser
    {
        // Matches  "key": "value"  or  "key": null
        private static readonly Regex StringProp =
            new Regex(@"""(\w+)""\s*:\s*(?:""((?:[^""\\]|\\.)*)""|null)",
                RegexOptions.Compiled);

        // Matches the start of the secrets array entries (objects between [ and ])
        private static readonly Regex SecretObject =
            new Regex(@"\{([^}]*)\}", RegexOptions.Compiled | RegexOptions.Singleline);

        public static SecretsManifest Parse(string json)
        {
            var manifest = new SecretsManifest();

            // Top-level string props
            foreach (Match m in StringProp.Matches(json))
            {
                var key = m.Groups[1].Value;
                var val = m.Groups[2].Success ? m.Groups[2].Value : null;
                switch (key)
                {
                    case "version": manifest.Version = val ?? ""; break;
                    case "project": manifest.Project = val ?? ""; break;
                    case "environment": manifest.Environment = val ?? ""; break;
                    case "fetchedAt": manifest.FetchedAt = val ?? ""; break;
                }
            }

            // Locate the secrets array
            var secretsIdx = json.IndexOf("\"secrets\"", StringComparison.Ordinal);
            if (secretsIdx < 0) return manifest;

            var arrayStart = json.IndexOf('[', secretsIdx);
            var arrayEnd   = json.LastIndexOf(']');
            if (arrayStart < 0 || arrayEnd < arrayStart) return manifest;

            var arrayContent = json.Substring(arrayStart, arrayEnd - arrayStart + 1);

            foreach (Match obj in SecretObject.Matches(arrayContent))
            {
                var entry = new SecretEntry();
                foreach (Match prop in StringProp.Matches(obj.Value))
                {
                    var k = prop.Groups[1].Value;
                    var v = prop.Groups[2].Success ? UnescapeJson(prop.Groups[2].Value) : null;
                    switch (k)
                    {
                        case "key":         entry.Key         = v ?? ""; break;
                        case "type":        entry.Type        = v ?? "String"; break;
                        case "description": entry.Description = v; break;
                    }
                }
                if (!string.IsNullOrWhiteSpace(entry.Key))
                    manifest.Secrets.Add(entry);
            }

            return manifest;
        }

        private static string UnescapeJson(string s) =>
            s.Replace("\\\"", "\"")
             .Replace("\\\\", "\\")
             .Replace("\\/",  "/")
             .Replace("\\n",  "\n")
             .Replace("\\r",  "\r")
             .Replace("\\t",  "\t");
    }
}
