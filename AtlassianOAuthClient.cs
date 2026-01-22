using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ConfluencePublisher;

internal sealed class OAuth2Client : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scopes;

    private OAuth2Token? _cachedToken;

    public OAuth2Client(string baseUrl, string clientId, string clientSecret, string scopes = "read:confluence-space.summary read:confluence-content write:confluence-content manage:confluence-attachment delete:confluence-attachment")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        _scopes = scopes;

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetAccessTokenAsync()
    {
        if (_cachedToken != null && !_cachedToken.IsExpired)
            return _cachedToken.AccessToken;

        // ZMIANA 1: Przeniesienie credentials do body (payload)
        var payload = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
            { "client_id", _clientId },         // <-- Dodane
            { "client_secret", _clientSecret }, // <-- Dodane
            //{ "scope", _scopes }
        };

        var content = new FormUrlEncodedContent(payload);
        var tokenUrl = $"{_baseUrl}/rest/oauth2/latest/token";

        // ZMIANA 2: Usunięto generowanie nagłówka Basic Auth (linie z Convert.ToBase64String...)

        var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = content
        };

        // ZMIANA 3: Usunięto linię request.Headers.Authorization = ... (serwer tego nie chce)

        Console.WriteLine($"[DEBUG] Wysyłanie token request do: {tokenUrl}");
        // Console.WriteLine($"[DEBUG] Payload: {await content.ReadAsStringAsync()}"); // Opcjonalnie odkomentuj, ale uważaj na logowanie sekretów!

        HttpResponseMessage response;
        string body = string.Empty;

        try
        {
            response = await _httpClient.SendAsync(request);
            body = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"[DEBUG] Status code: {(int)response.StatusCode} ({response.ReasonPhrase})");

            // Reszta bez zmian...
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OAuth2 token request failed ({(int)response.StatusCode}): {body}");
            }

            var tokenResponse = JsonSerializer.Deserialize<OAuth2TokenResponse>(body)
                ?? throw new InvalidOperationException("Failed to deserialize token response");

            _cachedToken = new OAuth2Token
            {
                AccessToken = tokenResponse.AccessToken,
                ExpiresIn   = tokenResponse.ExpiresIn,
                AcquiredAt  = DateTime.UtcNow
            };

            return _cachedToken.AccessToken;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ERROR] Wyjątek podczas pobierania tokena:");
            Console.WriteLine(ex.ToString());
            Console.WriteLine($"Ostatnio widziane body: {body}");
            throw;
        }
    }

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

    private class OAuth2TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;
    }

    private class OAuth2Token
    {
        public string AccessToken { get; set; } = string.Empty;
        public DateTime AcquiredAt { get; set; }
        public int ExpiresIn { get; set; }

        public bool IsExpired => DateTime.UtcNow >= AcquiredAt.AddSeconds(ExpiresIn - 60);
    }
}