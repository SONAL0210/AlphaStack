using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlphaStack.Application.Common;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Application.Features.Trading;

public record PaperTradingOptions
{
    public decimal SlippageMinPercent { get; init; } = 0.1m;
    public decimal SlippageMaxPercent { get; init; } = 0.3m;
    public int ExecutionDelayMinMs { get; init; } = 100;
    public int ExecutionDelayMaxMs { get; init; } = 500;
}

public class PaperOrderSimulator
{
    private readonly ITradeOrderRepository _orderRepo;
    private readonly IPositionRepository _positionRepo;
    private readonly ITradeAnalyticsRepository _analyticsRepo;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<PaperOrderSimulator> _logger;
    private readonly PaperTradingOptions _options;
    private readonly Random _rng = new();
    private readonly ITradeRepository _tradeRepo;

    public PaperOrderSimulator(
        ITradeOrderRepository orderRepo,
        IPositionRepository positionRepo,
        ITradeAnalyticsRepository analyticsRepo,
        IUnitOfWork uow,
        IOptions<PaperTradingOptions> options,
        ITradeRepository tradeRepo,
        ILogger<PaperOrderSimulator> logger)
    {
        _orderRepo = orderRepo;
        _positionRepo = positionRepo;
        _analyticsRepo = analyticsRepo;
        _uow = uow;
        _logger = logger;
        _tradeRepo = tradeRepo;
        _options = options.Value;
    }

    public async Task<bool> SimulateEntryFillAsync(Guid signalGroupId, CancellationToken ct = default)
    {
        bool allSuccess = true;
        var orders = await _orderRepo.GetBySignalGroupAsync(signalGroupId, ct);
        // Recovery mode: include already-filled orders too.
        // If a previous run marked orders as Filled but position creation failed,
        // we still want to recreate missing positions on the next run.
        var approvedOrders = orders
            .Where(o => o.Status == OrderStatus.Approved || o.Status == OrderStatus.Filled)
            .ToList();
        var existingPositions = await _positionRepo.GetBySignalGroupAsync(signalGroupId, ct);
        var positionedOrderKeys = existingPositions
            .Select(p => (p.InstrumentToken, p.TradingSymbol))
            .ToHashSet();

        if (!approvedOrders.Any())
        {
            _logger.LogWarning("[PaperSim] No approved orders found for group {GroupId}", signalGroupId);
            return false;
        }

        foreach (var order in approvedOrders)
        {
            try
            {
                await RetryHelper.ExecuteWithRetryAsync(async () =>
                {
                    var delay = _rng.Next(_options.ExecutionDelayMinMs, _options.ExecutionDelayMaxMs);
                    await Task.Delay(delay, ct);

                    var ltp = order.LimitPrice ?? 0;
                    var fillPrice = ApplySlippage(ltp, order.Side);

                    order.MarkFilled(fillPrice, order.Quantity);
                    await _orderRepo.UpdateAsync(order, ct);

                    if (!positionedOrderKeys.Contains((order.InstrumentToken, order.TradingSymbol)))
                    {
                        var position = Position.Open(
                            strategyExecutionId: order.StrategyExecutionId,
                            mode: order.Mode,
                            signalGroupId: order.SignalGroupId,
                            tradingSymbol: order.TradingSymbol,
                            exchange: order.Exchange,
                            instrumentToken: order.InstrumentToken,
                            side: order.Side,
                            quantity: order.FilledQuantity,
                            entryPrice: fillPrice,
                            optionType: order.OptionType,
                            strikePrice: order.StrikePrice,
                            expiryDate: order.ExpiryDate);

                        await _positionRepo.AddAsync(position, ct);

                        _logger.LogInformation(
                            "[PaperSim] Position created | Group={GroupId} | Symbol={Symbol}",
                            signalGroupId,
                            order.TradingSymbol);

                        // Persist positions immediately so analytics failures cannot roll back positions.
                        await _uow.SaveChangesAsync(ct);

                        positionedOrderKeys.Add((order.InstrumentToken, order.TradingSymbol));
                    }

                    _logger.LogInformation(
                        "[PaperSim] Filled | {Symbol} | LTP={Ltp} Fill={Fill}",
                        order.TradingSymbol,
                        ltp,
                        fillPrice);
                },
                logger: _logger,
                operationName: $"FillOrder-{order.Id}");
                
            }
            catch (Exception ex)
            {
                allSuccess = false;
                _logger.LogError(ex, "[PaperSim] FAILED | OrderId={OrderId}", order.Id);
                order.MarkFailed();
                await _orderRepo.UpdateAsync(order, ct);
            }
        }

        // trade positions loop, before analytics block:
        var tradeExists = await _tradeRepo.GetByEntrySignalGroupAsync(signalGroupId, ct);
        if (tradeExists is null && approvedOrders.Count >= 2)
        {
            var sellLeg = approvedOrders.First(o => o.Side == OrderSide.Sell);
            
            var trade = Trade.Create(
                strategyExecutionId: sellLeg.StrategyExecutionId,
                symbol:              sellLeg.TradingSymbol,
                direction:           TradeDirection.Short,  // spreads are always short volatility
                quantity:            sellLeg.FilledQuantity,
                entrySignalGroupId:  signalGroupId,
                entryClientOrderId:  sellLeg.ClientOrderId ?? signalGroupId.ToString("N"));

            trade.MarkEntryPending();
            trade.MarkEntered(
                entryPrice: (sellLeg.FilledPrice ?? 0) - (approvedOrders.First(o => o.Side == OrderSide.Buy).FilledPrice ?? 0),
                entryTime:  DateTime.UtcNow);

            await _tradeRepo.AddAsync(trade, ct);
            await _uow.SaveChangesAsync(ct);
            
            _logger.LogInformation("[PaperSim] Trade record created | GroupId={GroupId}", signalGroupId);
        }

        await _uow.SaveChangesAsync(ct);
        return allSuccess;
    }

    private decimal ApplySlippage(decimal price, OrderSide side)
    {
        var slippagePct = _options.SlippageMinPercent +
            (decimal)_rng.NextDouble() * (_options.SlippageMaxPercent - _options.SlippageMinPercent);

        var factor = slippagePct / 100m;

        return side == OrderSide.Buy
            ? price * (1 + factor)
            : price * (1 - factor);
    }
}
