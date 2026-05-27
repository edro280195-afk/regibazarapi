using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EntregasApi.Services;

/// <summary>
/// Normaliza texto para fuzzy matching: lowercase, sin diacríticos, sin signos.
/// Usado por el resolver de identidad de clientas y por el matcher de keywords del live.
/// </summary>
public static class TextNormalizer
{
    private static readonly Regex NonAlphanumRegex = new(@"[^a-z0-9 ]", RegexOptions.Compiled);
    private static readonly Regex MultipleSpacesRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonDigitsRegex = new(@"[^0-9]", RegexOptions.Compiled);

    /// <summary>
    /// Normaliza un nombre o texto general: lowercase + sin diacríticos + sin signos + espacios colapsados.
    /// Ej: "María Antonieta" → "maria antonieta", "Lupe López" → "lupe lopez".
    /// </summary>
    public static string NormalizeName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var withoutDiacritics = RemoveDiacritics(input.Trim().ToLowerInvariant());
        var alphanum = NonAlphanumRegex.Replace(withoutDiacritics, " ");
        var collapsed = MultipleSpacesRegex.Replace(alphanum, " ").Trim();
        return collapsed;
    }

    /// <summary>
    /// Normaliza un teléfono dejando solo dígitos. Devuelve null si quedan menos de 7 dígitos.
    /// </summary>
    public static string? NormalizePhone(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = NonDigitsRegex.Replace(input, "");
        return digits.Length >= 7 ? digits : null;
    }

    /// <summary>
    /// Normaliza una dirección: misma lógica que el nombre. Útil para trigram matching.
    /// Devuelve null si queda vacía.
    /// </summary>
    public static string? NormalizeAddress(string? input)
    {
        var n = NormalizeName(input);
        return string.IsNullOrEmpty(n) ? null : n;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
