using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CheerfulGiverNXT
{
    public static class BlackbaudPkceAuthHttps
    {
        private const string AuthorizationEndpoint = "https://oauth2.sky.blackbaud.com/authorization";
        private const string TokenEndpoint = "https://oauth2.sky.blackbaud.com/token";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private static readonly TimeSpan DefaultRefreshSkew = TimeSpan.FromMinutes(2);

        public sealed record TokenResult(
            string AccessToken,
            string TokenType,
            int ExpiresIn,
            string Scope,
            string? RefreshToken,
            DateTimeOffset ExpiresAtUtc)
        {
            public bool IsExpiringSoon(TimeSpan? skew = null)
                => DateTimeOffset.UtcNow >= ExpiresAtUtc - (skew ?? DefaultRefreshSkew);
        }

        public static Task<TokenResult> AcquireTokenAsync(
            string clientId,
            string redirectUri,
            string scope,
            CancellationToken ct = default)
            => AcquireTokenAsync(clientId, clientSecret: null, redirectUri, scope, timeout: TimeSpan.FromMinutes(10), ct);

        public static async Task<TokenResult> AcquireTokenAsync(
            string clientId,
            string? clientSecret,
            string redirectUri,
            string scope,
            TimeSpan? timeout,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("clientId is required.", nameof(clientId));
            if (string.IsNullOrWhiteSpace(redirectUri))
                throw new ArgumentException("redirectUri is required.", nameof(redirectUri));

            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redir))
                throw new ArgumentException("redirectUri must be an absolute URI.", nameof(redirectUri));

            if (!string.Equals(redir.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(redir.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("redirectUri must be http:// or https:// and must exactly match the Redirect URI configured in Blackbaud (including trailing slash).", nameof(redirectUri));
if (!redir.IsLoopback)
                throw new ArgumentException("redirectUri must be a loopback URI (localhost/127.0.0.1/::1).", nameof(redirectUri));

            if (redir.Port <= 1024)
                throw new ArgumentException("redirectUri must use a non-privileged port (e.g., 5001).", nameof(redirectUri));

            var (verifier, challenge) = CreatePkcePair();
            var state = CreateBase64UrlString(32);

            var tcs = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);

            var urlBase = $"{redir.Scheme}://{redir.Host}:{redir.Port}";
            var callbackPath = redir.AbsolutePath;

            using var host = CreateCallbackHost(urlBase, callbackPath, expectedState: state, tcs);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
                timeoutCts.CancelAfter(timeout.Value);

            await host.StartAsync(timeoutCts.Token).ConfigureAwait(false);

            var authUrl = BuildAuthorizationUrl(clientId, redirectUri, scope, challenge, state);

            try
            {
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("Failed to launch the system browser for OAuth authorization.", ex);
            }

            Dictionary<string, string> query;
            try
            {
                using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
                    query = await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (query.TryGetValue("error", out var err))
            {
                query.TryGetValue("error_description", out var errDesc);
                throw new InvalidOperationException($"OAuth error: {err} {errDesc}".Trim());
            }

            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("No authorization code returned to redirect URI.");

            return await ExchangeCodeForTokenAsync(clientId, clientSecret, redirectUri, code, verifier, timeoutCts.Token)
                .ConfigureAwait(false);
        }

        public static async Task<TokenResult> RefreshTokenAsync(
            string clientId,
            string? clientSecret,
            string refreshToken,
            bool preserveRefreshToken,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("clientId is required.", nameof(clientId));
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new ArgumentException("refreshToken is required.", nameof(refreshToken));

            using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);

            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            }

            var body = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", clientId),
            };

            if (preserveRefreshToken)
                body.Add(new("preserve_refresh_token", "true"));

            if (!string.IsNullOrWhiteSpace(clientSecret))
                body.Add(new("client_secret", clientSecret));

            req.Content = new FormUrlEncodedContent(body);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token refresh returned {(int)resp.StatusCode}: {json}");

            var tr = ParseTokenResponse(json);

            // If provider rotates refresh tokens, keep new if provided; otherwise keep old.
            var effectiveRefresh = string.IsNullOrWhiteSpace(tr.RefreshToken) ? refreshToken : tr.RefreshToken;

            return tr with { RefreshToken = effectiveRefresh };
        }

        public static async Task<TokenResult> EnsureValidAsync(
            string clientId,
            TokenResult current,
            string? clientSecret = null,
            bool preserveRefreshToken = false,
            TimeSpan? refreshSkew = null,
            CancellationToken ct = default)
        {
            if (current is null)
                throw new ArgumentNullException(nameof(current));

            if (!current.IsExpiringSoon(refreshSkew))
                return current;

            if (string.IsNullOrWhiteSpace(current.RefreshToken))
                throw new InvalidOperationException("Token is expiring but no refresh token is available.");

            return await RefreshTokenAsync(clientId, clientSecret, current.RefreshToken!, preserveRefreshToken, ct)
                .ConfigureAwait(false);
        }

        private static IHost CreateCallbackHost(
            string baseUrl,
            string callbackPath,
            string expectedState,
            TaskCompletionSource<Dictionary<string, string>> tcs)
        {
            if (!callbackPath.StartsWith("/"))
                callbackPath = "/" + callbackPath;

            return Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseUrls(baseUrl);

                    web.ConfigureServices(services =>
                    {
                        services.AddRouting();
                    });

                    web.Configure(app =>
                    {
                        app.UseRouting();

                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet(callbackPath, async context =>
                            {
                                if (tcs.Task.IsCompleted)
                                {
                                    context.Response.StatusCode = StatusCodes.Status200OK;
                                    await context.Response.WriteAsync("OK");
                                    return;
                                }

                                var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var kv in context.Request.Query)
                                    q[kv.Key] = kv.Value.ToString();

                                var hasError = q.ContainsKey("error");
                                var hasState = q.TryGetValue("state", out var s);

                                if (!hasError && (!hasState || !string.Equals(s, expectedState, StringComparison.Ordinal)))
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "text/html; charset=utf-8";
                                    await context.Response.WriteAsync("<html><body><h3>Invalid OAuth callback (state mismatch).</h3></body></html>");
                                    return;
                                }

                                context.Response.StatusCode = StatusCodes.Status200OK;
                                context.Response.ContentType = "text/html; charset=utf-8";
                                await context.Response.WriteAsync("<html><body><h3>Authorization complete. You can close this tab.</h3><script>window.close();</script></body></html>");

                                tcs.TrySetResult(q);
                            });
                        });
                    });
                })
                .Build();
        }

        private static string BuildAuthorizationUrl(string clientId, string redirectUri, string scope, string codeChallenge, string state)
        {
            scope = (scope ?? string.Empty).Trim();
            while (scope.Contains("  ", StringComparison.Ordinal))
                scope = scope.Replace("  ", " ", StringComparison.Ordinal);

            var sb = new StringBuilder();
            sb.Append(AuthorizationEndpoint);
            sb.Append("?client_id=").Append(Uri.EscapeDataString(clientId));
            sb.Append("&response_type=code");
            sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
            if (!string.IsNullOrWhiteSpace(scope))
                sb.Append("&scope=").Append(Uri.EscapeDataString(scope));
            sb.Append("&state=").Append(Uri.EscapeDataString(state));
            sb.Append("&code_challenge_method=S256");
            sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
            return sb.ToString();
        }

        private static async Task<TokenResult> ExchangeCodeForTokenAsync(
            string clientId,
            string? clientSecret,
            string redirectUri,
            string code,
            string codeVerifier,
            CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);

            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            }

            var body = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "authorization_code"),
                new("client_id", clientId),
                new("redirect_uri", redirectUri),
                new("code", code),
                new("code_verifier", codeVerifier),
            };

            if (!string.IsNullOrWhiteSpace(clientSecret))
                body.Add(new("client_secret", clientSecret));

            req.Content = new FormUrlEncodedContent(body);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token endpoint returned {(int)resp.StatusCode}: {json}");

            return ParseTokenResponse(json);
        }

        private static TokenResult ParseTokenResponse(string json)
        {
            TokenResponse? model;
            try
            {
                model = JsonSerializer.Deserialize<TokenResponse>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unable to parse token response JSON: {json}", ex);
            }

            if (model is null || string.IsNullOrWhiteSpace(model.AccessToken))
                throw new InvalidOperationException($"No access_token in token response: {json}");

            var expiresIn = model.ExpiresIn > 0 ? model.ExpiresIn : 3600;
            var expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            return new TokenResult(
                AccessToken: model.AccessToken!,
                TokenType: model.TokenType ?? "",
                ExpiresIn: expiresIn,
                Scope: model.Scope ?? "",
                RefreshToken: string.IsNullOrWhiteSpace(model.RefreshToken) ? null : model.RefreshToken,
                ExpiresAtUtc: expiresAtUtc
            );
        }

        private sealed class TokenResponse
        {
            [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
            [JsonPropertyName("token_type")] public string? TokenType { get; set; }
            [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
            [JsonPropertyName("scope")] public string? Scope { get; set; }
            [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        }

        private static (string verifier, string challenge) CreatePkcePair()
        {
            var verifier = CreateBase64UrlString(64);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            var challenge = Base64UrlEncode(hash);
            return (verifier, challenge);
        }

        private static string CreateBase64UrlString(int numBytes)
        {
            var bytes = RandomNumberGenerator.GetBytes(numBytes);
            return Base64UrlEncode(bytes);
        }

        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
