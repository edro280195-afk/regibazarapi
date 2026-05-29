using System.Text.RegularExpressions;

namespace EntregasApi.Services;

/// <summary>
/// Valida y normaliza referencias de Facebook (URL de perfil, link de m.me,
/// username suelto o ID numérico). Es el espejo en backend de la utilidad
/// messenger.util.ts del frontend, usado en la importación masiva de Facebooks.
/// </summary>
public static class FacebookLinkHelper
{
    private static readonly string[] ReservedHandles =
        { "profile.php", "people", "pages", "groups", "marketplace", "watch", "gaming", "events" };

    private static readonly Regex MmeRegex = new(@"(?:https?://)?(?:www\.)?m\.me/([^/?#\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MessengerRegex = new(@"messenger\.com/t/([^/?#\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IdParamRegex = new(@"[?&]id=(\d+)", RegexOptions.Compiled);
    private static readonly Regex PeopleRegex = new(@"/people/[^/]+/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FbRegex = new(@"(?:https?://)?(?:www\.|m\.|web\.)?(?:facebook|fb)\.com/([^/?#\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NumericRegex = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9.]+$", RegexOptions.Compiled);

    /// <summary>
    /// Devuelve true si la cadena parece una referencia válida de Facebook que
    /// podríamos convertir en un chat de Messenger. No garantiza que el perfil
    /// exista, solo que tiene una forma utilizable.
    /// </summary>
    public static bool LooksLikeFacebookRef(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var raw = input.Trim();

        if (MmeRegex.IsMatch(raw)) return true;
        if (MessengerRegex.IsMatch(raw)) return true;
        if (IdParamRegex.IsMatch(raw)) return true;
        if (PeopleRegex.IsMatch(raw)) return true;

        var fb = FbRegex.Match(raw);
        if (fb.Success && !ReservedHandles.Contains(fb.Groups[1].Value.ToLowerInvariant())) return true;

        if (NumericRegex.IsMatch(raw)) return true;
        if (UsernameRegex.IsMatch(raw)) return true;

        return false;
    }
}
