using System.Xml.Linq;

namespace VoltixQbAgent.QuickBooks;

/// <summary>
/// Entity pulls: builds the qbXML query, parses the response into row
/// dictionaries shaped for Voltix's /api/qb-agent/upsert endpoints, and
/// filters by watermark agent-side.
///
/// Deliberately no qbXML iterators or server-side date filters yet — one
/// query returns the whole result set (the pattern the proven reference app
/// uses); the watermark filter runs here. Fine for SMB-sized files; iterator
/// chunking is a later optimisation.
/// </summary>
public static class QbPuller
{
    public sealed record ParseResult(List<Dictionary<string, object?>> Rows, DateTimeOffset? MaxModified);

    public static string BuildQueryXml(QbSession session, string entity) => entity switch
    {
        "customers" => session.RunRequest(ms => ms.AppendCustomerQueryRq()),
        "vendors" => session.RunRequest(ms => ms.AppendVendorQueryRq()),
        "accounts" => session.RunRequest(ms => ms.AppendAccountQueryRq()),
        "items" => session.RunRequest(ms => ms.AppendItemQueryRq()),
        _ => throw new QbAgentException($"Unknown pull entity: {entity}"),
    };

    public static ParseResult Parse(string entity, string xml)
    {
        var doc = XDocument.Parse(xml);
        return entity switch
        {
            "customers" => ParsePartners(doc, "CustomerRet", "BillAddress"),
            "vendors" => ParsePartners(doc, "VendorRet", "VendorAddress"),
            "accounts" => ParseAccounts(doc),
            "items" => ParseItems(doc),
            _ => throw new QbAgentException($"Unknown pull entity: {entity}"),
        };
    }

    private static ParseResult ParsePartners(XDocument doc, string retName, string addressName)
    {
        var rows = new List<Dictionary<string, object?>>();
        DateTimeOffset? max = null;
        foreach (var ret in doc.Descendants(retName))
        {
            var modified = ParseTime(ret.Element("TimeModified")?.Value);
            Track(ref max, modified);
            var addr = ret.Element(addressName);
            var addrLines = new[] { "Addr1", "Addr2", "Addr3", "Addr4", "Addr5" }
                .Select(a => addr?.Element(a)?.Value?.Trim())
                .Where(v => !string.IsNullOrEmpty(v));
            rows.Add(new Dictionary<string, object?>
            {
                ["list_id"] = ret.Element("ListID")?.Value,
                ["edit_sequence"] = ret.Element("EditSequence")?.Value,
                ["name"] = ret.Element("Name")?.Value,
                ["company_name"] = ret.Element("CompanyName")?.Value,
                ["phone"] = ret.Element("Phone")?.Value,
                ["alt_phone"] = ret.Element("AltPhone")?.Value,
                ["email"] = ret.Element("Email")?.Value,
                ["address"] = string.Join("\n", addrLines),
                ["city"] = addr?.Element("City")?.Value,
                ["state"] = addr?.Element("State")?.Value,
                ["postal_code"] = addr?.Element("PostalCode")?.Value,
                ["country"] = addr?.Element("Country")?.Value,
                ["currency"] = ret.Element("CurrencyRef")?.Element("FullName")?.Value,
                ["is_active"] = ret.Element("IsActive")?.Value != "false",
                ["_modified"] = modified?.ToString("o"),
            });
        }
        return new ParseResult(rows, max);
    }

    private static ParseResult ParseAccounts(XDocument doc)
    {
        var rows = new List<Dictionary<string, object?>>();
        DateTimeOffset? max = null;
        foreach (var ret in doc.Descendants("AccountRet"))
        {
            var modified = ParseTime(ret.Element("TimeModified")?.Value);
            Track(ref max, modified);
            rows.Add(new Dictionary<string, object?>
            {
                ["list_id"] = ret.Element("ListID")?.Value,
                ["edit_sequence"] = ret.Element("EditSequence")?.Value,
                ["name"] = ret.Element("Name")?.Value,
                ["full_name"] = ret.Element("FullName")?.Value,
                ["account_number"] = ret.Element("AccountNumber")?.Value,
                ["account_type"] = ret.Element("AccountType")?.Value,
                ["parent_full_name"] = ret.Element("ParentRef")?.Element("FullName")?.Value,
                ["is_active"] = ret.Element("IsActive")?.Value != "false",
                ["_modified"] = modified?.ToString("o"),
            });
        }
        return new ParseResult(rows, max);
    }

    private static readonly (string RetName, string ItemType)[] ItemRets =
    {
        ("ItemInventoryRet", "inventory"),
        ("ItemNonInventoryRet", "noninventory"),
        ("ItemServiceRet", "service"),
        ("ItemOtherChargeRet", "othercharge"),
    };

    private static ParseResult ParseItems(XDocument doc)
    {
        var rows = new List<Dictionary<string, object?>>();
        DateTimeOffset? max = null;
        foreach (var (retName, itemType) in ItemRets)
        {
            foreach (var ret in doc.Descendants(retName))
            {
                var modified = ParseTime(ret.Element("TimeModified")?.Value);
                Track(ref max, modified);

                // Pricing lives directly on inventory items; the other types
                // wrap it in ORSalesPurchase (SalesOrPurchase | SalesAndPurchase).
                var sp = ret.Element("SalesOrPurchase");
                var sap = ret.Element("SalesAndPurchase");
                var price = Num(ret.Element("SalesPrice")?.Value)
                            ?? Num(sap?.Element("SalesPrice")?.Value)
                            ?? Num(sp?.Element("Price")?.Value);
                var cost = Num(ret.Element("PurchaseCost")?.Value)
                           ?? Num(sap?.Element("PurchaseCost")?.Value);
                var desc = ret.Element("SalesDesc")?.Value
                           ?? sap?.Element("SalesDesc")?.Value
                           ?? sp?.Element("Desc")?.Value;

                rows.Add(new Dictionary<string, object?>
                {
                    ["list_id"] = ret.Element("ListID")?.Value,
                    ["edit_sequence"] = ret.Element("EditSequence")?.Value,
                    ["name"] = ret.Element("Name")?.Value,
                    ["full_name"] = ret.Element("FullName")?.Value,
                    ["description"] = desc,
                    ["barcode"] = ret.Element("BarCode")?.Element("BarCodeValue")?.Value,
                    ["item_type"] = itemType,
                    ["sales_price"] = price,
                    ["purchase_cost"] = cost,
                    ["quantity_on_hand"] = Num(ret.Element("QuantityOnHand")?.Value),
                    ["is_active"] = ret.Element("IsActive")?.Value != "false",
                    ["_modified"] = modified?.ToString("o"),
                });
            }
        }
        return new ParseResult(rows, max);
    }

    /// <summary>Watermark filter (agent-side). Inclusive boundary — upserts
    /// are idempotent, so refetching the edge row is harmless.</summary>
    public static List<Dictionary<string, object?>> FilterByWatermark(
        List<Dictionary<string, object?>> rows, DateTimeOffset? watermark)
    {
        if (watermark is null) return rows;
        return rows.Where(r =>
        {
            var m = r.TryGetValue("_modified", out var v) && v is string sVal ? ParseTime(sVal) : null;
            return m is null || m >= watermark;
        }).ToList();
    }

    private static void Track(ref DateTimeOffset? max, DateTimeOffset? value)
    {
        if (value.HasValue && (!max.HasValue || value > max)) max = value;
    }

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var dto) ? dto : null;

    private static double? Num(string? value) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}
