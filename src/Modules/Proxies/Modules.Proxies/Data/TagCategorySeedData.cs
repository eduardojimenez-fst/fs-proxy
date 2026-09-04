namespace FSH.Modules.Proxies.Data;

/// <summary>The default reference tag catalog, seeded once by <see cref="ProxiesDbInitializer"/>.</summary>
public static class TagCategorySeedData
{
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Categories { get; } =
    [
        ("country", [
            "AR", "BO", "CL", "CO", "EC", "GT", "MX", "PE", "UY",
        ]),
        ("source", [
            "Argentina - BAC",
            "Argentina - Comprar",
            "Argentina - PBAC",
            "Peru - SEACE",
            "Guatemala - GuateCompras",
            "Peru - PAC",
            "Costa Rica - SICOP",
            "Mexico - CompraNet",
            "Argentina - Ministerio Salud PBA",
            "Argentina - PAMI Central",
            "Argentina - PAMI Ugls",
            "Argentina - UAPE",
            "Argentina - Comprar Mendoza",
            "Argentina - Mendoza - Osep",
            "Argentina - Comprar - Garrahan",
            "Colombia - Secop",
            "Bolivia - Sicoes",
            "Ecuador - Sercop",
            "Uruguay - Compras Estatales",
            "Chile - Mercado Publico",
            "Chile - Cenabast",
        ]),
        ("entityType", [
            "Tender",
            "PurchaseOrder",
            "PAC",
            "RFI",
            "BigPurchase",
            "QuoteAgreement",
            "QuoteAgreementHardwareStorare",
            "QuoteAgreementTransportation",
            "Attachments",
            "Claim",
            "DirectDeal",
            "QuoteRequest",
            "QuickBid",
        ]),
        ("operationType", [
            "Attachments",
        ]),
        ("application", [
            "TaskManager", "SGL", "QB-Legacy", "QR-Legacy", "PO-Legacy", "AG-Legacy", "TAG", "POM",
        ]),
    ];
}
