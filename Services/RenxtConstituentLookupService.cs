using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CheerfulGiverNXT.Infrastructure.Logging;

namespace CheerfulGiverNXT.Services
{
    public sealed class RenxtConstituentLookupService
    {
        public record ConstituentGridRow(
            int Id,
            string FullName,
            string Spouse,
            string Contact,
            string Street,
            string City,
            string State,
            string Zip,
            string? LookupId = null
        );

        public record FundRef(int Id, string Name);

        private readonly HttpClient _http;
        private static readonly Regex StartsWithNumber = new(@"^\s*\d+", RegexOptions.Compiled);
        private static readonly Regex DigitsOnly = new(@"\D+", RegexOptions.Compiled);

        // Fund name cache (to avoid calling /fundraising/v1/funds repeatedly).
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

        public sealed record CreatedConstituent(int Id, IReadOnlyList<string> Warnings);

        /// <summary>
        /// Creates an Individual constituent. Creates the constituent first, then attempts to add email/phone/address.
        /// Returns the created constituent id and any non-fatal warnings (e.g., contact info couldn't be added).
        /// </summary>
        public async Task<CreatedConstituent> CreateIndividualConstituentAsync(
            string? firstName,
            string lastName,
            string? email,
            string? phone,
            string? addressLine1,
            string? addressLine2,
            string? city,
            string? state,
            string? postalCode,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(lastName))
                throw new ArgumentException("Last name is required.", nameof(lastName));

            // 1) Create the constituent (minimal payload is the most reliable).
            var createPayload = new Dictionary<string, object?>
            {
                ["type"] = "Individual",
                ["last"] = lastName.Trim()
            };
            if (!string.IsNullOrWhiteSpace(firstName))
                createPayload["first"] = firstName.Trim();

            var createJson = JsonSerializer.Serialize(createPayload);

