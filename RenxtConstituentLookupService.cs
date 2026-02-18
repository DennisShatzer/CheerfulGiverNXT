using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT
{
    public sealed class RenxtConstituentLookupService
    {
        public record ConstituentGridRow(
            int Id,
            string FullName,
            string Spouse,
            string Street,
            string City,
            string State,
            string Zip,
            string? LookupId = null
        );

        private readonly HttpClient _http;
        private static readonly Regex StartsWithNumber = new(@"^\s*\d+", RegexOptions.Compiled);

        // IMPORTANT: Do not set auth headers here.
        // They are injected per request by BlackbaudAuthHandler.
        public RenxtConstituentLookupService(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));

            if (_http.BaseAddress is null)
                _http.BaseAddress = new Uri("https://api.sky.blackbaud.com/");

            if (!_http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<IReadOnlyList<ConstituentGridRow>> SearchGridAsync(string rawInput, CancellationToken ct = default)
        {
            var input = Normalize(rawInput);
            if (string.IsNullOrWhiteSpace(input))
                return Array.Empty<ConstituentGridRow>();

            var isAddress = StartsWithNumber.IsMatch(input);
            var strict = isAddress ? "" : "&strict_search=true";

            var searchUrl =
                "constituent/v1/constituents/search" +
                $"?search_text={WebUtility.UrlEncode(input)}" +
                $"{strict}&include_inactive=true&limit=25";

            using var searchResp = await SendWithRetryAfterAsync(() => new HttpRequestMessage(HttpMethod.Get, searchUrl), ct);
            searchResp.EnsureSuccessStatusCode();

            using var searchStream = await searchResp.Content.ReadAsStreamAsync(ct);
            using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: ct);

            var ids = ExtractIds(searchDoc.RootElement).Distinct().Take(25).ToArray();
            if (ids.Length == 0)
                return Array.Empty<ConstituentGridRow>();

            const int maxConcurrency = 4;
            using var sem = new SemaphoreSlim(maxConcurrency);

            var tasks = ids.Select(async id =>
            {
                await sem.WaitAsync(ct);
                try { return await GetGridRowAsync(id, ct); }
                finally { sem.Release(); }
            });

            var rows = await Task.WhenAll(tasks);
            return rows.Where(r => r is not null).Cast<ConstituentGridRow>().ToList();
        }

        private async Task<ConstituentGridRow?> GetGridRowAsync(int id, CancellationToken ct)
        {
            var url = $"constituent/v1/constituents/{id}";
            using var resp = await SendWithRetryAfterAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

            if (!resp.IsSuccessStatusCode)
                return null;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var lookupId = GetString(root, "lookup_id");

            var fullName = GetString(root, "name");
            if (string.IsNullOrWhiteSpace(fullName))
                fullName = BuildName(root);

            // Spouse
            var spouse = "";
            if (root.TryGetProperty("spouse", out var spouseObj) && spouseObj.ValueKind == JsonValueKind.Object)
            {
                var sf = GetString(spouseObj, "first");
                var sl = GetString(spouseObj, "last");
                spouse = Normalize($"{sf} {sl}");
            }

            // Address
            var street = ""; var city = ""; var state = ""; var zip = "";
            if (root.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.Object)
            {
                street = GetStringOrNumber(addr, "address_lines")
                    .Replace("\r\n", ", ")
                    .Replace("\n", ", ");

                city = GetStringOrNumber(addr, "city");

                state =
                    GetStringOrNumber(addr, "state")
                    .IfBlank(GetStringOrNumber(addr, "region"))
                    .IfBlank(GetStringOrNumber(addr, "county"));

                zip = GetStringOrNumber(addr, "postal_code");
            }

            return new ConstituentGridRow(
                Id: id,
                FullName: fullName,
                Spouse: spouse,
                Street: street,
                City: city,
                State: state,
                Zip: zip,
                LookupId: lookupId
            );
        }

        private static IEnumerable<int> ExtractIds(JsonElement root)
        {
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp) && TryReadInt32(idProp, out var id))
                        yield return id;
                    else if (item.TryGetProperty("record_id", out var ridProp) && TryReadInt32(ridProp, out var rid))
                        yield return rid;
                }
            }
            else if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                    if (item.TryGetProperty("id", out var idProp) && TryReadInt32(idProp, out var id))
                        yield return id;
            }
        }

        private static bool TryReadInt32(JsonElement el, out int value)
        {
            value = 0;

            if (el.ValueKind == JsonValueKind.Number)
                return el.TryGetInt32(out value);

            if (el.ValueKind == JsonValueKind.String)
                return int.TryParse(el.GetString(), out value);

            return false;
        }

        private static string BuildName(JsonElement root)
        {
            var first = GetString(root, "first");
            var last = GetString(root, "last");
            var org = GetString(root, "organization_name");

            var name = Normalize($"{first} {last}");
            return string.IsNullOrWhiteSpace(name) ? org : name;
        }

        private static string Normalize(string s) =>
            Regex.Replace((s ?? "").Trim(), @"\s+", " ");

        private static string GetString(JsonElement obj, string propertyName)
        {
            if (obj.ValueKind != JsonValueKind.Object)
                return "";

            return obj.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.String
                ? (p.GetString() ?? "")
                : "";
        }

        private static string GetStringOrNumber(JsonElement obj, string propertyName)
        {
            if (obj.ValueKind != JsonValueKind.Object)
                return "";

            if (!obj.TryGetProperty(propertyName, out var p))
                return "";

            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? "",
                JsonValueKind.Number => p.ToString() ?? "",
                _ => ""
            };
        }

        private async Task<HttpResponseMessage> SendWithRetryAfterAsync(
            Func<HttpRequestMessage> makeRequest,
            CancellationToken ct)
        {
            while (true)
            {
                using var req = makeRequest();
                var resp = await _http.SendAsync(req, ct);

                if ((int)resp.StatusCode is 429 or 403)
                {
                    if (resp.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
                    {
                        resp.Dispose();
                        await Task.Delay(delta, ct);
                        continue;
                    }
                }

                return resp;
            }
        }
    }

    internal static class StringExtensions
    {
        public static string IfBlank(this string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
