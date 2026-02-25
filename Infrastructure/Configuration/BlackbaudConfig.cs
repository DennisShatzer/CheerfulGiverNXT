using System;
using System.Configuration;

namespace CheerfulGiverNXT.Infrastructure.Configuration
{
    /// <summary>
    /// Central place to read Blackbaud-related settings from App.config.
    /// Keeps redirect URI out of code to avoid "invalid redirect_uri" mistakes.
    /// </summary>
    internal static class BlackbaudConfig
    {
        /// <summary>
        /// The redirect URI MUST exactly match one of the redirect URIs configured for your SKY API app
        /// (scheme, host, port, path, and trailing slash).
        /// </summary>
        public static string RedirectUri => RequireAbsoluteUri("BlackbaudRedirectUri");

        /// <summary>
        /// Space-delimited SKY API OAuth scopes requested during authorization.
        /// Configure in App.config appSetting 'BlackbaudScopes'.
        /// </summary>
        public static string Scopes => RequireNonEmpty("BlackbaudScopes");

        private static string RequireNonEmpty(string key)
        {
            var value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Missing appSetting '{key}' in App.config.");

            // Normalize common whitespace issues without changing the actual scope tokens.
            value = value.Trim();
            while (value.Contains("  ", StringComparison.Ordinal))
                value = value.Replace("  ", " ", StringComparison.Ordinal);

            return value;
        }

        private static string RequireAbsoluteUri(string key)
        {
            var value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Missing appSetting '{key}' in App.config.");

            // Validate only; do NOT normalize (Blackbaud requires exact string match, including trailing slash).
            if (!Uri.TryCreate(value, UriKind.Absolute, out _))
                throw new InvalidOperationException($"AppSetting '{key}' must be an absolute URI. Value: '{value}'");

            return value;
        }
    }
}
