// ExternalServices/Fyers/FyersOrderService.cs — updated

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.ExternalServices.Fyers;

/// <summary>
/// Fyers order-placement and account-state client — the first live
/// order-placement code in the codebase.
///
/// IMPORTANT — endpoint history: the correct endpoint is
/// /api/v3/orders/sync, NOT /api/v3/orders. The latter returns a
/// Cloudflare-level 403 (not a Fyers API error) and cost significant
/// debugging time before the correct path was found directly in Fyers'
/// docsv3 sample curl. If /orders/sync ever starts failing with a raw
/// HTML/Cloudflare response again, re-check the endpoint path first
/// before assuming an auth or app-registration problem.
///
/// IMPORTANT — market hours: orders placed outside actual exchange
/// session hours (confirmed: before ~9:15 IST) are auto-rejected by the
/// exchange almost immediately and are NOT cancellable afterward
/// (CancelOrderAsync will return code -52 "Not a pending order" — this
/// is expected behavior for an already-rejected order, not a bug).
/// LiveOrderExecutor must gate on market hours before calling
/// PlaceOrderAsync, not just at signal-evaluation time.
///
/// IMPORTANT — app registration: this requires a Fyers app of Type
/// "Trading" with "Order placements" explicitly granted under
/// Permissions. A "Non-trading" app (even fully authenticated, even with
/// a valid token) will be blocked at Cloudflare's edge before ever
/// reaching Fyers' order-validation logic — confirmed via JWT decode
/// showing a valid, correctly-scoped, non-expired token still getting a
/// Cloudflare 403 against the wrong app type. AlphaStack's live app is
/// registered separately from the original AlphaStack (Non-trading) app
/// used for quotes/funds — see /etc/alphastack.env for current ClientId.
///
/// See DECISIONS.md — "Live mode architecture direction" for why this is
/// isolated from PaperOrderSimulator entirely.
/// </summary>
public class FyersOrderService : IFyersOrderService
{
    private const string BaseUrl = "https://api-t1.fyers.in/api/v3/orders/sync";
    private const string MultilegUrl = "https://api-t1.fyers.in/api/v3/multileg/orders/sync";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly FyersTokenService _tokenService;
    private readonly ILogger<FyersOrderService> _logger;

    public FyersOrderService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        FyersTokenService tokenService,
        ILogger<FyersOrderService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<FyersFundsSnapshot> GetFundsAsync(CancellationToken ct = default)
    {
        var client = BuildClient();
        var response = await client.GetAsync("https://api-t1.fyers.in/api/v3/funds", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FyersOrder] GetFunds Response {Status}: {Body}", response.StatusCode, body);

        EnsureSuccess(response, body, "GetFunds");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        EnsureOk(root, "GetFunds");

        var fundLimits = root.GetProperty("fund_limit").EnumerateArray().ToList();
        var available = FindFundLine(fundLimits, "Available Balance");
        var utilized = FindFundLine(fundLimits, "Utilized Amount");
        var total = FindFundLine(fundLimits, "Total Balance");

        return new FyersFundsSnapshot(available, utilized, total, DateTime.UtcNow);
    }

    /// <summary>
    /// Places a live order. Caller MUST verify market hours before calling —
    /// this method does not check session state itself, since it's a thin
    /// broker client, not a policy layer. See class-level remarks.
    /// </summary>
    public async Task<FyersOrderResult> PlaceOrderAsync(FyersOrderRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            symbol = request.Symbol,
            qty = request.Quantity,
            type = request.Type,
            side = request.Side,
            productType = request.ProductType,
            limitPrice = request.LimitPrice,
            stopPrice = request.StopPrice,
            validity = request.Validity,
            disclosedQty = 0,
            offlineOrder = false,
            stopLoss = 0,
            takeProfit = 0,
            orderTag = request.OrderTag,
            isSliceOrder = false
        };

