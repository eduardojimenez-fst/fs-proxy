using System;
using System.Collections.Generic;

namespace FSH.Proxy.Client;

/// <summary>
/// The tags seeded in the service's reference catalog, as constants.
/// </summary>
/// <remarks>
/// These are plain <c>const string</c> on purpose. Tags are free-form on the server, so a custom tag
/// must stay a first-class citizen — <c>GetProxies(ProxyTags.Country.Chile, "experimento:foo")</c>
/// with no ceremony. Any closed type (an enum, a wrapper struct) would demote custom tags to
/// <c>ProxyTag.Custom("…")</c>, which is exactly what this design rejects.
///
/// Hand-maintained, because the SDK targets netstandard2.0 and cannot reference the server's
/// TagCategorySeedData. ProxyTagsTests.Every_Seeded_Catalog_Value_Should_Have_A_Constant is what
/// keeps them honest.
/// </remarks>
#pragma warning disable CA1034 // Nested types intentional: Country/Source/EntityType/OperationType/Application group the catalog the way the server's tag categories do.
public static class ProxyTags
{
    public static class Country
    {
        public const string Argentina = "country:ar";
        public const string Bolivia = "country:bo";
        public const string Chile = "country:cl";
        public const string Colombia = "country:co";
        public const string Ecuador = "country:ec";
        public const string Guatemala = "country:gt";
        public const string Mexico = "country:mx";
        public const string Peru = "country:pe";
        public const string Uruguay = "country:uy";
    }

    public static class EntityType
    {
        public const string Tender = "entitytype:tender";
        public const string PurchaseOrder = "entitytype:purchaseorder";
        public const string Pac = "entitytype:pac";
        public const string Rfi = "entitytype:rfi";
        public const string BigPurchase = "entitytype:bigpurchase";
        public const string QuoteAgreement = "entitytype:quoteagreement";
        public const string QuoteAgreementHardwareStorage = "entitytype:quoteagreementhardwarestorage";
        public const string QuoteAgreementTransportation = "entitytype:quoteagreementtransportation";
        public const string Claim = "entitytype:claim";
        public const string DirectDeal = "entitytype:directdeal";
        public const string QuoteRequest = "entitytype:quoterequest";
        public const string QuickBid = "entitytype:quickbid";
    }

    public static class OperationType
    {
        public const string Attachments = "operationtype:attachments";
    }

    public static class Source
    {
        public const string ArgentinaBac = "source:argentina - bac";
        public const string ArgentinaComprar = "source:argentina - comprar";
        public const string ArgentinaPbac = "source:argentina - pbac";
        public const string PeruSeace = "source:peru - seace";
        public const string GuatemalaGuateCompras = "source:guatemala - guatecompras";
        public const string PeruPac = "source:peru - pac";
        public const string CostaRicaSicop = "source:costa rica - sicop";
        public const string MexicoCompraNet = "source:mexico - compranet";
        public const string ArgentinaMinisterioSaludPba = "source:argentina - ministerio salud pba";
        public const string ArgentinaPamiCentral = "source:argentina - pami central";
        public const string ArgentinaPamiUgls = "source:argentina - pami ugls";
        public const string ArgentinaUape = "source:argentina - uape";
        public const string ArgentinaComprarMendoza = "source:argentina - comprar mendoza";
        public const string ArgentinaMendozaOsep = "source:argentina - mendoza - osep";
        public const string ArgentinaComprarGarrahan = "source:argentina - comprar - garrahan";
        public const string ColombiaSecop = "source:colombia - secop";
        public const string BoliviaSicoes = "source:bolivia - sicoes";
        public const string EcuadorSercop = "source:ecuador - sercop";
        public const string UruguayComprasEstatales = "source:uruguay - compras estatales";
        public const string ChileMercadoPublico = "source:chile - mercado publico";
        public const string ChileCenabast = "source:chile - cenabast";
    }

    public static class Application
    {
        public const string TaskManager = "application:taskmanager";
        public const string Sgl = "application:sgl";
        public const string QbLegacy = "application:qb-legacy";
        public const string QrLegacy = "application:qr-legacy";
        public const string PoLegacy = "application:po-legacy";
        public const string AgLegacy = "application:ag-legacy";
        public const string Tag = "application:tag";
        public const string Pom = "application:pom";
    }

    /// <summary>Every constant above, for the drift test and for anyone who wants to enumerate.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Country.Argentina, Country.Bolivia, Country.Chile, Country.Colombia, Country.Ecuador,
        Country.Guatemala, Country.Mexico, Country.Peru, Country.Uruguay,

        EntityType.Tender, EntityType.PurchaseOrder, EntityType.Pac, EntityType.Rfi,
        EntityType.BigPurchase, EntityType.QuoteAgreement, EntityType.QuoteAgreementHardwareStorage,
        EntityType.QuoteAgreementTransportation, EntityType.Claim, EntityType.DirectDeal,
        EntityType.QuoteRequest, EntityType.QuickBid,

        OperationType.Attachments,

        Source.ArgentinaBac, Source.ArgentinaComprar, Source.ArgentinaPbac, Source.PeruSeace,
        Source.GuatemalaGuateCompras, Source.PeruPac, Source.CostaRicaSicop, Source.MexicoCompraNet,
        Source.ArgentinaMinisterioSaludPba, Source.ArgentinaPamiCentral, Source.ArgentinaPamiUgls,
        Source.ArgentinaUape, Source.ArgentinaComprarMendoza, Source.ArgentinaMendozaOsep,
        Source.ArgentinaComprarGarrahan, Source.ColombiaSecop, Source.BoliviaSicoes,
        Source.EcuadorSercop, Source.UruguayComprasEstatales, Source.ChileMercadoPublico,
        Source.ChileCenabast,

        Application.TaskManager, Application.Sgl, Application.QbLegacy, Application.QrLegacy,
        Application.PoLegacy, Application.AgLegacy, Application.Tag, Application.Pom,
    };

    /// <summary>Composes a tag from a category and a value, normalized the way the server will.</summary>
    public static string Of(string category, string value)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Category is required.", nameof(category));
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", nameof(value));

        return Normalize(category.Trim() + ":" + value.Trim());
    }

    /// <summary>
    /// Mirrors the server's <c>Tag.Normalize</c> exactly: trim, then lowercase with the invariant
    /// culture. Applied to every tag the client sends, so callers may pass either form.
    /// </summary>
    public static string Normalize(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("Tag is required.", nameof(tag));

        // CA1308 wants ToUpperInvariant for normalization, but the server's Tag.Normalize lowercases
        // — tags are stored and matched lowercase, so mirroring it exactly is the whole point.
#pragma warning disable CA1308
        return tag.Trim().ToLowerInvariant();
#pragma warning restore CA1308
    }
}
#pragma warning restore CA1034
