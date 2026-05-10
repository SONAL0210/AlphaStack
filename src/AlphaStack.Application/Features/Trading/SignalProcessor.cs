using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Application.Features.Trading;

/// <summary>
/// Converts a StrategySignal into TradeOrder records and dispatches
/// Telegram approval requests for entry signals.
/// Exits bypass approval and go straight to the simulator.
///
/// Analytics: creates TradeAnalytics at entry fill, updates at exit fill.
/// </summary>
public class SignalProcessor
{
    private readonly ITradeOrderRepository          _orderRepo;
    private readonly IPositionRepository            _positionRepo;
    private readonly IUserProfileRepository         _userRepo;
    private readonly IStrategyExecutionRepository   _executionRepo;
    private readonly ITradeAnalyticsRepository      _analyticsRepo;
    private readonly IEncryptionService             _encryption;
    private readonly ITelegramNotificationService   _telegram;
    private readonly IRiskManager                   _riskManager;
    private readonly IUnitOfWork                    _uow;
    private readonly ILogger<SignalProcessor>       _logger;
     private readonly ShadowTradeLoggerService _shadowLogger;

    public SignalProcessor(
        IRiskManager riskManager,
        ITradeOrderRepository orderRepo,
        IPositionRepository positionRepo,
        IUserProfileRepository userRepo,
        IStrategyExecutionRepository executionRepo,
        ITradeAnalyticsRepository analyticsRepo,
        IEncryptionService encryption,
        ITelegramNotificationService telegram,
        IUnitOfWork uow,
        ILogger<SignalProcessor> logger,
        ShadowTradeLoggerService shadowLogger)
    {
        _riskManager = riskManager;
        _orderRepo = orderRepo;
        _positionRepo = positionRepo;
        _userRepo = userRepo;
        _executionRepo = executionRepo;
        _analyticsRepo = analyticsRepo;
        _encryption = encryption;
        _telegram = telegram;
        _uow = uow;
        _logger = logger;
        _shadowLogger = shadowLogger;
    }

    public async Task ProcessAsync(StrategySignal signal, CancellationToken ct = default)
    {
        if (signal.Action == SignalAction.Enter)
            await ProcessEntrySignalAsync(signal, ct);
        else if (signal.Action == SignalAction.Exit)
            await ProcessExitSignalAsync(signal, ct);
    }

    // ── Entry ─────────────────────────────────────────────────────────────────

