using BellaBaxter.SourceGenerator;
using Xunit;

namespace BellaBaxter.SourceGenerator.Tests;

public class ManifestParserTests
{
    private const string SampleJson = """
        {
          "version": "1",
          "project": "my-app",
          "environment": "dev",
          "fetchedAt": "2026-03-01T00:00:00Z",
          "secrets": [
            { "key": "DATABASE_URL",  "type": "ConnectionString", "description": "Postgres connection string" },
            { "key": "API_KEY",       "type": "String" },
            { "key": "MAX_RETRIES",   "type": "Integer" },
            { "key": "FEATURE_FLAG",  "type": "Boolean" },
            { "key": "RATIO",         "type": "Float" },
            { "key": "CORRELATION_ID","type": "Guid" },
            { "key": "CERT_PEM",      "type": "CertificatePem" },
            { "key": "PAYLOAD_B64",   "type": "Base64" },
            { "key": "WEBHOOK_URL",   "type": "Url" },
            { "key": "CONFIG_JSON",   "type": "Json", "description": null }
          ]
        }
        """;

    [Fact]
    public void Parse_ReturnsCorrectTopLevelFields()
    {
        var manifest = ManifestParser.Parse(SampleJson);

        Assert.Equal("1",          manifest.Version);
        Assert.Equal("my-app",     manifest.Project);
        Assert.Equal("dev",        manifest.Environment);
        Assert.Equal("2026-03-01T00:00:00Z", manifest.FetchedAt);
    }

    [Fact]
    public void Parse_ReturnsAllSecrets()
    {
        var manifest = ManifestParser.Parse(SampleJson);
        Assert.Equal(10, manifest.Secrets.Count);
    }

    [Fact]
    public void Parse_ParsesKeyAndType()
    {
        var manifest = ManifestParser.Parse(SampleJson);
        Assert.Equal("DATABASE_URL",     manifest.Secrets[0].Key);
        Assert.Equal("ConnectionString", manifest.Secrets[0].Type);
        Assert.Equal("Postgres connection string", manifest.Secrets[0].Description);
    }

    [Fact]
    public void Parse_NullDescriptionReturnsNull()
    {
        var manifest = ManifestParser.Parse(SampleJson);
        var configJson = manifest.Secrets.Find(s => s.Key == "CONFIG_JSON");
        Assert.NotNull(configJson);
        Assert.Null(configJson!.Description);
    }

    [Fact]
    public void Parse_EmptySecretsArray_ReturnsEmptyList()
    {
        var json = """{"version":"1","project":"p","environment":"e","fetchedAt":"","secrets":[]}""";
        var manifest = ManifestParser.Parse(json);
        Assert.Empty(manifest.Secrets);
    }
}
