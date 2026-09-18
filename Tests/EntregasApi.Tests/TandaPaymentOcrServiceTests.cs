using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class TandaPaymentOcrServiceTests
{
    [Fact]
    public void ExtractAmount_UsesTheAmountNearTheReceiptTotal()
    {
        var amount = TandaPaymentOcrService.ExtractAmount("TRANSFERENCIA\nTOTAL: $1,250.50 MXN");

        Assert.Equal(1250.50m, amount);
    }

    [Fact]
    public void ExtractDepositDate_RecognizesMexicanDateAndTime()
    {
        var depositDate = TandaPaymentOcrService.ExtractDepositDate("Fecha de operación: 17/09/2026 14:35");

        Assert.True(depositDate.HasValue);
        Assert.Equal(new DateTime(2026, 9, 17), depositDate.Value.Date);
    }
}
