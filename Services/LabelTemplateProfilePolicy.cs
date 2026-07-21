using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>Evita crear combinaciones que no caben físicamente en el equipo comprado.</summary>
public static class LabelTemplateProfilePolicy
{
    public static bool IsSupported(LabelTemplateKind kind, LabelPrinterProfile profile) => (kind, profile) switch
    {
        (LabelTemplateKind.InventoryBox, LabelPrinterProfile.NiimbotB1_50x50) => true,
        (LabelTemplateKind.InventoryItem, LabelPrinterProfile.NiimbotB1_50x50) => true,
        (LabelTemplateKind.OrderPackage, LabelPrinterProfile.AiyinE40_4x6) => true,
        _ => false
    };

    public static string GetUnsupportedMessage(LabelTemplateKind kind) => kind switch
    {
        LabelTemplateKind.InventoryBox => "Las etiquetas de caja se diseñan para la NIIMBOT B1 de 50 × 50 mm.",
        LabelTemplateKind.InventoryItem => "Las etiquetas de artículo se diseñan para la NIIMBOT B1 de 50 × 50 mm.",
        LabelTemplateKind.OrderPackage => "Las etiquetas de bolsa se diseñan para la AIYIN E40 Pro en 4 × 6 pulgadas.",
        _ => "La combinación de etiqueta e impresora no está soportada."
    };
}
