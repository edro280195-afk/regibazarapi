namespace EntregasApi.Services;

public enum RegistrationDecision
{
    Allowed,
    /// <summary>No hay sesión: debe iniciar sesión (401).</summary>
    Unauthorized,
    /// <summary>Hay sesión pero no es administradora (403).</summary>
    Forbidden
}

/// <summary>
/// Quién puede crear usuarios con POST /api/auth/register.
///  - Base vacía: el primer usuario puede darse de alta sin sesión (arranque de una BD nueva).
///  - Con usuarios: solo una administradora con sesión iniciada. Antes cualquiera podía
///    registrarse y recibía rol Admin por defecto.
/// </summary>
public static class RegistrationPolicy
{
    public static RegistrationDecision Evaluate(bool anyUsers, bool isAuthenticated, bool isAdmin)
    {
        if (!anyUsers) return RegistrationDecision.Allowed;
        if (!isAuthenticated) return RegistrationDecision.Unauthorized;
        return isAdmin ? RegistrationDecision.Allowed : RegistrationDecision.Forbidden;
    }
}
