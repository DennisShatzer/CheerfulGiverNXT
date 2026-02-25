// RenxtGiftServer.cs
// Pledge-only create (RE NXT Gifts v2) with required Schedule + optional Installments logic.
// Namespace: CheerfulGiverNXT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CheerfulGiverNXT.Infrastructure.AppMode;

namespace CheerfulGiverNXT.Services
{
    /// <summary>
    /// Minimal pledge (commitment only; no money received) service using SKY API Gifts v2.
    /// - Creates pledge with required schedule (frequency, number_of_installments, start_date).
    /// - Optionally verifies installments exist; can add installments if needed.
    ///
    /// IMPORTANT: This class does not manage tokens. Headers are injected per request
    /// by BlackbaudAuthHandler on the shared HttpClient.
    /// </summary>
    public sealed class RenxtGiftServer
    {
        private readonly HttpClient _http;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public RenxtGiftServer(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            if (_http.BaseAddress is null)
                _http.BaseAddress = new Uri("https://api.sky.blackbaud.com/");
        }

        public sealed record CreatePledgeRequest(
            string ConstituentId,
            decimal Amount,
            DateTime PledgeDate,
            string FundId,

            PledgeFrequency Frequency,
            int NumberOfInstallments,
            DateTime StartDate,

            string? PaymentMethod = "Other",
            string? Comments = null,
            string? CampaignId = null,
            string? AppealId = null,
            string? PackageId = null,

            bool VerifyInstallmentsAfterCreate = true,
            bool AddInstallmentsIfMissing = true
        );

        public sealed record CreatePledgeResult(
            string GiftId,
            string RawCreateResponseJson,
            bool InstallmentsPresentAfterCreate,
            string? RawInstallmentListJson = null,
            string? RawInstallmentAddJson = null
        );

        public sealed record PledgeInstallment(int Sequence, DateTime Date, decimal Amount);

        // ------------------------------------------------------------
        // Gift search (best-effort) - used for duplicate detection.
        // ------------------------------------------------------------

        public sealed record GiftSearchItem(
            string Id,
            string? GiftType,
            DateTime? GiftDate,
            decimal? Amount
        );

        /// <summary>
        /// Best-effort query of gifts for a constituent within a gift_date window.
        /// The SKY API response shape and supported query params can vary by tenant.
        /// This method is intentionally tolerant: it returns an empty list if it can't parse.
        /// </summary>
        public async Task<IReadOnlyList<GiftSearchItem>> SearchGiftsAsync(
            string constituentId,
            DateTime fromGiftDate,
            DateTime toGiftDate,
            int limit = 50,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(constituentId))
                throw new ArgumentNullException(nameof(constituentId));

            var from = fromGiftDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = toGiftDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Attempt a few common parameter names used in SKY API collections.
            // If a tenant doesn't support these params, the call may still succeed but ignore them.
            // If it fails, callers should treat that as "unknown" and continue.
            var url =
                "gft-gifts/v2/gifts" +
                "?constituent_id=" + Uri.EscapeDataString(constituentId.Trim()) +
                "&from_gift_date=" + Uri.EscapeDataString(from) +
                "&to_gift_date=" + Uri.EscapeDataString(to) +
                "&limit=" + Math.Clamp(limit, 1, 200).ToString(CultureInfo.InvariantCulture);

            using var resp = await SendWithRetryAfterAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

            return ParseGiftSearch(body);
        }

        private static IReadOnlyList<GiftSearchItem> ParseGiftSearch(string json)
        {
            var list = new List<GiftSearchItem>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                JsonElement items;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // Common shapes: { value:[...] }, { items:[...] }, { results:[...] }
                    if (root.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array) { }
                    else if (root.TryGetProperty("items", out items) && items.ValueKind == JsonValueKind.Array) { }
                    else if (root.TryGetProperty("results", out items) && items.ValueKind == JsonValueKind.Array) { }
                    else if (root.TryGetProperty("gifts", out items) && items.ValueKind == JsonValueKind.Array) { }
                    else return list;
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    items = root;
                }
                else
                {
                    return list;
                }

                foreach (var it in items.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object)
                        continue;

                    var id = TryGetString(it, "id") ?? TryGetString(it, "gift_id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var giftType = TryGetString(it, "gift_type") ?? TryGetString(it, "type");

                    DateTime? giftDate = null;
                    var dateStr = TryGetString(it, "gift_date") ?? TryGetString(it, "date");
                    if (!string.IsNullOrWhiteSpace(dateStr) && DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                        giftDate = dt.Date;

                    decimal? amount = null;
                    if (it.TryGetProperty("amount", out var amountEl))
                    {
                        // amount may be { value: 123.45 } or a number
                        if (amountEl.ValueKind == JsonValueKind.Object && amountEl.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                        {
                            if (v.TryGetDecimal(out var d)) amount = d;
                        }
                        else if (amountEl.ValueKind == JsonValueKind.Number)
                        {
                            if (amountEl.TryGetDecimal(out var d)) amount = d;
                        }
                    }

                    list.Add(new GiftSearchItem(id!, giftType, giftDate, amount));
                }
            }
            catch
            {
                // ignore parse errors; return best-effort (possibly empty)
            }

            return list;
        }

