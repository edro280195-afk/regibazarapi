using System.Text.Json;
using EntregasApi.Models;

namespace EntregasApi.Services;

public interface ILabelTemplateDesignValidator
{
    LabelTemplateValidationResult Validate(string designJson, LabelTemplateKind kind, LabelPrinterProfile printerProfile);
}

public sealed record LabelTemplateValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlySet<Guid> AssetIds)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Valida el contrato del diseñador en el servidor. Así una plantilla publicada
/// conserva dimensiones, QR y bindings útiles aunque se llame desde otro cliente.
/// </summary>
public sealed class LabelTemplateDesignValidator : ILabelTemplateDesignValidator
{
    private const int MaxDesignLength = 250_000;
    private const int MaxElements = 80;
    private const double Tolerance = 0.05;

    private static readonly HashSet<string> SupportedElementTypes = new(StringComparer.Ordinal)
    {
        "text", "data", "image", "qr", "barcode", "shape", "line"
    };

    public LabelTemplateValidationResult Validate(
        string designJson,
        LabelTemplateKind kind,
        LabelPrinterProfile printerProfile)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var assetIds = new HashSet<Guid>();

        if (string.IsNullOrWhiteSpace(designJson))
        {
            errors.Add("El diseño de la etiqueta es obligatorio.");
            return new LabelTemplateValidationResult(errors, warnings, assetIds);
        }

        if (designJson.Length > MaxDesignLength)
        {
            errors.Add("El diseño supera el tamaño máximo permitido.");
            return new LabelTemplateValidationResult(errors, warnings, assetIds);
        }

        try
        {
            using var document = JsonDocument.Parse(designJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("El diseño debe ser un objeto JSON.");
                return new LabelTemplateValidationResult(errors, warnings, assetIds);
            }

            ValidateSchemaVersion(root, errors);
            var (expectedWidth, expectedHeight, minimumQrSize, minimumBarcodeWidth) = GetProfileDimensions(printerProfile);
            ValidateCanvas(root, expectedWidth, expectedHeight, errors);

            if (!root.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            {
                errors.Add("El diseño debe incluir la lista de elementos.");
                return new LabelTemplateValidationResult(errors, warnings, assetIds);
            }

            if (elements.GetArrayLength() is < 1 or > MaxElements)
            {
                errors.Add($"La etiqueta debe contener entre 1 y {MaxElements} elementos.");
                return new LabelTemplateValidationResult(errors, warnings, assetIds);
            }

            var elementIds = new HashSet<string>(StringComparer.Ordinal);
            var bindings = new HashSet<string>(StringComparer.Ordinal);

            var index = 0;
            foreach (var element in elements.EnumerateArray())
            {
                index++;
                ValidateElement(
                    element,
                    index,
                    expectedWidth,
                    expectedHeight,
                    minimumQrSize,
                    minimumBarcodeWidth,
                    elementIds,
                    bindings,
                    assetIds,
                    errors,
                    warnings);
            }

            ValidateRequiredBindings(kind, bindings, errors);
        }
        catch (JsonException)
        {
            errors.Add("El diseño no contiene JSON válido.");
        }

        return new LabelTemplateValidationResult(errors, warnings, assetIds);
    }

    private static void ValidateSchemaVersion(JsonElement root, ICollection<string> errors)
    {
        if (!TryGetInt(root, "schemaVersion", out var schemaVersion) || schemaVersion != 1)
        {
            errors.Add("La versión del esquema de etiqueta no es compatible.");
        }
    }

    private static void ValidateCanvas(JsonElement root, double expectedWidth, double expectedHeight, ICollection<string> errors)
    {
        if (!root.TryGetProperty("canvas", out var canvas) || canvas.ValueKind != JsonValueKind.Object ||
            !TryGetDouble(canvas, "widthMm", out var width) || !TryGetDouble(canvas, "heightMm", out var height))
        {
            errors.Add("El lienzo debe declarar widthMm y heightMm.");
            return;
        }

        if (Math.Abs(width - expectedWidth) > Tolerance || Math.Abs(height - expectedHeight) > Tolerance)
        {
            errors.Add($"El lienzo debe medir {expectedWidth:0.##} × {expectedHeight:0.##} mm para este perfil de impresora.");
        }
    }