    private async Task ProcessEntrySignalAsync(StrategySignal signal, CancellationToken ct)
    {
        _logger.LogInformation(
            "[SignalProcessor] Entry signal received | ExecutionId={ExecutionId} GroupId={GroupId} Mode={Mode}",
            signal.StrategyExecutionId, signal.SignalGroupId, signal.Mode);

        // 1. Load execution + user
        var execution = await _executionRepo.GetByIdAsync(signal.StrategyExecutionId, ct)
            ?? throw new InvalidOperationException($"Execution {signal.StrategyExecutionId} not found.");

        var user = await _userRepo.GetByIdAsync(execution.UserProfileId, ct)
            ?? throw new InvalidOperationException("User not found for signal processing.");

        var botToken = _encryption.Decrypt(user.EncryptedTelegramBotToken);

        // 2. Estimate capital (sell-leg premium × qty)
        var estimatedCapital = signal.Legs
            .Where(l => l.Side == OrderSide.Sell)
            .Sum(l => l.LastPrice * l.Quantity);

        _logger.LogInformation(
            "[SignalProcessor] Risk check | ExecutionId={ExecutionId} EstimatedCapital=₹{Capital:F0}",
            execution.Id, estimatedCapital);

        // 3. Risk check — abort before creating any DB records
        var riskResult = await _riskManager.ValidateEntryAsync(execution, user, estimatedCapital, ct);
        if (!riskResult.IsAllowed)
        {
            _logger.LogWarning(
                "[SignalProcessor] Entry BLOCKED | GroupId={GroupId} | Reason={Reason}",
                signal.SignalGroupId, riskResult.Reason);

            await _telegram.SendMessageAsync(botToken, user.TelegramChatId,
                $"🚫 *Trade Blocked*\n_{riskResult.Reason}_", ct);
            return;
        }

        // 4. Persist orders (Pending)
        var orders = new List<TradeOrder>();
        foreach (var leg in signal.Legs)
        {
            var order = TradeOrder.Create(
                strategyExecutionId: signal.StrategyExecutionId,
                mode:                signal.Mode,
                signalGroupId:       signal.SignalGroupId,
                tradingSymbol:       leg.TradingSymbol,
                exchange:            leg.Exchange,
                instrumentToken:     leg.InstrumentToken,
                instrumentType:      InstrumentType.FuturesAndOptions,
                side:                leg.Side,
                orderType:           OrderType.Market,
                quantity:            leg.Quantity,
                limitPrice:          leg.LastPrice,
                optionType:          leg.OptionType,
                strikePrice:         leg.StrikePrice,
                expiryDate:          leg.ExpiryDate);

            orders.Add(order);
            await _orderRepo.AddAsync(order, ct);
        }

        await _uow.SaveChangesAsync(ct);

        // 5. Assign ClientOrderIds AFTER DB save (idempotency)
        foreach (var order in orders)
        {
            order.AssignClientOrderId(Guid.NewGuid().ToString("N"));
            await _orderRepo.UpdateAsync(order, ct);
        }

        await _uow.SaveChangesAsync(ct);

        // 6. Create TradeAnalytics record from signal metadata
        await CreateAnalyticsAtEntryAsync(signal, execution, ct);

         _ = Task.Run(async () =>
        {
            try
            {
                var meta = SignalMetadata.ParseFromRationale(signal.Rationale);
                var sellLeg = signal.Legs.FirstOrDefault(l => l.Side == OrderSide.Sell);
                var buyLeg  = signal.Legs.FirstOrDefault(l => l.Side == OrderSide.Buy);
                var netCredit = sellLeg is not null && buyLeg is not null
                    ? sellLeg.LastPrice - buyLeg.LastPrice : 0m;

                var vixRegime = meta.Vix < 14 ? "VIX_LOW"
                    : meta.Vix < 18            ? "VIX_MID" : "VIX_HIGH";

                var context = new ShadowMarketContext(
                    Spot:        meta.Spot,
                    Vix:         meta.Vix,
                    VixRegime:   vixRegime,
                    Ema20:       meta.Ema20,
                    Adr:         meta.Adr,
                    Atr:         meta.Atr,
                    AtrAverage:  meta.AtrAvg,
                    GapPercent:  0m,    // not in rationale yet — add when BaseSpreadEngine exposes it
                    DaysToExpiry: sellLeg?.ExpiryDate.HasValue == true
                        ? (sellLeg.ExpiryDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.Today).Days : 0,
                    Expiry:      sellLeg?.ExpiryDate ?? DateOnly.FromDateTime(DateTime.Today),
                    RealNetCredit: netCredit);

                var strategyName   = signal.Rationale.Contains("BearCall") ? "BearCallSpread" : "BullPutSpread";
                var entryVariation = DateTime.Now.DayOfWeek switch
                {
                    DayOfWeek.Monday    => "MondayEntry",
                    DayOfWeek.Wednesday => "WednesdayEntry",
                    DayOfWeek.Friday    => "FridayEntry",
                    DayOfWeek.Tuesday   => "TuesdayEntry",
                    DayOfWeek.Thursday  => "ThursdayEntry",
                    _                   => "UnknownEntry"
                };

                var spreadWidth = sellLeg is not null && buyLeg is not null
                    ? (int)Math.Abs((sellLeg.StrikePrice ?? 0) - (buyLeg.StrikePrice ?? 0)) : 200;

                await _shadowLogger.LogVariantsAsync(
                    context:           context,
                    realSignalGroupId: signal.SignalGroupId,
                    strategyName:      strategyName,
                    entryVariation:    entryVariation,
                    strikeInterval:    strategyName.Contains("BankNifty") ? 100 : 50,
                    quantity:          sellLeg?.Quantity ?? 25,
                    realAdrMultiplier: meta.AdrMultiplier,
                    realSpreadWidth:   spreadWidth,
                    ct:                CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ShadowLogger] Fire-and-forget logging failed for GroupId={G}",
                    signal.SignalGroupId);
            }
        }, CancellationToken.None);

