using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;
using static EntregasApi.Services.OrderPaymentPolicy;

namespace EntregasApi.Tests;

public class OrderPaymentPolicyTests
{
    // ── TryGetMercadoPagoId ──

    [Theory]
    [InlineData("MP#123 | accredited", 123L)]
    [InlineData("MP#9876543210 | approved (Webhook)", 9876543210L)]
    [InlineData("MP#55 | pending_review_manual", 55L)]
    public void TryGetMercadoPagoId_ReadsTheIdFromTheNotes(string notes, long expected)
    {
        Assert.Equal(expected, TryGetMercadoPagoId(notes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pago en efectivo")]
    [InlineData("MP# | sin id")]
    [InlineData("MP#abc")]
    public void TryGetMercadoPagoId_ReturnsNull_WhenThePaymentIsNotFromMercadoPago(string? notes)
    {
        Assert.Null(TryGetMercadoPagoId(notes));
    }

    [Fact]
    public void TryGetMercadoPagoId_IsExact_SoShorterIdsNeverMatchLongerOnes()
    {
        // El webhook antes comparaba con Contains: "MP#12" coincidía con "MP#123".
        Assert.NotEqual(12L, TryGetMercadoPagoId("MP#123 | accredited"));
    }

    // ── ShouldRevertCardPayment ──

    [Theory]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    [InlineData("refunded")]
    [InlineData("charged_back")]
    public void ShouldRevertCardPayment_IsTrue_WhenTheMoneyIsNotThere(string status)
    {
        Assert.True(ShouldRevertCardPayment(status));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("in_process")]
    [InlineData("pending")]
    [InlineData("authorized")]
    [InlineData("in_mediation")]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldRevertCardPayment_IsFalse_WhenTheMoneyIsThereOrMayArrive(string? status)
    {
        Assert.False(ShouldRevertCardPayment(status));
    }

    // ── EvaluateRemoval ──

    [Theory]
    [InlineData("approved")]
    [InlineData("in_mediation")]
    public void EvaluateRemoval_Blocks_WhenMercadoPagoHasItApproved(string mpStatus)
    {
        Assert.Equal(RemovalDecision.BlockMercadoPagoApproved, EvaluateRemoval(null, mpStatus));
    }

    [Theory]
    [InlineData("in_process")]
    [InlineData("pending")]
    [InlineData("authorized")]
    public void EvaluateRemoval_AllowsButWarns_WhenMercadoPagoStillReviewsIt(string mpStatus)
    {
        Assert.Equal(RemovalDecision.AllowButMayReturn, EvaluateRemoval(null, mpStatus));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    [InlineData("refunded")]
    [InlineData("charged_back")]
    [InlineData(null)]
    public void EvaluateRemoval_Allows_WhenMercadoPagoDoesNotHaveTheMoney(string? mpStatus)
    {
        Assert.Equal(RemovalDecision.Allow, EvaluateRemoval(null, mpStatus));
    }

    [Fact]
    public void EvaluateRemoval_Allows_ManualPaymentsWithNoCashSession()
    {
        // Efectivo / transferencia registrados a mano: sin MP y sin caja.
        Assert.Equal(RemovalDecision.Allow, EvaluateRemoval(null, null));
    }

    [Fact]
    public void EvaluateRemoval_Allows_PosPaymentsWhileTheCashSessionIsOpen()
    {
        // FinalCashExpected se calcula al cerrar, así que con la caja abierta no hay descuadre.
        Assert.Equal(RemovalDecision.Allow, EvaluateRemoval(SessionStatus.Open, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("approved")]
    [InlineData("rejected")]
    public void EvaluateRemoval_Blocks_PosPaymentsOfAClosedCashSession_NoMatterWhat(string? mpStatus)
    {
        Assert.Equal(RemovalDecision.BlockCashSessionClosed, EvaluateRemoval(SessionStatus.Closed, mpStatus));
    }

    // ── BlockMessage ──

    [Theory]
    [InlineData(RemovalDecision.Allow)]
    [InlineData(RemovalDecision.AllowButMayReturn)]
    public void BlockMessage_IsNull_WhenRemovalIsAllowed(RemovalDecision decision)
    {
        Assert.Null(BlockMessage(decision));
    }

    [Fact]
    public void BlockMessage_ExplainsWhyEachBlockHappened()
    {
        Assert.Contains("caja", BlockMessage(RemovalDecision.BlockCashSessionClosed));
        var mp = BlockMessage(RemovalDecision.BlockMercadoPagoApproved);
        Assert.Contains("Mercado Pago", mp);
        Assert.Contains("reembólsalo", mp);
    }
}
