using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BellaBaxter.AspNet.Configuration;

/// <summary>
/// Microsoft.Extensions.Configuration provider that loads secrets from Baxter API
/// and hot-reloads when values change (via polling).
/// </summary>
public sealed class BellaConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly BellaPollingProvider _poller;
    private bool _disposed;

    internal BellaConfigurationProvider(BellaOptions options, ILogger? logger = null)
    {
        _poller = new BellaPollingProvider(options, logger);
        _poller.SecretsChanged += OnSecretsChanged;
    }

    /// <summary>Called once by the configuration system at startup to load initial values.</summary>
    public override void Load()
    {
        try
        {
            var secrets = _poller.LoadSecretsAsync().GetAwaiter().GetResult();
            foreach (var kv in secrets)
                Data[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            // Log but don't crash — FallbackOnError handles resilience in the poller
            Console.Error.WriteLine($"[BellaBaxter] Error loading secrets at startup: {ex.Message}");
        }
    }

    private void OnSecretsChanged(object? sender, SecretsChangedEventArgs e)
    {
        foreach (var change in e.Changes)
        {
            if (change.NewValue is not null)
                Data[change.Key] = change.NewValue;
            else
                Data.Remove(change.Key);
        }

        // Triggers IOptionsMonitor.OnChange() and IChangeToken consumers
        OnReload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _poller.SecretsChanged -= OnSecretsChanged;
        _poller.Dispose();
        _disposed = true;
    }
}

/// <summary>IConfigurationSource implementation — created by AddBellaSecrets().</summary>
public sealed class BellaConfigurationSource : IConfigurationSource
{
    private readonly BellaOptions _options;
    private readonly ILogger? _logger;

    internal BellaConfigurationSource(BellaOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new BellaConfigurationProvider(_options, _logger);
}
