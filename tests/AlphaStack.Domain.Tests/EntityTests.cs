using System;
using Xunit;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Domain.Tests;

public class EntityTests
{
    [Fact]
    public void TradeOrder_Approve_FromPending_ShouldWork()
    {
        var order = TradeOrder.Create(
            Guid.NewGuid(),
            ExecutionMode.Paper,
            Guid.NewGuid(),
            "TEST",
            "NSE",
            1,
            InstrumentType.Equity,
            OrderSide.Buy,
            OrderType.Market,
            10
        );

        order.Approve();

        Assert.Equal(OrderStatus.Approved, order.Status);
    }

    [Fact]
    public void TradeOrder_Reject_FromPending_ShouldWork()
    {
        var order = TradeOrder.Create(
            Guid.NewGuid(),
            ExecutionMode.Paper,
            Guid.NewGuid(),
            "TEST",
            "NSE",
            1,
            InstrumentType.Equity,
            OrderSide.Buy,
            OrderType.Market,
            10
        );

        order.Reject();

        Assert.Equal(OrderStatus.Rejected, order.Status);
    }

    [Fact]
    public void TradeOrder_CannotApprove_AfterReject()
    {
        var order = TradeOrder.Create(
            Guid.NewGuid(),
            ExecutionMode.Paper,
            Guid.NewGuid(),
            "TEST",
            "NSE",
            1,
            InstrumentType.Equity,
            OrderSide.Buy,
            OrderType.Market,
            10
        );

        order.Reject();

        Assert.Throws<InvalidOperationException>(() => order.Approve());
    }

    [Fact]
    public void TradeOrder_MarkFilled_ShouldUpdateState()
    {
        var order = TradeOrder.Create(
            Guid.NewGuid(),
            ExecutionMode.Paper,
            Guid.NewGuid(),
            "TEST",
            "NSE",
            1,
            InstrumentType.Equity,
            OrderSide.Buy,
            OrderType.Market,
            10
        );

        order.Approve();
        order.MarkFilled(100, 10);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(100, order.FilledPrice);
        Assert.Equal(10, order.FilledQuantity);
    }
}