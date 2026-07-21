using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class LabelTemplateDesignValidatorTests
{
    private readonly LabelTemplateDesignValidator _validator = new();

    [Theory]
    [InlineData(LabelTemplateKind.InventoryBox, LabelPrinterProfile.NiimbotB1_50x50)]
    [InlineData(LabelTemplateKind.InventoryItem, LabelPrinterProfile.NiimbotB1_50x50)]
    [InlineData(LabelTemplateKind.OrderPackage, LabelPrinterProfile.AiyinE40_4x6)]
    public void DefaultDesign_IsPublishableForItsSupportedHardware(
        LabelTemplateKind kind,
        LabelPrinterProfile profile)
    {
        var design = LabelTemplateDesignFactory.CreateDefaultDesign(kind, profile);

        var result = _validator.Validate(design, kind, profile);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsAnInventoryBoxQrThatIsTooSmall()
    {
        var design = LabelTemplateDesignFactory.CreateDefaultDesign(
            LabelTemplateKind.InventoryBox,
            LabelPrinterProfile.NiimbotB1_50x50)
            .Replace("\"x\":29,\"y\":18,\"width\":20,\"height\":20", "\"x\":29,\"y\":18,\"width\":14,\"height\":14", StringComparison.Ordinal);

        var result = _validator.Validate(design, LabelTemplateKind.InventoryBox, LabelPrinterProfile.NiimbotB1_50x50);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("QR debe ser cuadrado", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsABoxDesignWithoutNfcBinding()
    {
        var design = LabelTemplateDesignFactory.CreateDefaultDesign(
            LabelTemplateKind.InventoryBox,
            LabelPrinterProfile.NiimbotB1_50x50)
            .Replace("box.nfcUrl", "box.invalidUrl", StringComparison.Ordinal);

        var result = _validator.Validate(design, LabelTemplateKind.InventoryBox, LabelPrinterProfile.NiimbotB1_50x50);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("box.nfcUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfilePolicy_OnlyAllowsThePurchasedHardwareForEachBusinessLabel()
    {
        Assert.True(LabelTemplateProfilePolicy.IsSupported(LabelTemplateKind.InventoryBox, LabelPrinterProfile.NiimbotB1_50x50));
        Assert.True(LabelTemplateProfilePolicy.IsSupported(LabelTemplateKind.InventoryItem, LabelPrinterProfile.NiimbotB1_50x50));
        Assert.True(LabelTemplateProfilePolicy.IsSupported(LabelTemplateKind.OrderPackage, LabelPrinterProfile.AiyinE40_4x6));
        Assert.False(LabelTemplateProfilePolicy.IsSupported(LabelTemplateKind.OrderPackage, LabelPrinterProfile.NiimbotB1_50x50));
    }
}
