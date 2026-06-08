using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class ClientDataPolicyTests
{
    [Fact]
    public void NormalizeOptionalAddress_ReturnsNullForBlankValues()
    {
        Assert.Null(ClientDataPolicy.NormalizeOptionalAddress(null));
        Assert.Null(ClientDataPolicy.NormalizeOptionalAddress("   "));
        Assert.Equal("Calle Peru 123", ClientDataPolicy.NormalizeOptionalAddress(" Calle Peru 123 "));
    }

    [Fact]
    public void ResolveDeliveryAddress_FallsBackToClientAddressWhenAlternativeIsBlank()
    {
        var result = ClientDataPolicy.ResolveDeliveryAddress("Calle Mina 45", " ");

        Assert.Equal("Calle Mina 45", result);
    }

    [Fact]
    public void PreserveMissingData_CopiesAddressAndCoordinatesToTarget()
    {
        var target = CreateClient("Perfil antiguo");
        var source = CreateClient("Perfil nuevo");
        source.Address = "Calle Venezuela 200";
        source.NormalizedAddress = TextNormalizer.NormalizeAddress(source.Address);
        source.Latitude = 27.49;
        source.Longitude = -99.51;

        var preserved = ClientDataPolicy.PreserveMissingData(target, source);

        Assert.Equal(source.Address, target.Address);
        Assert.Equal(source.NormalizedAddress, target.NormalizedAddress);
        Assert.Equal(source.Latitude, target.Latitude);
        Assert.Equal(source.Longitude, target.Longitude);
        Assert.Contains("direccion", preserved);
    }

    [Fact]
    public void PreserveMissingData_DoesNotOverwriteDifferentTargetAddress()
    {
        var target = CreateClient("Perfil conservado");
        target.Address = "Calle Principal 10";
        target.NormalizedAddress = TextNormalizer.NormalizeAddress(target.Address);

        var source = CreateClient("Perfil eliminado");
        source.Address = "Calle Secundaria 20";
        source.Latitude = 27.5;
        source.Longitude = -99.5;

        var preserved = ClientDataPolicy.PreserveMissingData(target, source);

        Assert.Equal("Calle Principal 10", target.Address);
        Assert.Null(target.Latitude);
        Assert.Null(target.Longitude);
        Assert.DoesNotContain("direccion", preserved);
    }

    private static Client CreateClient(string name)
    {
        return new Client
        {
            Name = name,
            NormalizedName = TextNormalizer.NormalizeName(name)
        };
    }
}