    private static void ValidateElement(
        JsonElement element,
        int index,
        double canvasWidth,
        double canvasHeight,
        double minimumQrSize,
        double minimumBarcodeWidth,
        ISet<string> elementIds,
        ISet<string> bindings,
        ISet<Guid> assetIds,
        ICollection<string> errors,
        ICollection<string> warnings)
    {
        var prefix = $"Elemento {index}";
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}: debe ser un objeto.");
            return;
        }

        if (!TryGetString(element, "id", out var id) || id.Length > 64 || !id.All(character => char.IsLetterOrDigit(character) || character is '-' or '_'))
        {
            errors.Add($"{prefix}: id inválido.");
        }
        else if (!elementIds.Add(id))
        {
            errors.Add($"{prefix}: el id '{id}' está repetido.");
        }

        if (!TryGetString(element, "type", out var type) || !SupportedElementTypes.Contains(type))
        {
            errors.Add($"{prefix}: tipo de elemento no permitido.");
            return;
        }

        var hasX = TryGetDouble(element, "x", out var x);
        var hasY = TryGetDouble(element, "y", out var y);
        var hasWidth = TryGetDouble(element, "width", out var width);
        var hasHeight = TryGetDouble(element, "height", out var height);
        if (!hasX || !hasY || !hasWidth || !hasHeight ||
            x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > canvasWidth + Tolerance || y + height > canvasHeight + Tolerance)
        {
            errors.Add($"{prefix}: posición o tamaño fuera del lienzo.");
        }

        if (element.TryGetProperty("rotation", out var rotationElement) &&
            (!rotationElement.TryGetDouble(out var rotation) || rotation is < -360 or > 360))
        {
            errors.Add($"{prefix}: rotación inválida.");
        }

        var isVisible = !element.TryGetProperty("visible", out var visibleElement) || visibleElement.ValueKind != JsonValueKind.False;
        if (!element.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}: faltan las propiedades del elemento.");
            return;
        }

        switch (type)
        {
            case "data":
            case "qr":
            case "barcode":
                if (!TryGetString(properties, "binding", out var binding) || binding.Length > 100 || !IsSafeBinding(binding))
                {
                    errors.Add($"{prefix}: el binding es obligatorio y no es válido.");
                }
                else if (isVisible)
                {
                    bindings.Add(binding);
                }
                break;
            case "text":
                if (!TryGetString(properties, "text", out var text) || text.Length > 1000)
                {
                    errors.Add($"{prefix}: el texto es obligatorio y debe medir máximo 1000 caracteres.");
                }
                break;
            case "image":
                if (!TryGetString(properties, "assetId", out var assetIdValue) || !Guid.TryParse(assetIdValue, out var assetId))
                {
                    errors.Add($"{prefix}: la imagen debe provenir de la biblioteca de activos.");
                }
                else
                {
                    assetIds.Add(assetId);
                }
                break;
        }

        if (type == "qr")
        {
            if (!isVisible)
            {
                errors.Add($"{prefix}: el código QR requerido no puede estar oculto.");
            }
            if (Math.Abs(width - height) > Tolerance || Math.Min(width, height) < minimumQrSize)
            {
                errors.Add($"{prefix}: el QR debe ser cuadrado y medir al menos {minimumQrSize:0.#} mm.");
            }
            if (element.TryGetProperty("rotation", out var qrRotation) && qrRotation.TryGetDouble(out var degrees) && Math.Abs(degrees) > Tolerance)
            {
                errors.Add($"{prefix}: el QR no debe rotarse para conservar su lectura.");
            }
            if (x < 1 || y < 1 || x + width > canvasWidth - 1 || y + height > canvasHeight - 1)
            {
                warnings.Add($"{prefix}: deja al menos 1 mm libre alrededor del QR para que se lea con más confianza.");
            }
        }

        if (type == "barcode")
        {
            if (width < minimumBarcodeWidth || height < 10)
            {
                errors.Add($"{prefix}: el código de barras debe medir al menos {minimumBarcodeWidth:0.#} mm de ancho y 10 mm de alto.");
            }
            if (!isVisible)
            {
                errors.Add($"{prefix}: el código de barras requerido no puede estar oculto.");
            }
        }
    }

    private static void ValidateRequiredBindings(LabelTemplateKind kind, ISet<string> bindings, ICollection<string> errors)
    {
        var requiredBindings = kind switch
        {
            LabelTemplateKind.InventoryBox => new[] { "box.code", "box.nfcUrl" },
            LabelTemplateKind.InventoryItem => new[] { "item.name", "item.scannableCode" },
            LabelTemplateKind.OrderPackage => new[] { "order.clientName", "package.number", "package.qrCodeValue" },
            _ => Array.Empty<string>()
        };

        foreach (var binding in requiredBindings)
        {
            if (!bindings.Contains(binding))
            {
                errors.Add($"La plantilla debe conservar el dato obligatorio '{binding}'.");
            }
        }
    }

    private static (double Width, double Height, double MinimumQrSize, double MinimumBarcodeWidth) GetProfileDimensions(LabelPrinterProfile profile) => profile switch
    {
        LabelPrinterProfile.NiimbotB1_50x50 => (50, 50, 20, 30),
        LabelPrinterProfile.AiyinE40_4x6 => (101.6, 152.4, 28, 45),
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    private static bool IsSafeBinding(string binding) =>
        binding.All(character => char.IsLetterOrDigit(character) || character is '.' or '_');

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               (value = property.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out value);
    }
}
