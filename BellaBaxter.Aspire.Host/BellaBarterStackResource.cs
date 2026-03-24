using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Represents the full Bella Baxter stack running inside an Aspire AppHost.
/// Exposes the Baxter API URL so child projects can connect.
/// </summary>
public sealed class BellaBarterStackResource(
    string name,
    IResourceBuilder<IResourceWithConnectionString> api
) : Resource(name), IResourceWithEnvironment
{
    /// <summary>Reference to the Bella API resource (for WaitFor / endpoint resolution).</summary>
    internal IResourceBuilder<IResourceWithConnectionString> Api { get; } = api;

    /// <summary>
    /// Returns an endpoint expression that resolves to the Baxter API HTTP base URL at runtime.
    /// Inject this into child projects via WithEnvironment.
    /// </summary>
    public EndpointReference GetApiEndpoint() =>
        ((IResourceWithEndpoints)Api.Resource).GetEndpoint("http");
}
