using EntregasApi.Models;
using System.Text.Json;

namespace EntregasApi.Services;

/// <summary>
/// Diseños iniciales deliberadamente sobrios y seguros. El editor los convierte en
/// una plantilla de marca, pero desde el primer guardado ya cumplen las reglas de
/// lectura y el tamaño físico del hardware seleccionado.
/// </summary>
public static class LabelTemplateDesignFactory
{
    public static string CreateDefaultDesign(LabelTemplateKind kind, LabelPrinterProfile profile)
    {
        return (kind, profile) switch
        {
            (LabelTemplateKind.InventoryBox, LabelPrinterProfile.NiimbotB1_50x50) => Box50x50,
            (LabelTemplateKind.InventoryItem, LabelPrinterProfile.NiimbotB1_50x50) => Item50x50,
            (LabelTemplateKind.OrderPackage, LabelPrinterProfile.AiyinE40_4x6) => Package4x6,
            _ => CreateNeutralDesign(profile)
        };
    }

    private static string CreateNeutralDesign(LabelPrinterProfile profile)
    {
        var (width, height) = profile switch
        {
            LabelPrinterProfile.NiimbotB1_50x50 => (50, 50),
            LabelPrinterProfile.AiyinE40_4x6 => (101.6, 152.4),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            canvas = new { widthMm = width, heightMm = height, background = "#FFFFFF" },
            elements = new[]
            {
                new
                {
                    id = "brand",
                    type = "text",
                    x = 4,
                    y = 4,
                    width = width - 8,
                    height = 10,
                    rotation = 0,
                    visible = true,
                    zIndex = 1,
                    properties = new { text = "REGI BAZAR", fontSize = 18, fontWeight = 700, align = "center" }
                }
            }
        });
    }

    private const string Box50x50 = """
        {"schemaVersion":1,"canvas":{"widthMm":50,"heightMm":50,"background":"#FFFFFF"},"elements":[
          {"id":"brand","type":"text","x":3,"y":3,"width":44,"height":5,"rotation":0,"visible":true,"zIndex":1,"properties":{"text":"REGI BAZAR","fontSize":15,"fontWeight":700,"align":"center","letterSpacing":1}},
          {"id":"box-code","type":"data","x":3,"y":10,"width":26,"height":7,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"box.code","fontSize":19,"fontWeight":800,"align":"left"}},
          {"id":"box-name","type":"data","x":3,"y":18,"width":26,"height":12,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"box.name","fontSize":11,"fontWeight":700,"align":"left","wrap":true}},
          {"id":"box-location","type":"data","x":3,"y":32,"width":26,"height":5,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"box.location","fontSize":8,"fontWeight":500,"align":"left"}},
          {"id":"box-items","type":"data","x":3,"y":39,"width":26,"height":5,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"box.totalUnits","fontSize":8,"fontWeight":600,"align":"left","prefix":"Unidades: "}},
          {"id":"box-qr","type":"qr","x":29,"y":18,"width":20,"height":20,"rotation":0,"visible":true,"zIndex":3,"properties":{"binding":"box.nfcUrl","errorCorrection":"M"}},
          {"id":"box-scan","type":"text","x":29,"y":40,"width":20,"height":4,"rotation":0,"visible":true,"zIndex":2,"properties":{"text":"ACERCA EL CELULAR","fontSize":6,"fontWeight":700,"align":"center"}}
        ]}
        """;

    private const string Item50x50 = """
        {"schemaVersion":1,"canvas":{"widthMm":50,"heightMm":50,"background":"#FFFFFF"},"elements":[
          {"id":"brand","type":"text","x":3,"y":3,"width":44,"height":5,"rotation":0,"visible":true,"zIndex":1,"properties":{"text":"REGI BAZAR","fontSize":15,"fontWeight":700,"align":"center","letterSpacing":1}},
          {"id":"item-name","type":"data","x":3,"y":10,"width":44,"height":11,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"item.name","fontSize":15,"fontWeight":800,"align":"left","wrap":true}},
          {"id":"item-variant","type":"data","x":3,"y":22,"width":44,"height":5,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"item.variant","fontSize":9,"fontWeight":500,"align":"left"}},
          {"id":"item-barcode","type":"barcode","x":3,"y":29,"width":44,"height":12,"rotation":0,"visible":true,"zIndex":3,"properties":{"binding":"item.scannableCode","format":"CODE128","displayValue":false}},
          {"id":"item-code","type":"data","x":3,"y":42,"width":44,"height":4,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"item.scannableCode","fontSize":7,"fontWeight":600,"align":"center"}},
          {"id":"item-box","type":"data","x":3,"y":46,"width":44,"height":3,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"item.boxCode","fontSize":6,"fontWeight":500,"align":"center","prefix":"Caja "}}
        ]}
        """;

    private const string Package4x6 = """
        {"schemaVersion":1,"canvas":{"widthMm":101.6,"heightMm":152.4,"background":"#FFFFFF"},"elements":[
          {"id":"brand","type":"text","x":6,"y":6,"width":89.6,"height":9,"rotation":0,"visible":true,"zIndex":1,"properties":{"text":"REGI BAZAR","fontSize":26,"fontWeight":800,"align":"center","letterSpacing":1}},
          {"id":"brand-subtitle","type":"text","x":6,"y":16,"width":89.6,"height":5,"rotation":0,"visible":true,"zIndex":1,"properties":{"text":"TODO PARA TU HOGAR","fontSize":10,"fontWeight":600,"align":"center","letterSpacing":1}},
          {"id":"rule","type":"line","x":6,"y":24,"width":89.6,"height":0.5,"rotation":0,"visible":true,"zIndex":1,"properties":{"strokeWidth":0.5}},
          {"id":"client-kicker","type":"text","x":6,"y":29,"width":58,"height":4,"rotation":0,"visible":true,"zIndex":1,"properties":{"text":"ENTREGAR A","fontSize":9,"fontWeight":700,"align":"left","letterSpacing":1}},
          {"id":"client-name","type":"data","x":6,"y":34,"width":58,"height":11,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"order.clientName","fontSize":21,"fontWeight":800,"align":"left","wrap":true}},
          {"id":"client-phone","type":"data","x":6,"y":47,"width":58,"height":5,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"order.phone","fontSize":10,"fontWeight":600,"align":"left","prefix":"Tel. "}},
          {"id":"address","type":"data","x":6,"y":55,"width":58,"height":25,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"order.address","fontSize":12,"fontWeight":600,"align":"left","wrap":true}},
          {"id":"package-kicker","type":"text","x":69,"y":29,"width":26.6,"height":4,"rotation":0,"visible":true,"zIndex":1,"properties":{"text":"BOLSA","fontSize":9,"fontWeight":700,"align":"center","letterSpacing":1}},
          {"id":"package-number","type":"data","x":69,"y":34,"width":26.6,"height":14,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"package.number","fontSize":38,"fontWeight":800,"align":"center","prefix":"#"}},
          {"id":"package-total","type":"data","x":69,"y":49,"width":26.6,"height":5,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"package.total","fontSize":10,"fontWeight":600,"align":"center","suffix":" BOLSAS"}},
          {"id":"package-qr","type":"qr","x":67,"y":58,"width":29,"height":29,"rotation":0,"visible":true,"zIndex":3,"properties":{"binding":"package.qrCodeValue","errorCorrection":"M"}},
          {"id":"package-scan-code","type":"data","x":67,"y":89,"width":29,"height":5,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"package.qrCodeValue","fontSize":6,"fontWeight":600,"align":"center"}},
          {"id":"items-kicker","type":"text","x":6,"y":88,"width":56,"height":4,"rotation":0,"visible":true,"zIndex":1,"properties":{"text":"CONTENIDO DEL PEDIDO","fontSize":9,"fontWeight":700,"align":"left","letterSpacing":1}},
          {"id":"items-summary","type":"data","x":6,"y":94,"width":56,"height":32,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"order.itemSummary","fontSize":10,"fontWeight":500,"align":"left","wrap":true}},
          {"id":"delivery-note","type":"data","x":6,"y":128,"width":89.6,"height":10,"rotation":0,"visible":true,"zIndex":2,"properties":{"binding":"order.deliveryInstructions","fontSize":8,"fontWeight":600,"align":"left","wrap":true,"prefix":"Nota: "}},
          {"id":"footer","type":"text","x":6,"y":142,"width":89.6,"height":4,"rotation":0,"visible":true,"zIndex":1,"properties":{"text":"ESCANEA AL CARGAR Y ENTREGAR · GRACIAS POR ELEGIRNOS","fontSize":7,"fontWeight":600,"align":"center"}}
        ]}
        """;
}
