using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntregasApi.Tests;

public class ColoniaClassifierTests
{
    private readonly ColoniaClassifierService _classifier;

    public ColoniaClassifierTests()
    {
        var config = new ConfigurationBuilder().Build();
        _classifier = new ColoniaClassifierService(null!, config);
    }

    [Fact]
    public void ClassifyAddress_NullOrEmpty_ReturnsPickup()
    {
        var matchNull = _classifier.ClassifyAddress(null);
        var matchEmpty = _classifier.ClassifyAddress("   ");

        Assert.True(matchNull.IsPickup);
        Assert.Equal(ColoniaClassifierService.PickupName, matchNull.Name);
        Assert.True(matchEmpty.IsPickup);
    }

    [Fact]
    public void ClassifyAddress_ExplicitPickupKeywords_ReturnsPickup()
    {
        var match1 = _classifier.ClassifyAddress("Recoge en tienda");
        var match2 = _classifier.ClassifyAddress("Pickup matriz");
        var match3 = _classifier.ClassifyAddress("Pasa por el pedido al domicilio de regibazar");

        Assert.True(match1.IsPickup);
        Assert.True(match2.IsPickup);
        Assert.True(match3.IsPickup);
    }

    [Fact]
    public void ClassifyAddress_PickUpOrderType_ReturnsPickupEvenWithAddress()
    {
        var match = _classifier.ClassifyAddress("Calle Obregon 123", orderType: OrderType.PickUp);

        Assert.True(match.IsPickup);
        Assert.Equal(ColoniaClassifierService.PickupName, match.Name);
    }

    [Fact]
    public void ClassifyAddress_TextMatch_IdentifiesColonia()
    {
        var madero = _classifier.ClassifyAddress("Maclovio Herrera 1520, Col. Madero");
        var viveros = _classifier.ClassifyAddress("Calle Washington 400 Col Viveros");
        var jardin = _classifier.ClassifyAddress("Paseo Colon 304, Col. Jardín");
        var fresnos = _classifier.ClassifyAddress("Revolución 202, Fracc. Los Fresnos");

        Assert.False(madero.IsPickup);
        Assert.Equal("Col. Madero", madero.Name);
        Assert.Equal("Col. Viveros", viveros.Name);
        Assert.Equal("Col. Jardín", jardin.Name);
        Assert.Equal("Fracc. Los Fresnos", fresnos.Name);
    }

    [Fact]
    public void ClassifyAddress_CoordinateProximity_IdentifiesColoniaWhenTextOmitsIt()
    {
        // Coordenadas muy cercanas al centro de Col. Madero (27.4880, -99.5100)
        var match = _classifier.ClassifyAddress("Calle Peru 123", latitude: 27.4882, longitude: -99.5098);

        Assert.False(match.IsPickup);
        Assert.Equal("Col. Madero", match.Name);
    }

    [Fact]
    public void ClassifyAddress_UnrecognizedAddressWithoutCoords_ReturnsOtrasColonias()
    {
        var match = _classifier.ClassifyAddress("Calle SinNombre 9999");

        Assert.False(match.IsPickup);
        Assert.Equal("Otras Colonias (NLD)", match.Name);
    }
}
