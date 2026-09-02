using FSH.Framework.Core.Domain;
using FSH.Modules.Billing.Contracts;
using FSH.Modules.Billing.Domain;
using Shouldly;
using Xunit;

namespace Billing.Tests.Domain;

public sealed class WalletTests
{
    [Fact]
    public void Create_starts_active_with_zero_balance()
    {
        var w = Wallet.Create("tenant-a", "USD");
        w.TenantId.ShouldBe("tenant-a");
        w.Currency.ShouldBe("USD");
        w.Balance.Amount.ShouldBe(0m);
        w.Status.ShouldBe(WalletStatus.Active);
        w.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Credit_increases_balance_and_returns_ledger_row()
    {
        var w = Wallet.Create("tenant-a", "USD");
        var tx = w.Credit(new Money(50m, "USD"), WalletTransactionKind.Topup, "Top-up", "req-1");
        w.Balance.Amount.ShouldBe(50m);
        tx.Amount.Amount.ShouldBe(50m);
        tx.WalletId.ShouldBe(w.Id);
        tx.TenantId.ShouldBe("tenant-a");
        tx.ReferenceId.ShouldBe("req-1");
    }

    [Fact]
    public void Credit_rejects_non_positive_amount()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => Wallet.Create("t", "USD").Credit(new Money(0m, "USD"), WalletTransactionKind.Topup, "x", null));

    [Fact]
    public void Credit_rejects_currency_mismatch()
    {
        var w = Wallet.Create("tenant-a", "USD");
        Should.Throw<InvalidOperationException>(
            () => w.Credit(new Money(50m, "EUR"), WalletTransactionKind.Topup, "Top-up", null));
        // The rejected credit must leave the aggregate untouched — no phantom
        // ledger row, balance still zero.
        w.Transactions.ShouldBeEmpty();
        w.Balance.Amount.ShouldBe(0m);
    }

    [Fact]
    public void Debit_rejects_currency_mismatch()
    {
        var w = Wallet.Create("tenant-a", "USD");
        w.Credit(new Money(50m, "USD"), WalletTransactionKind.Topup, "Top-up", null);
        Should.Throw<InvalidOperationException>(
            () => w.Debit(new Money(10m, "EUR"), WalletTransactionKind.MessageCharge, "msg", null));
        // Only the opening credit survives; the mismatched debit adds nothing.
        w.Transactions.Count.ShouldBe(1);
        w.Balance.Amount.ShouldBe(50m);
    }

    [Fact]
    public void Debit_beyond_balance_throws()
    {
        var w = Wallet.Create("tenant-a", "USD");
        w.Credit(new Money(10m, "USD"), WalletTransactionKind.Topup, "Top-up", null);
        Should.Throw<InvalidOperationException>(
            () => w.Debit(new Money(25m, "USD"), WalletTransactionKind.MessageCharge, "msg", null));
    }
}