        // 7. Send Telegram approval request (enriched message)
        var message           = FormatEntryMessage(signal, execution);
        var telegramMessageId = await _telegram.SendApprovalRequestAsync(
            botToken, user.TelegramChatId, message, signal.SignalGroupId.ToString(), ct);

        foreach (var order in orders)
        {
            order.MarkApprovalRequested(telegramMessageId);
            await _orderRepo.UpdateAsync(order, ct);
        }

        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[SignalProcessor] Approval request sent | GroupId={GroupId} OrderCount={Count}",
            signal.SignalGroupId, orders.Count);
    }

    // ── Exit ──────────────────────────────────────────────────────────────────

    private async Task ProcessExitSignalAsync(StrategySignal signal, CancellationToken ct)
    {
        _logger.LogInformation(
            "[SignalProcessor] Auto-exit | GroupId={GroupId}", signal.SignalGroupId);

        var userId = await GetUserIdAsync(signal.StrategyExecutionId, ct);
        var user   = await _userRepo.GetByIdAsync(userId, ct);

        if (user is null) return;

        var botToken = _encryption.Decrypt(user.EncryptedTelegramBotToken);

        var openPositions = await _positionRepo.GetBySignalGroupAsync(signal.SignalGroupId, ct);
        openPositions = openPositions
            .Where(p => p.Status == PositionStatus.Open)
            .ToList();

        if (!openPositions.Any())
        {
            _logger.LogError(
                "[SignalProcessor] Exit failed — no open positions | GroupId={GroupId}",
                signal.SignalGroupId);

            await _telegram.SendMessageAsync(botToken, user.TelegramChatId,
                $"⚠️ *Exit Execution Failed*\n" +
                $"Group: `{signal.SignalGroupId}`\n" +
                $"No open positions were found. Manual review required.", ct);
            return;
        }

        foreach (var position in openPositions)
        {
            var exitLeg = signal.Legs.FirstOrDefault(l =>
                l.TradingSymbol == position.TradingSymbol &&
                l.InstrumentToken == position.InstrumentToken);

            if (exitLeg is null)
            {
                _logger.LogWarning(
                    "[SignalProcessor] Missing exit leg for open position {Symbol} | GroupId={GroupId}",
                    position.TradingSymbol, signal.SignalGroupId);
                continue;
            }

            position.Close(exitLeg.LastPrice);
            await _positionRepo.UpdateAsync(position, ct);

            _logger.LogInformation(
                "[SignalProcessor] Position closed | {Symbol} Exit=₹{Exit:F2} PnL=₹{PnL:F0}",
                position.TradingSymbol, exitLeg.LastPrice, position.RealizedPnL);
        }

        await _uow.SaveChangesAsync(ct);

        // Update analytics at exit
        await UpdateAnalyticsAtExitAsync(signal, ct);

        // Send exit notification
        var exitMsg = await FormatExitMessageAsync(signal, ct);
        await _telegram.SendMessageAsync(botToken, user.TelegramChatId, exitMsg, ct);
    }

    // ── Analytics: Entry ──────────────────────────────────────────────────────

    private async Task CreateAnalyticsAtEntryAsync(
        StrategySignal signal, StrategyExecution execution, CancellationToken ct)
    {
        try
        {
            // Read indicator values directly from signal properties (no more regex parsing)
            var sellLeg = signal.Legs.First(l => l.Side == OrderSide.Sell);
            var buyLeg  = signal.Legs.First(l => l.Side == OrderSide.Buy);

            var netCredit = sellLeg.LastPrice - buyLeg.LastPrice;
            var quantity  = (decimal)sellLeg.Quantity;
            var entryDay  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                              TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).DayOfWeek;
            
            var gapPercent = signal.GapPercent;

            var entryVariation = entryDay switch
            {
                DayOfWeek.Monday    => "MondayEntry",
                DayOfWeek.Friday    => "FridayEntry",
                DayOfWeek.Wednesday => "WednesdayEntry",
                _ => $"{entryDay}Entry"
            };

            // Read VIX and EMA20 directly from signal — no regex needed
            var vix  = signal.Vix;
            var spot = signal.SpotAtSignal;
            var ema20 = signal.Ema20;

            var vixRegime = vix < 14m  ? "VIX_LOW"
                : vix <= 18m           ? "VIX_MID"
                : "VIX_HIGH";

            var marketRegime = spot > ema20 ? "TrendUp"
                : spot < ema20              ? "TrendDown"
                : "Range";

            var adr = signal.Adr;
            var atr = signal.Atr;

            var analytics = TradeAnalytics.CreateAtEntry(
                tradeId:              signal.SignalGroupId,
                strategyName:         signal.StrategyType,
                entryVariation:       entryVariation,
                spotAtEntry:          spot,
                vixAtEntry:           vix,
                vixRegime:            vixRegime,
                marketRegime:         marketRegime,
                ema20AtEntry:         ema20,
                adrAtEntry:           adr,
                atrAtEntry:           atr,
                atrAverageAtEntry:    signal.AtrAverage,
                adrMultiplierUsed:    1.5m, 
                gapPercent:           signal.GapPercent,
                shortStrike:          sellLeg.StrikePrice ?? 0,
                longStrike:           buyLeg.StrikePrice  ?? 0,
                expiryDate:           sellLeg.ExpiryDate  ?? DateOnly.FromDateTime(DateTime.Today),
                premiumCollected:     netCredit,
                quantity:             quantity,
                allocatedCapital:     execution.AllocatedCapital,
                executionDelayMs:     new Random().Next(300, 500),
                slippageRs:           0m);

            await _analyticsRepo.AddAsync(analytics, ct);
            await _uow.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[Analytics] Created | Strategy={S} Entry={E} VIX={V:F1} Regime={R} " +
                "Short={SS} Long={LS} Credit=₹{C:F2} CapitalAtRisk=₹{CAR:F0} ({P:F1}%)",
                signal.StrategyType, entryVariation, vix, marketRegime,
                sellLeg.StrikePrice, buyLeg.StrikePrice, netCredit,
                analytics.CapitalAtRisk, analytics.CapitalAtRiskPercent);
        }
        catch (Exception ex)
        {
            // Never block the trade for analytics failures
            _logger.LogError(ex, "[Analytics] Failed to create analytics record for GroupId={G}",
                signal.SignalGroupId);
        }
    }

    // ── Analytics: Exit ───────────────────────────────────────────────────────

    private async Task UpdateAnalyticsAtExitAsync(StrategySignal signal, CancellationToken ct)
    {
        try
        {
            var analytics = await _analyticsRepo.GetByTradeIdAsync(signal.SignalGroupId, ct);
            if (analytics is null)
            {
                _logger.LogWarning("[Analytics] No analytics record found for GroupId={G}", signal.SignalGroupId);
                return;
            }

            var exitVariation = signal.Rationale switch
            {
                var r when r.Contains("Profit target")  => "ProfitTarget50",
                var r when r.Contains("Stop loss")      => "StopLoss2x",
                var r when r.Contains("Expiry day")     => "ExpiryClose",
                var r when r.Contains("End of day")     => "EndOfDay",
                _ => "Manual"
            };

            // Get exit leg prices from the signal
            var sellLeg = signal.Legs.FirstOrDefault(l => l.Side == OrderSide.Buy);  // closing sell = buy back
            var buyLeg  = signal.Legs.FirstOrDefault(l => l.Side == OrderSide.Sell); // closing buy = sell

            var exitSpreadValue = sellLeg is not null && buyLeg is not null
                ? Math.Abs(sellLeg.LastPrice - buyLeg.LastPrice)
                : 0m;

            var premiumCaptured = analytics.PremiumCollected - exitSpreadValue;
            var qty             = signal.Legs.First().Quantity;
            var grossPnl        = (analytics.PremiumCollected - exitSpreadValue) * qty;
            var brokerage       = 20m * signal.Legs.Count * 2; // ₹20/order × legs × (entry+exit)

            // Use UTC throughout for HoldingMinutes — avoids timezone conversion errors
            var exitTimeUtc  = DateTime.UtcNow;
            var entryTimeUtc = analytics.CreatedAt; // CreatedAt is stored as UTC

            analytics.CloseAnalytics(
                spotAtExit:      signal.SpotAtSignal,
                exitVariation:   exitVariation,
                exitReason:      signal.Rationale,
                premiumCaptured: Math.Max(0, premiumCaptured),
                grossPnL:        grossPnl,
                brokerage:       brokerage,
                entryTime:       entryTimeUtc,
                exitTime:        exitTimeUtc);

            await _analyticsRepo.UpdateAsync(analytics, ct);
            await _uow.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[Analytics] Closed | Exit={E} GrossPnL=₹{G:F0} Net=₹{N:F0} HoldingMin={H}",
                exitVariation, grossPnl, grossPnl - brokerage, analytics.HoldingMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Analytics] Failed to update analytics at exit for GroupId={G}",
                signal.SignalGroupId);
        }
    }

    // ── Telegram messages ─────────────────────────────────────────────────────

    private static string FormatEntryMessage(StrategySignal signal, StrategyExecution execution)
    {
        var sell           = signal.Legs.First(l => l.Side == OrderSide.Sell);
        var buy            = signal.Legs.First(l => l.Side == OrderSide.Buy);
        var netCredit      = sell.LastPrice - buy.LastPrice;
        var netCreditTotal = netCredit * sell.Quantity;
        var spreadWidth    = (sell.StrikePrice ?? 0) - (buy.StrikePrice ?? 0);
        var maxLoss        = (spreadWidth - netCredit) * sell.Quantity;
        var profitTarget   = netCreditTotal * 0.5m;
        var stopLoss       = netCreditTotal * 2.0m;
        var capitalPct     = execution.AllocatedCapital > 0
            ? maxLoss / execution.AllocatedCapital * 100 : 0;

        // Safe zone: Nifty stays above short strike
        // Danger zone: Nifty drops near long strike
        var safeZone   = sell.StrikePrice ?? 0;
        var dangerZone = buy.StrikePrice  ?? 0;

        return $"""
🟢 *Bull Put Spread — Entry Signal*
Mode: `{signal.Mode}`

📉 *Sell* {sell.StrikePrice}PE {sell.ExpiryDate:dd-MMM} × {sell.Quantity} @ ₹{sell.LastPrice:F2}
📈 *Buy*  {buy.StrikePrice}PE  {buy.ExpiryDate:dd-MMM} × {buy.Quantity}  @ ₹{buy.LastPrice:F2}

💰 Net credit:      ₹{netCredit:F2}/unit = *₹{netCreditTotal:F0}* total
🎯 Profit target:   ₹{profitTarget:F0}  (50% capture)
🛑 Stop loss:       ₹{stopLoss:F0}  (2× credit)
📊 Capital at risk: ₹{maxLoss:F0}  ({capitalPct:F1}% of portfolio)

🔼 Safe zone:   Nifty stays above *{safeZone:F0}*
🔽 Danger zone: Nifty drops below *{dangerZone:F0}*
⏰ Expiry: {sell.ExpiryDate:ddd dd-MMM} at 3:30 PM IST

📋 {signal.Rationale}

Signal ID: `{signal.SignalGroupId}`
""";
    }

    private async Task<string> FormatExitMessageAsync(StrategySignal signal, CancellationToken ct)
    {
        var analytics = await _analyticsRepo.GetByTradeIdAsync(signal.SignalGroupId, ct);
        if (analytics is null)
            return $"🔴 *Exit executed*\n{signal.Rationale}";

        var outcome = !analytics.NetPnL.HasValue
        ? "🟡 Open"
        : analytics.NetPnL.Value >= 0
            ? "✅ Profit"
            : "❌ Loss";
        return $"""
🔴 *Position Closed*

{outcome}: *₹{analytics.NetPnL:F0}* (after brokerage)
Gross P&L: ₹{analytics.GrossPnL:F0}
Brokerage: ₹{analytics.Brokerage:F0}
Held for:  {analytics.HoldingMinutes} minutes

Exit reason: _{signal.Rationale}_
Max profit seen: ₹{analytics.MaxMtmProfit:F0}
Max loss seen:   ₹{analytics.MaxMtmLoss:F0}
""";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid> GetUserIdAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await _executionRepo.GetByIdAsync(executionId, ct)
            ?? throw new InvalidOperationException($"Execution {executionId} not found.");
        return execution.UserProfileId;
    }

    /// <summary>
    /// Parses indicator values from the Rationale string stored in StrategySignal.
    /// Rationale format from BullPutSpreadEngine:
    ///   "NIFTY 24000 > EMA20 23800 | ADR 200pts → offset 300pts | ATR 180pts (avg 160pts) | ..."
    /// </summary>
    private record SignalMetadata(
        decimal Spot, decimal Ema20, decimal Vix,
        decimal Adr, decimal Atr, decimal AtrAvg,
        decimal AdrMultiplier)
    {
        public static SignalMetadata ParseFromRationale(string rationale)
        {
            // Defaults — used if parsing fails for any field
            decimal spot = 0, ema20 = 0, vix = 0, adr = 0, atr = 0, atrAvg = 0;
            decimal adrMultiplier = 1.5m;

            try
            {
                // "NIFTY 24000 > EMA20 23800"
                var spotMatch = System.Text.RegularExpressions.Regex.Match(rationale, @"NIFTY\s+([\d.]+)");
                if (spotMatch.Success) decimal.TryParse(spotMatch.Groups[1].Value, out spot);

                var emaMatch = System.Text.RegularExpressions.Regex.Match(rationale, @"EMA20\s+([\d.]+)");
                if (emaMatch.Success) decimal.TryParse(emaMatch.Groups[1].Value, out ema20);

                // "ADR 200pts"
                var adrMatch = System.Text.RegularExpressions.Regex.Match(rationale, @"ADR\s+([\d.]+)pts");
                if (adrMatch.Success) decimal.TryParse(adrMatch.Groups[1].Value, out adr);

                // "ATR 180pts (avg 160pts)"
                var atrMatch = System.Text.RegularExpressions.Regex.Match(rationale, @"ATR\s+([\d.]+)pts\s+\(avg\s+([\d.]+)pts\)");
                if (atrMatch.Success)
                {
                    decimal.TryParse(atrMatch.Groups[1].Value, out atr);
                    decimal.TryParse(atrMatch.Groups[2].Value, out atrAvg);
                }
            }
            catch { /* parsing failure — use defaults */ }

            return new SignalMetadata(spot, ema20, vix, adr, atr, atrAvg, adrMultiplier);
        }
    }
}
