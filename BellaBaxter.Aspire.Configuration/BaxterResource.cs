using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Represents a Baxter API connection resource in an Aspire AppHost.
/// Injects BellaBaxter__BaxterUrl and BellaBaxter__ApiKey into referencing projects.
/// The project+environment is encoded in the API key itself — no slug needed.
/// </summary>
public sealed class BaxterResource(
    string name,
    string baxterUrl,
    ParameterResource apiKey)
    : Resource(name), IResourceWithEnvironment
{
    public string BaxterUrl { get; } = baxterUrl;
    public ParameterResource ApiKey { get; } = apiKey;
}
