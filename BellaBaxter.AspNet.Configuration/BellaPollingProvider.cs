using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BellaBaxter.AspNet.Configuration;

/// <summary>
/// Polls Baxter API for secrets on a timer. Calls LoadSecretsAsync() on startup
/// and then every PollingInterval, firing SecretsChanged when values differ.
///
/// Authentication: HMAC-SHA256 request signing via BellaClientFactory (bax- API key).
/// End-to-end encryption: always enabled — the server requires X-E2E-Public-Key
/// for all secret read endpoints.
/// </summary>
internal sealed class BellaPollingProvider : IDisposable
{
    private readonly BellaOptions _options;
    private readonly BellaClient _client;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IDictionary<string, string>? _cache;
    private long _currentVersion = -1;
    private bool _contextResolved;

    /// <summary>Fired when at least one secret value has changed since last poll.</summary>
    public event EventHandler<SecretsChangedEventArgs>? SecretsChanged;

    public BellaPollingProvider(BellaOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;

        _client = BellaClientFactory.CreateWithHmacApiKey(
            options.BaxterUrl,
            options.ApiKey,
            appClient: options.AppClient ?? Environment.GetEnvironmentVariable("BELLA_BAXTER_APP_CLIENT"));

        _logger.LogDebug("[BellaBaxter] E2EE enabled (P-256 key pair generated)");

        // Start polling: first tick immediately (TimeSpan.Zero), then every interval
        _timer = new Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, options.PollingInterval);
    }

    // ── Secret polling ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches all secrets for the environment. On first call populates cache.
    /// On subsequent calls, detects changes and fires SecretsChanged.
    /// Returns the secrets dictionary (or cached copy on error if FallbackOnError=true).
    /// </summary>
    /// <summary>
    /// If ProjectSlug or EnvironmentSlug are not configured, resolve them from the API key
    /// context via GET /api/v1/keys/me. This makes EnvironmentSlug/ProjectSlug optional in
    /// appsettings — the API key already encodes the project/environment context.
    /// </summary>
    private async Task EnsureContextResolvedAsync(CancellationToken ct)
    {
        if (_contextResolved) return;

        if (!string.IsNullOrEmpty(_options.ProjectSlug) && !string.IsNullOrEmpty(_options.EnvironmentSlug))
        {
            _contextResolved = true;
            return;
        }

        var ctx = await _client.Api.V1.Keys.Me.GetAsync(cancellationToken: ct);
        if (ctx is null)
            throw new InvalidOperationException("[BellaBaxter] Could not resolve project/environment context from API key.");

        if (string.IsNullOrEmpty(_options.ProjectSlug))
            _options.ProjectSlug = ctx.ProjectSlug ?? string.Empty;

        if (string.IsNullOrEmpty(_options.EnvironmentSlug))
            _options.EnvironmentSlug = ctx.EnvironmentSlug ?? string.Empty;

        _logger.LogInformation("[BellaBaxter] Context resolved from API key: project='{Project}' environment='{Env}'",
            _options.ProjectSlug, _options.EnvironmentSlug);

        _contextResolved = true;
    }

    public async Task<IDictionary<string, string>> LoadSecretsAsync(CancellationToken ct = default)
    {
        try
        {
            // 0. Auto-resolve project/environment from API key if not configured
            await EnsureContextResolvedAsync(ct);

            // 1. Lightweight version check — skip full fetch if nothing changed
            if (_currentVersion >= 0)
            {
                try
                {
                    var versionResp = await _client.Api.V1.Projects[_options.ProjectSlug]
                        .Environments[_options.EnvironmentSlug]
                        .Secrets.Version.GetAsync(cancellationToken: ct);

                    if (versionResp?.Version == _currentVersion)
                    {
                        _logger.LogDebug("[BellaBaxter] Version unchanged ({Version}), skipping full fetch", _currentVersion);
                        return _cache!;
                    }
                }
                catch
                {
                    // Version endpoint unavailable — fall through to full fetch
                }
            }

            // 2. Full fetch via Kiota client
            var secretsResp = await _client.Api.V1.Projects[_options.ProjectSlug]
                .Environments[_options.EnvironmentSlug]
                .Secrets.GetAsync(cancellationToken: ct);

            if (secretsResp is null)
                throw new InvalidOperationException("Empty response from Baxter secrets endpoint.");

            var newRawSecrets = ExtractSecrets(secretsResp);
            if (secretsResp.Version.HasValue)
                _currentVersion = secretsResp.Version.Value;

            var newSecrets = ApplyKeyPrefix(newRawSecrets);

            await _lock.WaitAsync(ct);
            try
            {
                var changes = DetectChanges(_cache, newSecrets);
                _cache = newSecrets;

                if (changes.Count > 0)
                {
                    _logger.LogInformation("[BellaBaxter] {Count} secret(s) changed, reloading configuration", changes.Count);
                    SecretsChanged?.Invoke(this, new SecretsChangedEventArgs(changes));
                }
                else if (_cache is null)
                {
                    _logger.LogInformation("[BellaBaxter] Loaded {Count} secret(s) from environment '{Env}'",
                        newSecrets.Count, _options.EnvironmentSlug);
                }
            }
            finally { _lock.Release(); }

            return newSecrets;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BellaBaxter] Failed to fetch secrets from Baxter");

            if (_options.FallbackOnError && _cache is not null)
            {
                _logger.LogWarning("[BellaBaxter] Using cached secrets ({Count} entries)", _cache.Count);
                return _cache;
            }

            throw;
        }
    }

    private async Task PollAsync()
    {
        try { await LoadSecretsAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[BellaBaxter] Poll failed"); }
    }

    /// <summary>
    /// Extracts the secrets dictionary from the Kiota <see cref="AllEnvironmentSecretsResponse"/>.
    /// The generated model stores free-form dictionary keys in <c>AdditionalData</c>.
    /// </summary>
    private static Dictionary<string, string> ExtractSecrets(AllEnvironmentSecretsResponse resp)
    {
        if (resp.Secrets?.AdditionalData is null)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in resp.Secrets.AdditionalData)
        {
            if (kvp.Value is null) continue;

            // Kiota 1.x parses JSON values into typed CLR objects:
            // regular strings → string, UUID-shaped strings → Guid, numbers → int/long/double, booleans → bool.
            // Convert every non-null scalar to string so nothing is silently dropped.
            var str = kvp.Value switch
            {
                string s  => s,
                Guid g    => g.ToString(),           // UUIDs come back as Guid, not string
                bool b    => b.ToString().ToLower(),  // "true"/"false" not "True"/"False"
                int i     => i.ToString(),
                long l    => l.ToString(),
                double d  => d.ToString(),
                float f   => f.ToString(),
                _         => kvp.Value.ToString()
            };

            if (str is not null)
                result[kvp.Key.Replace("__", ":")] = str;
        }
        return result;
    }

    private Dictionary<string, string> ApplyKeyPrefix(Dictionary<string, string> secrets)
    {
        if (string.IsNullOrEmpty(_options.KeyPrefix))
            return secrets;

        return secrets
            .Select(kvp => new KeyValuePair<string, string>(
                kvp.Key.StartsWith(_options.KeyPrefix, StringComparison.OrdinalIgnoreCase)
                    ? kvp.Key[_options.KeyPrefix.Length..]
                    : kvp.Key,
                kvp.Value))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static List<SecretChange> DetectChanges(
        IDictionary<string, string>? oldSecrets,
        IDictionary<string, string> newSecrets)
    {
        var changes = new List<SecretChange>();
        if (oldSecrets is null) return changes;

        foreach (var kv in newSecrets)
        {
            oldSecrets.TryGetValue(kv.Key, out var oldVal);
            if (oldVal != kv.Value)
                changes.Add(new SecretChange(kv.Key, oldVal, kv.Value));
        }

        foreach (var key in oldSecrets.Keys.Except(newSecrets.Keys))
            changes.Add(new SecretChange(key, oldSecrets[key], null));

        return changes;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _lock.Dispose();
    }
}

public sealed record SecretChange(string Key, string? OldValue, string? NewValue);

public sealed class SecretsChangedEventArgs(IReadOnlyList<SecretChange> changes) : EventArgs
{
    public IReadOnlyList<SecretChange> Changes { get; } = changes;
}

