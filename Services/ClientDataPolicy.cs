using EntregasApi.Models;

namespace EntregasApi.Services;

public static class ClientDataPolicy
{
    public static string? NormalizeOptionalAddress(string? address)
    {
        var normalized = address?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? ResolveDeliveryAddress(string? clientAddress, string? alternativeAddress)
    {
        return NormalizeOptionalAddress(alternativeAddress)
            ?? NormalizeOptionalAddress(clientAddress);
    }

    public static IReadOnlyList<string> PreserveMissingData(Client target, Client source)
    {
        var preserved = new List<string>();
        var sourceAddress = NormalizeOptionalAddress(source.Address);
        var targetAddress = NormalizeOptionalAddress(target.Address);

        if (targetAddress == null && sourceAddress != null)
        {
            target.Address = sourceAddress;
            target.NormalizedAddress = TextNormalizer.NormalizeAddress(sourceAddress);
            target.Latitude = source.Latitude;
            target.Longitude = source.Longitude;
            preserved.Add("direccion");
        }
        else if (targetAddress != null
                 && sourceAddress != null
                 && TextNormalizer.NormalizeAddress(targetAddress) == TextNormalizer.NormalizeAddress(sourceAddress)
                 && (!target.Latitude.HasValue || !target.Longitude.HasValue)
                 && source.Latitude.HasValue
                 && source.Longitude.HasValue)
        {
            target.Latitude = source.Latitude;
            target.Longitude = source.Longitude;
            preserved.Add("coordenadas");
        }

        if (string.IsNullOrWhiteSpace(target.Phone) && !string.IsNullOrWhiteSpace(source.Phone))
        {
            target.Phone = source.Phone.Trim();
            target.NormalizedPhone = TextNormalizer.NormalizePhone(target.Phone);
            preserved.Add("telefono");
        }

        if (string.IsNullOrWhiteSpace(target.DeliveryInstructions)
            && !string.IsNullOrWhiteSpace(source.DeliveryInstructions))
        {
            target.DeliveryInstructions = source.DeliveryInstructions.Trim();
            preserved.Add("instrucciones");
        }

        if (string.IsNullOrWhiteSpace(target.FacebookProfileUrl)
            && !string.IsNullOrWhiteSpace(source.FacebookProfileUrl))
        {
            target.FacebookProfileUrl = source.FacebookProfileUrl.Trim();
            preserved.Add("Facebook");
        }

        return preserved;
    }
}