        var client = BuildClient();
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8,
            "application/json");

        _logger.LogInformation("[FyersOrder] Placing order: {Payload}", JsonSerializer.Serialize(payload));
        var response = await client.PostAsync(BaseUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FyersOrder] PlaceOrder Response {Status}: {Body}", response.StatusCode, body);

        return ParseOrderResult(body, "PlaceOrder");
    }

    public async Task<FyersOrderResult> CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var client = BuildClient();
        var content = new StringContent(
            JsonSerializer.Serialize(new { id = brokerOrderId }),
            System.Text.Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Delete, BaseUrl) { Content = content };

        _logger.LogInformation("[FyersOrder] Cancelling order {Id}", brokerOrderId);
        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FyersOrder] CancelOrder Response {Status}: {Body}", response.StatusCode, body);

        return ParseOrderResult(body, "CancelOrder");
    }

    /// <summary>
    /// Parses Fyers' order response, which reuses "code"/"message"/"s" for
    /// both success (code 1101 place / 1103 cancel) and failure (negative
    /// codes: -50 validation, -52 not-pending, -99 risk-engine rejection —
    /// all confirmed against real responses, not assumed).
    /// </summary>
    private FyersOrderResult ParseOrderResult(string body, string operation)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var status = root.TryGetProperty("s", out var s) ? s.GetString() : null;
            var orderId = root.TryGetProperty("id", out var id) ? id.GetString() : null;

            var success = status?.Equals("ok", StringComparison.OrdinalIgnoreCase) == true;

            if (!success)
            {
                _logger.LogWarning("[FyersOrder] {Operation} rejected | Code={Code} Message={Message}",
                    operation, code, message);
            }

            return new FyersOrderResult(success, orderId, code, message);
        }
        catch (JsonException ex)
        {
            // Non-JSON response — very likely the Cloudflare-block pattern
            // seen during initial endpoint debugging, not a Fyers API error.
            _logger.LogError(ex, "[FyersOrder] {Operation} returned non-JSON response: {Body}", operation, body);
            return new FyersOrderResult(false, null, -1, "Non-JSON response — check endpoint/app registration");
        }
    }

    private HttpClient BuildClient()
    {
        var client = _httpClientFactory.CreateClient("Fyers");
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            $"{_configuration["Fyers:ClientId"]}:{_tokenService.AccessToken}");
        return client;
    }

    private static decimal FindFundLine(List<JsonElement> fundLimits, string title)
    {
        foreach (var line in fundLimits)
        {
            if (line.TryGetProperty("title", out var t) &&
                t.GetString()?.Equals(title, StringComparison.OrdinalIgnoreCase) == true &&
                line.TryGetProperty("equityAmount", out var amt))
            {
                return amt.GetDecimal();
            }
        }
        return 0m;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"FYERS {operation} failed ({response.StatusCode}): {body}");
    }

    private static void EnsureOk(JsonElement root, string operation)
    {
        var status = root.TryGetProperty("s", out var s) ? s.GetString() : null;
        if (status is not null && !status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"FYERS {operation} failed: {root}");
    }

   /// <summary>
    /// Fetches status for a specific order via server-side id filtering
    /// (?id=), not client-side scanning of the full day's orderbook.
    /// </summary>
    public async Task<FyersOrderStatus?> GetOrderStatusAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var client = BuildClient();
        var response = await client.GetAsync($"https://api-t1.fyers.in/api/v3/orders?id={Uri.EscapeDataString(brokerOrderId)}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FyersOrder] GetOrderStatus Response {Status}: {Body}", response.StatusCode, body);

        EnsureSuccess(response, body, "GetOrderStatus");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        EnsureOk(root, "GetOrderStatus");

        if (!root.TryGetProperty("orderBook", out var orderBook) || orderBook.GetArrayLength() == 0)
        {
            _logger.LogWarning("[FyersOrder] Order {Id} not found", brokerOrderId);
            return null;
        }

        var order = orderBook[0];
        return new FyersOrderStatus(
            BrokerOrderId: order.TryGetProperty("id", out var id) ? id.GetString()! : brokerOrderId,
            Status: order.TryGetProperty("status", out var s) ? s.GetInt32() : 0,
            FilledQty: order.TryGetProperty("filledQty", out var f) ? f.GetInt32() : 0,
            RemainingQty: order.TryGetProperty("remainingQuantity", out var r) ? r.GetInt32() : 0,
            TradedPrice: order.TryGetProperty("tradedPrice", out var tp) ? tp.GetDecimal() : 0m,
            Message: order.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
    }

    /// <summary>
    /// Places a 2 or 3-leg atomic basket order. See class remarks and
    /// FyersMultilegOrderRequest — NOT YET LIVE-TESTED.
    /// </summary>
    public async Task<FyersOrderResult> PlaceMultilegOrderAsync(FyersMultilegOrderRequest request, CancellationToken ct = default)
    {
        if (request.Legs.Count < 2 || request.Legs.Count > 3)
            throw new ArgumentException($"Multileg orders support 2 or 3 legs only, got {request.Legs.Count}.");

        var legsObject = new Dictionary<string, object>();
        for (var i = 0; i < request.Legs.Count; i++)
        {
            var leg = request.Legs[i];
            legsObject[$"leg{i + 1}"] = new
            {
                symbol = leg.Symbol,
                qty = leg.Quantity,
                side = leg.Side,
                type = 1, // Limit only — multileg docs show no market-order example
                limitPrice = leg.LimitPrice
            };
        }

        var payload = new
        {
            orderTag = request.OrderTag,
            productType = request.ProductType,
            offlineOrder = false,
            orderType = request.OrderType,
            validity = "IOC",
            legs = legsObject
        };

        var client = BuildClient();
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8,
            "application/json");

        _logger.LogInformation("[FyersOrder] Placing multileg order ({LegCount} legs): {Payload}",
            request.Legs.Count, JsonSerializer.Serialize(payload));
        var response = await client.PostAsync(MultilegUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FyersOrder] PlaceMultilegOrder Response {Status}: {Body}", response.StatusCode, body);

        return ParseOrderResult(body, "PlaceMultilegOrder");
    }

    public async Task<IReadOnlyList<FyersOrderStatus>> GetOrdersByTagAsync(string orderTag, CancellationToken ct = default)
    {
        var client = BuildClient();
        var response = await client.GetAsync($"https://api-t1.fyers.in/api/v3/orders?order_tag={Uri.EscapeDataString(orderTag)}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[FyersOrder] GetOrdersByTag Response {Status}: {Body}", response.StatusCode, body);

        EnsureSuccess(response, body, "GetOrdersByTag");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        EnsureOk(root, "GetOrdersByTag");

        if (!root.TryGetProperty("orderBook", out var orderBook))
            return [];

        return orderBook.EnumerateArray().Select(order => new FyersOrderStatus(
            BrokerOrderId: order.TryGetProperty("id", out var id) ? id.GetString()! : "",
            Status: order.TryGetProperty("status", out var s) ? s.GetInt32() : 0,
            FilledQty: order.TryGetProperty("filledQty", out var f) ? f.GetInt32() : 0,
            RemainingQty: order.TryGetProperty("remainingQuantity", out var r) ? r.GetInt32() : 0,
            TradedPrice: order.TryGetProperty("tradedPrice", out var tp) ? tp.GetDecimal() : 0m,
            Message: order.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "")).ToList();
    }
}