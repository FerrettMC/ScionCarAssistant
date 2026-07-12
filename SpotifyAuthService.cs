using Microsoft.Maui.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace ScionCarAssistant;

public class SpotifyAuthService
{
  const string ClientId = "33592ce32ab54f0bbcba17a4170bcebc";
  const string RedirectUri = "sciontacar://callback";
  const string Scopes = "user-modify-playback-state user-read-playback-state playlist-read-private playlist-read-collaborative";

  string? _codeVerifier;

  public async Task<string?> LoginAndGetAuthCodeAsync()
  {
    _codeVerifier = GenerateCodeVerifier();
    var codeChallenge = GenerateCodeChallenge(_codeVerifier);

    var authUrl = $"https://accounts.spotify.com/authorize" +
        $"?response_type=code" +
        $"&client_id={ClientId}" +
        $"&scope={Uri.EscapeDataString(Scopes)}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&code_challenge_method=S256" +
        $"&code_challenge={codeChallenge}";

    var result = await WebAuthenticator.Default.AuthenticateAsync(
        new Uri(authUrl), new Uri(RedirectUri));

    return result.Properties.TryGetValue("code", out var code) ? code : null;
  }

  public string? GetCodeVerifier() => _codeVerifier;

  static string GenerateCodeVerifier()
  {
    var bytes = RandomNumberGenerator.GetBytes(32);
    return Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
  }

  static string GenerateCodeChallenge(string verifier)
  {
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
    return Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
  }

  public async Task<(string accessToken, string refreshToken)?> ExchangeCodeForTokenAsync(string code)
  {
    using var http = new HttpClient();

    var form = new Dictionary<string, string>
    {
      ["grant_type"] = "authorization_code",
      ["code"] = code,
      ["redirect_uri"] = RedirectUri,
      ["client_id"] = ClientId,
      ["code_verifier"] = _codeVerifier!
    };

    var response = await http.PostAsync(
        "https://accounts.spotify.com/api/token",
        new FormUrlEncodedContent(form));

    if (!response.IsSuccessStatusCode)
      return null;

    var json = await response.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;

    var accessToken = root.GetProperty("access_token").GetString()!;
    var refreshToken = root.GetProperty("refresh_token").GetString()!;

    return (accessToken, refreshToken);
  }
  public async Task<string?> RefreshAccessTokenAsync(string refreshToken)
  {
    using var http = new HttpClient();

    var form = new Dictionary<string, string>
    {
      ["grant_type"] = "refresh_token",
      ["refresh_token"] = refreshToken,
      ["client_id"] = ClientId
    };

    var response = await http.PostAsync(
        "https://accounts.spotify.com/api/token",
        new FormUrlEncodedContent(form));

    if (!response.IsSuccessStatusCode)
      return null;

    var json = await response.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    return doc.RootElement.GetProperty("access_token").GetString();
  }
}

