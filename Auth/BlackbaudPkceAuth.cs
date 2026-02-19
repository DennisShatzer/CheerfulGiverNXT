using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Auth
{
    public static class BlackbaudPkceAuth
    {
        // Canonical auth endpoint (may redirect to app.blackbaud.com/oauth/authorize)
        private const string AuthorizationEndpoint = "https://oauth2.sky.blackbaud.com/authorization";
        private const string TokenEndpoint = "https://oauth2.sky.blackbaud.com/token";

        public sealed record TokenResult(
            string AccessToken,
            string TokenType,
            int ExpiresIn,
            string Scope,
            string? RefreshToken
        );

        /// <summary>
        /// Minimal Authorization Code + PKCE flow for a PUBLIC client (no client_secret).
        /// You MUST register the exact redirectUri in your Blackbaud app settings.
        /// </summary>
        public static async Task<TokenResult> AcquireTokenAsync(
            string clientId,
            string redirectUri,
            string scope,                 // e.g. "rnxt.r"
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("clientId is required.");
            if (string.IsNullOrWhiteSpace(redirectUri)) throw new ArgumentException("redirectUri is required.");
            if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("scope is required.");

            var (verifier, challenge) = CreatePkcePair();
            var state = CreateBase64UrlString(32);

            var authUrl = BuildAuthorizationUrl(clientId, redirectUri, scope, challenge, state);

            // Start local listener for the redirect
            using var listener = new HttpListener();
            var prefix = RedirectUriToListenerPrefix(redirectUri);
            listener.Prefixes.Add(prefix);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                // Common fix (run once, elevated):
                // netsh http add urlacl url=http://127.0.0.1:58855/callback/ user=YOURMACHINE\\YOURUSER
                throw new InvalidOperationException(
                    $"Could not start HttpListener on '{prefix}'. " +
                    "On Windows you may need a URLACL reservation (netsh http add urlacl ...).", ex);
            }

            // Launch system browser
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            // Wait for the OAuth redirect
            var contextTask = listener.GetContextAsync();
            using (ct.Register(() => TryStop(listener)))
            {
                var context = await contextTask.ConfigureAwait(false);

                // Parse query params
                var query = ParseQueryString(context.Request.Url?.Query);

                // Always respond to browser so the tab can close
                await WriteBrowserResponseAsync(context.Response, "Authorization complete. You can close this window.").ConfigureAwait(false);

                if (query.TryGetValue("error", out var err))
                {
                    query.TryGetValue("error_description", out var errDesc);
                    throw new InvalidOperationException($"OAuth error: {err} {errDesc}".Trim());
                }

                if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                    throw new InvalidOperationException("No authorization code returned to redirect URI.");

                if (!query.TryGetValue("state", out var returnedState) || !string.Equals(returnedState, state, StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid state returned to redirect URI.");

                // Exchange code for access token
                return await ExchangeCodeForTokenAsync(clientId, redirectUri, code, verifier, ct).ConfigureAwait(false);
            }
        }

        private static string BuildAuthorizationUrl(string clientId, string redirectUri, string scope, string codeChallenge, string state)
        {
            // Minimal query string (Blackbaud accepts standard OAuth params + PKCE)
            // response_type=code
            // code_challenge_method=S256
            var sb = new StringBuilder();
            sb.Append(AuthorizationEndpoint);
            sb.Append("?client_id=").Append(Uri.EscapeDataString(clientId));
            sb.Append("&response_type=code");
            sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
            sb.Append("&scope=").Append(Uri.EscapeDataString(scope));
            sb.Append("&state=").Append(Uri.EscapeDataString(state));
            sb.Append("&code_challenge_method=S256");
            sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
            return sb.ToString();
        }

        private static async Task<TokenResult> ExchangeCodeForTokenAsync(
            string clientId,
            string redirectUri,
            string code,
            string codeVerifier,
            CancellationToken ct)
        {
            using var http = new HttpClient();

            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
            });

            using var resp = await http.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token endpoint returned {(int)resp.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string GetString(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
            int GetInt(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

            var accessToken = GetString("access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException($"No access_token in token response: {json}");

            return new TokenResult(
                AccessToken: accessToken,
                TokenType: GetString("token_type"),
                ExpiresIn: GetInt("expires_in"),
                Scope: GetString("scope"),
                RefreshToken: root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null
            );
        }

        private static (string verifier, string challenge) CreatePkcePair()
        {
            // 43-128 chars verifier, URL-safe
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
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static Dictionary<string, string> ParseQueryString(string? query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query)) return dict;

            var q = query.StartsWith("?") ? query.Substring(1) : query;
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0]);
                var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
                dict[key] = val;
            }
            return dict;
        }

        private static string RedirectUriToListenerPrefix(string redirectUri)
        {
            // HttpListener expects a prefix ending with /
            // Example redirectUri: http://127.0.0.1:58855/callback/
            // You MUST have a trailing slash in your redirectUri for this helper.
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
                throw new ArgumentException("redirectUri must be an absolute URI.");

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("For local loopback, use http:// redirect URIs (not https).");

            var path = uri.AbsolutePath.EndsWith("/") ? uri.AbsolutePath : uri.AbsolutePath + "/";
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{path}";
        }

        private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, string message)
        {
            try
            {
                var html = $"<html><body><h3>{WebUtility.HtmlEncode(message)}</h3></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html";
                response.ContentEncoding = Encoding.UTF8;
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                response.OutputStream.Close();
            }
            catch
            {
                // ignore
            }
        }

        private static void TryStop(HttpListener listener)
        {
            try { listener.Stop(); } catch { /* ignore */ }
        }
    }
}
