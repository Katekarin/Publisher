using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.IO;

namespace ConfluencePublisher;

/// <summary>
/// OAuth2Client supporting client_credentials grant only (no browser interaction required).
/// </summary>
internal sealed class OAuth2Client : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scopes;
    private readonly string _grantType;

    private readonly Credentials? _credentials;
    private readonly string? _credentialsFilePath;

    private OAuth2Token? _cachedToken;

    /// <summary>
    /// Initialize OAuth2Client for client_credentials grant (machine-to-machine, no browser).
    /// </summary>
    public OAuth2Client(
        string baseUrl,
        string clientId,
        string clientSecret,
        string scopes = "read write",
        string? grantType = null,
        Credentials? credentials = null,
        string? credentialsFilePath = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        _scopes = scopes;
        _grantType = string.IsNullOrWhiteSpace(grantType) ? "client_credentials" : grantType.Trim();

        _credentials = credentials;
        _credentialsFilePath = string.IsNullOrWhiteSpace(credentialsFilePath) ? null : credentialsFilePath;

        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri(_baseUrl + "/");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        TryLoadTokenFromCredentials();
    }

    /// <summary>
    /// Get valid access token (from cache if available and not expired, otherwise acquire new via client_credentials).
    /// </summary>
    public async Task<string> GetAccessTokenAsync()
    {
        if (_cachedToken != null && !_cachedToken.IsExpired)
        {
            var remainingSeconds = _cachedToken.ExpiresIn - (int)(DateTime.UtcNow - _cachedToken.AcquiredAt).TotalSeconds;
            Console.WriteLine($"[INFO] Using cached OAuth token (expires in {remainingSeconds}s).");
            return _cachedToken.AccessToken;
        }

        await EnsureConfluenceIsReachableAsync();
        await ExchangeClientCredentialsForTokenAsync();
        return _cachedToken!.AccessToken;
    }

    /// <summary>
    /// Force refresh/reacquire token (used when 401 is received on API call).
    /// </summary>
    public async Task ForceRefreshAsync()
    {
        _cachedToken = null;
        await GetAccessTokenAsync();
    }

    /// <summary>
    /// Send HTTP request with OAuth Bearer token attached.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        var token = await GetAccessTokenAsync();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task ExchangeClientCredentialsForTokenAsync()
    {
        var tokenUrl = $"{_baseUrl}/rest/oauth2/latest/token";

        var payload = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
            { "client_id", _clientId },
            { "client_secret", _clientSecret }
        };

        if (!string.IsNullOrWhiteSpace(_scopes))
        {
            payload["scope"] = _scopes;
        }

        var content = new FormUrlEncodedContent(payload);
        var response = await _httpClient.PostAsync(tokenUrl, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OAuth2 client_credentials exchange failed ({(int)response.StatusCode}): {body}");

        var tokenResponse = JsonSerializer.Deserialize<OAuth2TokenResponse>(body)
            ?? throw new InvalidOperationException("Failed to deserialize client_credentials token response");

        _cachedToken = new OAuth2Token
        {
            AccessToken = tokenResponse.AccessToken,
            ExpiresIn = tokenResponse.ExpiresIn,
            RefreshToken = tokenResponse.RefreshToken,
            AcquiredAt = DateTime.UtcNow
        };

        var displayToken = tokenResponse.AccessToken.Length > 50
            ? tokenResponse.AccessToken.Substring(0, 50) + "..."
            : tokenResponse.AccessToken;
        Console.WriteLine($"[INFO] ? Access token acquired via client_credentials!");
        Console.WriteLine($"[DEBUG] Token: {displayToken}");
        Console.WriteLine($"[DEBUG] Expires in: {tokenResponse.ExpiresIn} seconds");

        TryPersistTokenToCredentials();
    }

    private void TryLoadTokenFromCredentials()
    {
        try
        {
            if (_credentials == null)
                return;

            if (string.IsNullOrWhiteSpace(_credentials.OAuthAccessToken) ||
                _credentials.OAuthTokenAcquiredAtUtc == null ||
                _credentials.OAuthExpiresIn == null)
                return;

            _cachedToken = new OAuth2Token
            {
                AccessToken = _credentials.OAuthAccessToken,
                RefreshToken = _credentials.OAuthRefreshToken,
                ExpiresIn = _credentials.OAuthExpiresIn.Value,
                AcquiredAt = DateTime.SpecifyKind(_credentials.OAuthTokenAcquiredAtUtc.Value, DateTimeKind.Utc)
            };

            if (_cachedToken.IsExpired)
            {
                Console.WriteLine($"[INFO] Cached token has expired (acquired at {_cachedToken.AcquiredAt:O}, expires in {_cachedToken.ExpiresIn}s).");
                _cachedToken = null;
                return;
            }

            Console.WriteLine($"[INFO] Loaded valid cached OAuth token from credentials file (expires in {(_cachedToken.ExpiresIn - (int)(DateTime.UtcNow - _cachedToken.AcquiredAt).TotalSeconds)}s).");
        }
        catch
        {
            // ignore cache load errors
        }
    }

    private void TryPersistTokenToCredentials()
    {
        try
        {
            if (_cachedToken == null || string.IsNullOrWhiteSpace(_credentialsFilePath))
                return;

            Credentials? current;
            try
            {
                current = File.Exists(_credentialsFilePath)
                    ? JsonSerializer.Deserialize<Credentials>(File.ReadAllText(_credentialsFilePath))
                    : null;
            }
            catch
            {
                current = null;
            }

            current ??= new Credentials();

            var updated = new Credentials
            {
                BaseUrl = current.BaseUrl,
                Username = current.Username,
                ApiToken = current.ApiToken,
                OAuthClientId = current.OAuthClientId,
                OAuthClientSecret = current.OAuthClientSecret,
                RedirectUri = current.RedirectUri,
                OAuthScopes = current.OAuthScopes,
                OAuthGrantType = current.OAuthGrantType,
                OAuthAccessToken = _cachedToken.AccessToken,
                OAuthRefreshToken = _cachedToken.RefreshToken,
                OAuthExpiresIn = _cachedToken.ExpiresIn,
                OAuthTokenAcquiredAtUtc = _cachedToken.AcquiredAt
            };

            var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_credentialsFilePath, json);
        }
        catch
        {
            // ignore persistence errors
        }
    }

    private async Task EnsureConfluenceIsReachableAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/status");
            using var response = await _httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 500)
            {
                return;
            }
        }
        catch
        {
            // ignored; we'll throw a clearer message below
        }

        throw new InvalidOperationException(
            $"Confluence is not reachable at '{_baseUrl}'. Make sure the server is running before starting OAuth.");
    }

    private class OAuth2TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;
    }

    private class OAuth2Token
    {
        public string AccessToken { get; set; } = string.Empty;
        public string? RefreshToken { get; set; }
        public DateTime AcquiredAt { get; set; }
        public int ExpiresIn { get; set; }

        public bool IsExpired => DateTime.UtcNow >= AcquiredAt.AddSeconds(ExpiresIn - 60);
    }
}