            using var createResp = await SendWithRetryAfterAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "constituent/v1/constituents")
                {
                    Content = new StringContent(createJson, Encoding.UTF8, "application/json")
                },
                ct);

            var createBody = await createResp.Content.ReadAsStringAsync(ct);
            if (!createResp.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatSkyApiError("Create constituent", createResp.StatusCode, createBody));

            var newId = ParseId(createBody);

            // 2) Best-effort contact info.
            var warnings = new List<string>();

            if (!string.IsNullOrWhiteSpace(email))
            {
                try { await CreateEmailAsync(newId, email!.Trim(), ct); }
                catch (Exception ex) { warnings.Add("Email: " + ex.Message); try { _ = ErrorLogger.Log(ex, "RenxtConstituentLookupService.CreateEmail"); } catch { } }
            }

            var normalizedPhone = NormalizePhone(phone);
            if (!string.IsNullOrWhiteSpace(normalizedPhone))
            {
                try { await CreatePhoneAsync(newId, normalizedPhone!, "Home", ct); }
                catch (Exception ex) { warnings.Add("Phone: " + ex.Message); try { _ = ErrorLogger.Log(ex, "RenxtConstituentLookupService.CreatePhone"); } catch { } }
            }

            if (!string.IsNullOrWhiteSpace(addressLine1) || !string.IsNullOrWhiteSpace(addressLine2))
            {
                try
                {
                    var lines = (addressLine1 ?? "").Trim();
                    var line2 = (addressLine2 ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(line2))
                        lines = string.IsNullOrWhiteSpace(lines) ? line2 : (lines + "\n" + line2);

                    await CreateAddressAsync(
                        newId,
                        lines,
                        (city ?? "").Trim(),
                        (state ?? "").Trim(),
                        (postalCode ?? "").Trim(),
                        "Home",
                        ct);
                }
                catch (Exception ex)
                {
                    try { _ = ErrorLogger.Log(ex, "RenxtConstituentLookupService.CreateAddress"); } catch { }
                    warnings.Add("Address: " + ex.Message);
                }
            }

            return new CreatedConstituent(newId, warnings);
        }

        /// <summary>
        /// Creates an Organization constituent. Creates the constituent first, then attempts to add email/phone/address.
        /// Returns the created constituent id and any non-fatal warnings (e.g., contact info couldn't be added).
        /// </summary>
        public async Task<CreatedConstituent> CreateOrganizationConstituentAsync(
            string name,
            string? email,
            string? phone,
            string? addressLine1,
            string? addressLine2,
            string? city,
            string? state,
            string? postalCode,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Organization name is required.", nameof(name));

            // 1) Create the constituent.
            var createPayload = new Dictionary<string, object?>
            {
                ["type"] = "Organization",
                ["name"] = name.Trim()
            };

            var createJson = JsonSerializer.Serialize(createPayload);

            using var createResp = await SendWithRetryAfterAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "constituent/v1/constituents")
                {
                    Content = new StringContent(createJson, Encoding.UTF8, "application/json")
                },
                ct);

            var createBody = await createResp.Content.ReadAsStringAsync(ct);
            if (!createResp.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatSkyApiError("Create constituent", createResp.StatusCode, createBody));

            var newId = ParseId(createBody);

            // 2) Best-effort contact info.
            var warnings = new List<string>();

            if (!string.IsNullOrWhiteSpace(email))
            {
                try { await CreateEmailAsync(newId, email!.Trim(), ct); }
                catch (Exception ex) { warnings.Add("Email: " + ex.Message); try { _ = ErrorLogger.Log(ex, "RenxtConstituentLookupService.CreateEmail"); } catch { } }
            }

            var normalizedPhone = NormalizePhone(phone);
            if (!string.IsNullOrWhiteSpace(normalizedPhone))
            {
                // "Work" and "Business" are common defaults for orgs; if your instance uses different table values,
                // the call may fail and you'll see a warning (but the organization will still be created).
                try { await CreatePhoneAsync(newId, normalizedPhone!, "Work", ct); }
                catch (Exception ex) { warnings.Add("Phone: " + ex.Message); try { _ = ErrorLogger.Log(ex, "RenxtConstituentLookupService.CreatePhone"); } catch { } }
            }

            if (!string.IsNullOrWhiteSpace(addressLine1) || !string.IsNullOrWhiteSpace(addressLine2))
            {
                try
                {
                    var lines = (addressLine1 ?? "").Trim();
                    var line2 = (addressLine2 ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(line2))
                        lines = string.IsNullOrWhiteSpace(lines) ? line2 : (lines + "\n" + line2);

                    await CreateAddressAsync(
                        newId,
                        lines,
                        (city ?? "").Trim(),
                        (state ?? "").Trim(),
                        (postalCode ?? "").Trim(),
                        "Business",
                        ct);
                }
                catch (Exception ex)
                {
                    try { _ = ErrorLogger.Log(ex, "RenxtConstituentLookupService.CreateAddress"); } catch { }
                    warnings.Add("Address: " + ex.Message);
                }
            }

            return new CreatedConstituent(newId, warnings);
        }

        private async Task CreateEmailAsync(int constituentId, string address, CancellationToken ct)
        {
            // Endpoint name inferred from Blackbaud community discussions about /constituent/v1/emailaddresses.
            var payload = new Dictionary<string, object?>
            {
                ["constituent_id"] = constituentId.ToString(),
                ["address"] = address,
                ["type"] = "Email",
                ["primary"] = true,
                ["inactive"] = false
            };

            await PostAndEnsureAsync("constituent/v1/emailaddresses", payload, ct);
        }

        private async Task CreatePhoneAsync(int constituentId, string number, string phoneType, CancellationToken ct)
        {
            // The connectors docs list operations for creating phones on constituents.
            var payload = new Dictionary<string, object?>
            {
                ["constituent_id"] = constituentId.ToString(),
                ["type"] = phoneType,
                ["number"] = number,
                ["primary"] = true,
                ["do_not_call"] = false,
                ["inactive"] = false
            };

            await PostAndEnsureAsync("constituent/v1/phones", payload, ct);
        }

        private async Task CreateAddressAsync(int constituentId, string addressLines, string city, string state, string postalCode, string addressType, CancellationToken ct)
        {
            // PATCH examples confirm the addresses endpoint and common fields (address_lines, city, state, postal_code, type, preferred...).
            var payload = new Dictionary<string, object?>
            {
                ["constituent_id"] = constituentId.ToString(),
                ["type"] = addressType,
                ["address_lines"] = addressLines,
                ["city"] = city,
                ["state"] = state,
                ["postal_code"] = postalCode,
                ["country"] = "United States",
                ["preferred"] = true,
                ["do_not_mail"] = false
            };

            await PostAndEnsureAsync("constituent/v1/addresses", payload, ct);
        }

        private async Task PostAndEnsureAsync(string relativeUrl, Dictionary<string, object?> payload, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(payload);

            using var resp = await SendWithRetryAfterAsync(
                () => new HttpRequestMessage(HttpMethod.Post, relativeUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                },
                ct);

            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatSkyApiError($"POST {relativeUrl}", resp.StatusCode, body));
        }

        private static int ParseId(string responseBody)
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("id", out var idEl))
                throw new FormatException("Create response did not include an id.");

            var s = idEl.ValueKind switch
            {
                JsonValueKind.Number => idEl.ToString(),
                JsonValueKind.String => idEl.GetString(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(s) || !int.TryParse(s, out var id))
                throw new FormatException("Create response id was not numeric.");

            return id;
        }

        private static string FormatSkyApiError(string operation, HttpStatusCode status, string body)
        {
            var message = ExtractSkyApiMessage(body);
            var code = (int)status;
            return string.IsNullOrWhiteSpace(message)
                ? $"{operation} failed ({code})."
                : $"{operation} failed ({code}): {message}";
        }

        private static string ExtractSkyApiMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var first = root[0];
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                        return msg.GetString() ?? "";
                }

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msgObj) && msgObj.ValueKind == JsonValueKind.String)
                    return msgObj.GetString() ?? "";
            }
            catch
            {
                // Ignore parse errors and fall back to raw body.
            }

            body = body.Trim();
            return body.Length <= 400 ? body : body.Substring(0, 400) + "…";
        }

        private static string? NormalizePhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;

            var digits = DigitsOnly.Replace(phone, "");
            if (digits.Length == 11 && digits.StartsWith("1"))
                digits = digits.Substring(1);

            if (digits.Length == 10)
                return $"{digits.Substring(0, 3)}-{digits.Substring(3, 3)}-{digits.Substring(6, 4)}";

            // If it isn't a standard US number, return trimmed input and let SKY validate.
            return phone.Trim();
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
        /// Returns distinct fund IDs the constituent has contributed to by scanning gifts and their splits.
        /// </summary>
        public async Task<IReadOnlyList<int>> GetContributedFundIdsAsync(
            int constituentId,
            int maxGiftsToScan = 1000,
            CancellationToken ct = default)
        {
            if (constituentId <= 0)
                return Array.Empty<int>();

            maxGiftsToScan = Math.Max(1, maxGiftsToScan);

            const int pageSize = 200;
            var fundIds = new HashSet<int>();

            var offset = 0;
            while (offset < maxGiftsToScan)
            {
                var limit = Math.Min(pageSize, maxGiftsToScan - offset);

                // Gift List endpoint filtered by constituent_id.
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
                    if (gift.ValueKind == JsonValueKind.Object &&
                        gift.TryGetProperty("gift_splits", out var splits) &&
                        splits.ValueKind == JsonValueKind.Array)
                    {
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
                }

                offset += gifts.Count;
                if (gifts.Count < limit)
                    break;
            }

            return fundIds.OrderBy(x => x).ToList();
        }

        /// <summary>
        /// Returns distinct fund IDs + names for the constituent (names resolved via Fundraising funds list).
        /// </summary>
        public async Task<IReadOnlyList<FundRef>> GetContributedFundsAsync(
            int constituentId,
            int maxGiftsToScan = 1000,
            CancellationToken ct = default)
        {
            var ids = await GetContributedFundIdsAsync(constituentId, maxGiftsToScan, ct);
            if (ids.Count == 0)
                return Array.Empty<FundRef>();

            var nameMap = await GetFundNameMapAsync(ct);

            return ids
                .Select(id => new FundRef(id, nameMap.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"Fund #{id}"))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
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

                    if (offset > 200_000) // safety
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

            // Preferred contact (phone, else email)
            var phone = "";
            if (root.TryGetProperty("phone", out var phoneObj) && phoneObj.ValueKind == JsonValueKind.Object)
                phone = GetStringOrNumber(phoneObj, "number");

            var email = "";
            if (root.TryGetProperty("email", out var emailObj) && emailObj.ValueKind == JsonValueKind.Object)
                email = GetStringOrNumber(emailObj, "address");

            var contact = string.IsNullOrWhiteSpace(phone)
                ? email
                : (string.IsNullOrWhiteSpace(email) ? phone : $"{phone} / {email}");

            return new ConstituentGridRow(
                Id: id,
                FullName: fullName,
                Spouse: spouse,
                Contact: contact,
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
