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

        /// <summary>
        /// Fund reference (ID + display name/description).
        /// </summary>
        public record FundRef(int Id, string Name);

        private readonly HttpClient _http;
        private static readonly Regex StartsWithNumber = new(@"^\s*\d+", RegexOptions.Compiled);

        // Fund name cache (fund list changes infrequently; caching avoids lots of API calls)
        private readonly SemaphoreSlim _fundCacheLock = new(1, 1);
        private Dictionary<int, string>? _fundNameCache;
        private DateTimeOffset _fundCacheLoadedAtUtc = DateTimeOffset.MinValue;
        private static readonly TimeSpan FundCacheTtl = TimeSpan.FromHours(12);

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

        /// <summary>
        /// Returns the distinct funds this constituent has contributed to, derived from Gift API gift_splits.
        /// </summary>
        public async Task<IReadOnlyList<FundRef>> GetContributedFundsAsync(
            int constituentId,
            int maxGiftsToScan = 1000,
            CancellationToken ct = default)
        {
            var fundIds = await GetContributedFundIdsAsync(constituentId, maxGiftsToScan, ct);
            if (fundIds.Count == 0)
                return Array.Empty<FundRef>();

            var nameMap = await GetFundNameMapAsync(ct);

            return fundIds
                .Select(id =>
                {
                    var name = nameMap.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"Fund #{id}";
                    return new FundRef(id, name);
                })
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<IReadOnlyList<int>> GetContributedFundIdsAsync(
            int constituentId,
            int maxGiftsToScan,
            CancellationToken ct)
        {
            if (constituentId <= 0)
                return Array.Empty<int>();

            maxGiftsToScan = Math.Max(1, maxGiftsToScan);

            const int pageSize = 200;
            var offset = 0;
            var fundIds = new HashSet<int>();

            while (offset < maxGiftsToScan)
            {
                var limit = Math.Min(pageSize, maxGiftsToScan - offset);

                // Gift List filtered by constituent_id
                var url = $"gift/v1/gifts?constituent_id={constituentId}&limit={limit}&offset={offset}";
                using var resp = await SendWithRetryAfterAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var gifts = ExtractItems(doc.RootElement).ToList();
                if (gifts.Count == 0)
                    break;

                foreach (var gift in gifts)
                {
                    if (gift.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!gift.TryGetProperty("gift_splits", out var splits) || splits.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var split in splits.EnumerateArray())
                    {
                        if (split.ValueKind != JsonValueKind.Object)
                            continue;

                        if (split.TryGetProperty("fund_id", out var fundIdEl) &&
                            TryReadInt32(fundIdEl, out var fundId) &&
                            fundId > 0)
                        {
                            fundIds.Add(fundId);
                        }
                    }
                }

                offset += gifts.Count;
                if (gifts.Count < limit)
                    break;
            }

            return fundIds.OrderBy(x => x).ToList();
        }

        private async Task<Dictionary<int, string>> GetFundNameMapAsync(CancellationToken ct)
        {
            if (_fundNameCache is not null && (DateTimeOffset.UtcNow - _fundCacheLoadedAtUtc) < FundCacheTtl)
                return _fundNameCache;

            await _fundCacheLock.WaitAsync(ct);
            try
            {
                if (_fundNameCache is not null && (DateTimeOffset.UtcNow - _fundCacheLoadedAtUtc) < FundCacheTtl)
                    return _fundNameCache;

                var map = new Dictionary<int, string>();

                const int pageSize = 5000;
                var offset = 0;

                while (true)
                {
                    var url = $"fundraising/v1/funds?include_inactive=true&limit={pageSize}&offset={offset}";
                    using var resp = await SendWithRetryAfterAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
                    resp.EnsureSuccessStatusCode();

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    var funds = ExtractItems(doc.RootElement).ToList();
                    if (funds.Count == 0)
                        break;

                    foreach (var fund in funds)
                    {
                        if (fund.ValueKind != JsonValueKind.Object)
                            continue;

                        if (!fund.TryGetProperty("id", out var idEl) || !TryReadInt32(idEl, out var id) || id <= 0)
                            continue;

                        var name =
                            GetString(fund, "description")
                            .IfBlank(GetString(fund, "name"))
                            .IfBlank(GetString(fund, "title"));

                        if (!map.ContainsKey(id))
                            map[id] = name;
                    }

                    offset += funds.Count;
                    if (funds.Count < pageSize)
                        break;

                    // Safety stop if the API ignores offset for some reason.
                    if (offset > 200_000)
                        break;
                }

                _fundNameCache = map;
                _fundCacheLoadedAtUtc = DateTimeOffset.UtcNow;

                return map;
            }
            finally
            {
                _fundCacheLock.Release();
            }
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

        private static IEnumerable<JsonElement> ExtractItems(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    yield return item;
                yield break;
            }

            if (root.ValueKind != JsonValueKind.Object)
                yield break;

            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    yield return item;
                yield break;
            }

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                    yield return item;
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
