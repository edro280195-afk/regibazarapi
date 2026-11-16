using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class RegistrationPolicyTests
{
    [Theory]
    [InlineData(false, false, false)] // base vacía, sin sesión: el primer usuario puede darse de alta
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void EmptyDatabase_AllowsTheFirstUser(bool authenticated, bool admin, bool _)
    {
        Assert.Equal(RegistrationDecision.Allowed, RegistrationPolicy.Evaluate(false, authenticated, admin));
    }

    [Fact]
    public void WithUsers_AnonymousIsRejectedWith401()
    {
        Assert.Equal(RegistrationDecision.Unauthorized, RegistrationPolicy.Evaluate(true, false, false));
    }

    [Fact]
    public void WithUsers_NonAdminIsRejectedWith403()
    {
        // Choferes, escáner y bodega tienen sesión válida pero no pueden crear usuarios.
        Assert.Equal(RegistrationDecision.Forbidden, RegistrationPolicy.Evaluate(true, true, false));
    }

    [Fact]
    public void WithUsers_AdminIsAllowed()
    {
        Assert.Equal(RegistrationDecision.Allowed, RegistrationPolicy.Evaluate(true, true, true));
    }

    [Fact]
    public void WithUsers_AdminFlagWithoutSessionIsNotEnough()
    {
        // Defensa en profundidad: sin autenticación nunca se permite, aunque el flag viniera en true.
        Assert.Equal(RegistrationDecision.Unauthorized, RegistrationPolicy.Evaluate(true, false, true));
    }
}