        private static string? TryGetString(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(name, out var p)) return null;
            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.ToString(),
                _ => null
            };
        }

        public async Task<CreatePledgeResult> CreatePledgeAsync(CreatePledgeRequest req, CancellationToken ct = default)
        {
            Validate(req);

            // HARD GUARD: never allow pledge posting when Demo mode is enabled or when disabled by config.
            if (!SkyPostingPolicy.IsPostingAllowed(out var reason))
                throw new InvalidOperationException("SKY API pledge posting is disabled. " + (reason ?? string.Empty));

            var payload = new
            {
                gift_type = "Pledge",
                amount = new { value = req.Amount },
                constituent = new { id = req.ConstituentId.Trim() },
                gift_date = req.PledgeDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

                comments = string.IsNullOrWhiteSpace(req.Comments) ? null : req.Comments.Trim(),

                gift_splits = new[]
                {
                    new
                    {
                        amount = new { value = req.Amount },
                        fund_id = req.FundId.Trim(),
                        campaign_id = string.IsNullOrWhiteSpace(req.CampaignId) ? null : req.CampaignId.Trim(),
                        appeal_id = string.IsNullOrWhiteSpace(req.AppealId) ? null : req.AppealId.Trim(),
                        package_id = string.IsNullOrWhiteSpace(req.PackageId) ? null : req.PackageId.Trim()
                    }
                },

                payments = new[]
                {
                    new { method = string.IsNullOrWhiteSpace(req.PaymentMethod) ? "Other" : req.PaymentMethod.Trim() }
                },

                schedule = new
                {
                    frequency = ToApiFrequency(req.Frequency),
                    number_of_installments = req.NumberOfInstallments,
                    start_date = req.StartDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                }
            };

            var createJson = JsonSerializer.Serialize(payload, JsonOpts);

            using var resp = await SendWithRetryAfterAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, "gft-gifts/v2/gifts");
                r.Content = new StringContent(createJson, Encoding.UTF8, "application/json");
                return r;
            }, ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"HTTP {(int)resp.StatusCode}: {body}{Environment.NewLine}" +
                    $"Request payload: {createJson}");

            var giftId = ExtractIdOrThrow(body);

            string? listJson = null;
            string? addJson = null;
            bool installmentsPresent = false;

            if (req.VerifyInstallmentsAfterCreate)
            {
                try
                {
                    (installmentsPresent, listJson) = await TryGetInstallmentsAsync(giftId, ct).ConfigureAwait(false);

                    if (!installmentsPresent && req.AddInstallmentsIfMissing)
                    {
                        var installments = BuildInstallments(
                            totalAmount: req.Amount,
                            firstInstallmentDateLocal: req.StartDate.Date,
                            frequency: req.Frequency,
                            numberOfInstallments: req.NumberOfInstallments);

                        addJson = await AddInstallmentsAsync(giftId, installments, ct).ConfigureAwait(false);
                        (installmentsPresent, _) = await TryGetInstallmentsAsync(giftId, ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Best-effort: do not fail pledge creation if installments endpoints aren't available.
                }
            }

            return new CreatePledgeResult(
                GiftId: giftId,
                RawCreateResponseJson: body,
                InstallmentsPresentAfterCreate: installmentsPresent,
                RawInstallmentListJson: listJson,
                RawInstallmentAddJson: addJson
            );
        }

        /// <summary>
        /// Deletes a gift by id using the Gift API (v1).
        ///
        /// Endpoint is documented as DELETE /gifts/{id} under the Gift API, and the service base in SKY is /gift/v1.
        /// In other words: DELETE https://api.sky.blackbaud.com/gift/v1/gifts/{id}
        ///
        /// NOTE: Deletion is subject to SKY rules (some gifts cannot be deleted).
        /// </summary>
        public async Task DeleteGiftAsync(string giftId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(giftId))
                throw new ArgumentNullException(nameof(giftId));

            // HARD GUARD: never allow write operations when Demo mode is enabled or posting is disabled by policy.
            if (!SkyPostingPolicy.IsPostingAllowed(out var reason))
                throw new InvalidOperationException("SKY API gift deletion is disabled. " + (reason ?? string.Empty));

            var url = "gift/v1/gifts/" + Uri.EscapeDataString(giftId.Trim());

            using var resp = await SendWithRetryAfterAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, url),
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
        }

        public async Task<string> GetInstallmentsRawAsync(string giftId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(giftId)) throw new ArgumentNullException(nameof(giftId));

            using var resp = await SendWithRetryAfterAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"gft-gifts/v2/gifts/{Uri.EscapeDataString(giftId)}/installments"),
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

            return body;
        }

        public async Task<string> AddInstallmentsAsync(string giftId, IReadOnlyList<PledgeInstallment> installments, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(giftId)) throw new ArgumentNullException(nameof(giftId));
            if (installments is null || installments.Count == 0) throw new ArgumentException("At least one installment is required.", nameof(installments));

            var payload = new
            {
                installments = installments.Select(i => new
                {
                    date = ToIsoZulu(i.Date.Date),
                    amount = i.Amount,
                    sequence = i.Sequence
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);

            using var resp = await SendWithRetryAfterAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, $"gft-gifts/v2/gifts/{Uri.EscapeDataString(giftId)}/installments");
                r.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return r;
            }, ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

            return body;
        }

        /// <summary>
        /// Builds installments that ALWAYS sum exactly to totalAmount (cent-perfect).
        /// </summary>
        public static List<PledgeInstallment> BuildInstallments(decimal totalAmount, DateTime firstInstallmentDateLocal, PledgeFrequency frequency, int numberOfInstallments)
        {
            if (numberOfInstallments <= 0) throw new ArgumentOutOfRangeException(nameof(numberOfInstallments));
            if (totalAmount <= 0m) throw new ArgumentOutOfRangeException(nameof(totalAmount));

            int totalCents = (int)Math.Round(totalAmount * 100m, 0, MidpointRounding.AwayFromZero);
            int baseCents = totalCents / numberOfInstallments;
            int remainder = totalCents % numberOfInstallments;

            var list = new List<PledgeInstallment>(numberOfInstallments);
            for (int i = 0; i < numberOfInstallments; i++)
            {
                int cents = baseCents + (i < remainder ? 1 : 0);
                decimal amt = cents / 100m;
                DateTime date = AddFrequency(firstInstallmentDateLocal.Date, frequency, i);

                list.Add(new PledgeInstallment(Sequence: i + 1, Date: date, Amount: amt));
            }

            return list;
        }

        private static void Validate(CreatePledgeRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.ConstituentId))
                throw new ArgumentException("ConstituentId is required.", nameof(req));
            if (string.IsNullOrWhiteSpace(req.FundId))
                throw new ArgumentException("FundId is required (must be the fund system ID).", nameof(req));
            if (req.Amount <= 0m)
                throw new ArgumentException("Amount must be > 0.", nameof(req));
            if (req.NumberOfInstallments <= 0)
                throw new ArgumentException("NumberOfInstallments must be greater than zero.", nameof(req));
        }

        private static string ExtractIdOrThrow(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.ValueKind switch
                    {
                        JsonValueKind.String => idProp.GetString(),
                        JsonValueKind.Number => idProp.ToString(),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(id))
                        return id!;
                }
            }
            catch
            {
                // fall through
            }

            throw new InvalidOperationException($"Pledge created but response did not include an id. Raw: {json}");
        }

        private async Task<(bool hasAny, string rawJson)> TryGetInstallmentsAsync(string giftId, CancellationToken ct)
        {
            using var resp = await SendWithRetryAfterAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"gft-gifts/v2/gifts/{Uri.EscapeDataString(giftId)}/installments"),
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (false, body);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.Array)
                {
                    return (value.GetArrayLength() > 0, body);
                }

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("installments", out var inst) &&
                    inst.ValueKind == JsonValueKind.Array)
                {
                    return (inst.GetArrayLength() > 0, body);
                }

                if (root.ValueKind == JsonValueKind.Array)
                    return (root.GetArrayLength() > 0, body);
            }
            catch
            {
                // ignore parse errors; treat as unknown
            }

            return (false, body);
        }

        private static string ToApiFrequency(PledgeFrequency freq) => freq switch
        {
            PledgeFrequency.Weekly => "Weekly",
            PledgeFrequency.Monthly => "Monthly",
            PledgeFrequency.Quarterly => "Quarterly",
            PledgeFrequency.Annually => "Annually",
            _ => "Monthly"
        };

        private static DateTime AddFrequency(DateTime start, PledgeFrequency freq, int offset) => freq switch
        {
            PledgeFrequency.Weekly => start.AddDays(7 * offset),
            PledgeFrequency.Monthly => start.AddMonths(offset),
            PledgeFrequency.Quarterly => start.AddMonths(3 * offset),
            PledgeFrequency.Annually => start.AddYears(offset),
            _ => start.AddMonths(offset)
        };

        private static string ToIsoZulu(DateTime dateLocalDateOnly)
        {
            var utc = DateTime.SpecifyKind(dateLocalDateOnly.Date, DateTimeKind.Utc);
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private async Task<HttpResponseMessage> SendWithRetryAfterAsync(
            Func<HttpRequestMessage> makeRequest,
            CancellationToken ct)
        {
            while (true)
            {
                using var req = makeRequest();
                var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

                if ((int)resp.StatusCode is 429 or 403)
                {
                    if (resp.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
                    {
                        resp.Dispose();
                        await Task.Delay(delta, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                return resp;
            }
        }
    }
}
