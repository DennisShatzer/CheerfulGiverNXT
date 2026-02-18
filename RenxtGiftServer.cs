// RenxtGiftServer.cs
// Pledge-only create (RE NXT Gifts v2) with required Schedule + optional Installments logic.
// Namespace: CheerfulGiverNXT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT
{
    /// <summary>
    /// Minimal pledge (commitment only; no money received) service using SKY API Gifts v2.
    /// - Creates pledge with required schedule (frequency, number_of_installments, start_date).
    /// - Optionally verifies installments exist; can add installments if needed.
    /// </summary>
    public sealed class RenxtGiftServer
    {
        private readonly string _accessToken;
        private readonly string _subscriptionKey = "b5ba8b93e7844a27b54432ce3d4df680";

        // Reuse HttpClient for the whole process (important for performance).
        private static readonly HttpClient _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.sky.blackbaud.com/")
        };

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public RenxtGiftServer(string accessToken, string subscriptionKey)
        {
            _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
            _subscriptionKey = subscriptionKey ?? throw new ArgumentNullException(nameof(subscriptionKey));
        }

        public sealed record CreatePledgeRequest(
            string ConstituentId,
            decimal Amount,
            DateTime PledgeDate,
            string FundId,

            // Schedule (REQUIRED by the endpoint based on your 400 error)
            PledgeFrequency Frequency,
            int NumberOfInstallments,
            DateTime StartDate,

            // Optional metadata
            string? PaymentMethod = "Other",
            string? Comments = null,
            string? CampaignId = null,
            string? AppealId = null,
            string? PackageId = null,

            // Behavior flags (safe defaults)
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

        public sealed record PledgeInstallment(
            int Sequence,
            DateTime Date,
            decimal Amount
        );

        public PledgeFrequency Frequency { get; set; } = PledgeFrequency.Monthly;


        /// <summary>
        /// Create a pledge in gifts v2. This endpoint requires Schedule (per your 400).
        /// Endpoint: POST /gft-gifts/v2/gifts
        /// </summary>
        public async Task<CreatePledgeResult> CreatePledgeAsync(CreatePledgeRequest req, CancellationToken ct = default)
        {
            Validate(req);

            // Build request JSON with REQUIRED schedule.
            // NOTE: gift_date should be a DATE string (yyyy-MM-dd) rather than a full timestamp.
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

                // Pledge commitment only: record method metadata (no $ received).
                payments = new[]
                {
                    new { method = string.IsNullOrWhiteSpace(req.PaymentMethod) ? "Other" : req.PaymentMethod.Trim() }
                },

                // REQUIRED (based on your error)
                schedule = new
                {
                    frequency = ToApiFrequency(req.Frequency),
                    number_of_installments = req.NumberOfInstallments,
                    start_date = req.StartDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                }

                // IMPORTANT:
                // Do NOT send installments here unless you confirm your tenant supports it on create.
                // Some tenants want schedule-only on create (and then installments are auto-generated).
            };

            var createJson = JsonSerializer.Serialize(payload, JsonOpts);

            using var request = new HttpRequestMessage(HttpMethod.Post, "gft-gifts/v2/gifts");
            AddHeaders(request);
            request.Content = new StringContent(createJson, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

            var giftId = ExtractIdOrThrow(body);

            // Optional: verify installments exist and add them if missing.
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
                        // Re-check (best effort)
                        (installmentsPresent, _) = await TryGetInstallmentsAsync(giftId, ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // If your tenant doesn’t expose the installments list endpoint, we don't fail pledge creation.
                    // You can still add installments explicitly if needed, by calling AddInstallmentsAsync yourself.
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
        /// List pledge installments (best effort).
        /// Common endpoint shape: GET /gft-gifts/v2/gifts/{giftId}/installments
        /// </summary>
        public async Task<string> GetInstallmentsRawAsync(string giftId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(giftId)) throw new ArgumentNullException(nameof(giftId));

            using var req = new HttpRequestMessage(HttpMethod.Get, $"gft-gifts/v2/gifts/{Uri.EscapeDataString(giftId)}/installments");
            AddHeaders(req);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

            return body;
        }

        /// <summary>
        /// Add installments explicitly.
        /// Endpoint: POST /gft-gifts/v2/gifts/{giftId}/installments
        /// </summary>
        public async Task<string> AddInstallmentsAsync(string giftId, IReadOnlyList<PledgeInstallment> installments, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(giftId)) throw new ArgumentNullException(nameof(giftId));
            if (installments is null || installments.Count == 0) throw new ArgumentException("At least one installment is required.", nameof(installments));

            var payload = new
            {
                installments = installments.Select(i => new
                {
                    // Some tenants accept date-only; others accept ISO w/ Z. We send ISO Z.
                    date = ToIsoZulu(i.Date.Date),
                    amount = i.Amount,
                    sequence = i.Sequence
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"gft-gifts/v2/gifts/{Uri.EscapeDataString(giftId)}/installments");
            AddHeaders(req);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
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

            // Work in cents to guarantee exact sum.
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

        // ---------------------------
        // Internal helpers
        // ---------------------------

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

        private void AddHeaders(HttpRequestMessage req)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            // Avoid duplicates if reused
            if (req.Headers.Contains("Bb-Api-Subscription-Key"))
                req.Headers.Remove("Bb-Api-Subscription-Key");

            req.Headers.Add("Bb-Api-Subscription-Key", _subscriptionKey);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
            using var req = new HttpRequestMessage(HttpMethod.Get, $"gft-gifts/v2/gifts/{Uri.EscapeDataString(giftId)}/installments");
            AddHeaders(req);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (false, body);

            // Try to detect array length in common shapes.
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // Some APIs return { value: [...] }
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.Array)
                {
                    return (value.GetArrayLength() > 0, body);
                }

                // Others return { installments: [...] }
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("installments", out var inst) &&
                    inst.ValueKind == JsonValueKind.Array)
                {
                    return (inst.GetArrayLength() > 0, body);
                }

                // Or just an array
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
            // Turn a local date into a deterministic Z timestamp at midnight.
            // If you prefer local midnight converted to UTC, change this.
            var utc = DateTime.SpecifyKind(dateLocalDateOnly.Date, DateTimeKind.Utc);
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
    }
}
