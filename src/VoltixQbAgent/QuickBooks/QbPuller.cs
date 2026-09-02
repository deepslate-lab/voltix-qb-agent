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

    /// <summary>Rows fetched so far + error info when a chunk failed midway —
    /// the caller uploads what it has (idempotent) and reports the failure
    /// without advancing the watermark.</summary>
    public sealed record ChunkedResult(List<Dictionary<string, object?>> Rows, DateTimeOffset? MaxModified, string? Error);

    private const int ChunkSize = 100;
    private const int ItContinue = 0; // ENIteratorType
    private const int ItStart = 1;

    private static dynamic AppendQuery(dynamic msgSet, string entity) => entity switch
    {
        "customers" => msgSet.AppendCustomerQueryRq(),
        "vendors" => msgSet.AppendVendorQueryRq(),
        "accounts" => msgSet.AppendAccountQueryRq(),
        "items" => msgSet.AppendItemQueryRq(),
        _ => throw new QbAgentException($"Unknown pull entity: {entity}"),
    };

    private static void SetMaxReturned(dynamic query, string entity, int max)
    {
        switch (entity)
        {
            case "customers": query.ORCustomerListQuery.CustomerListFilter.MaxReturned.SetValue(max); break;
            case "vendors": query.ORVendorListQuery.VendorListFilter.MaxReturned.SetValue(max); break;
            case "accounts": query.ORAccountListQuery.AccountListFilter.MaxReturned.SetValue(max); break;
            case "items": query.ORListQueryWithOwnerIDAndClass.ListWithClassFilter.MaxReturned.SetValue(max); break;
        }
    }

    public static string BuildQueryXml(QbSession session, string entity) =>
        session.RunRequest(ms => AppendQuery(ms, entity));

    /// <summary>Iterator/attribute info from a chunk's *QueryRs element.</summary>
    private static (string? IteratorId, int Remaining) ReadIteratorAttrs(string xml)
    {
        var doc = XDocument.Parse(xml);
        var rs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("QueryRs"));
        var id = rs?.Attribute("iteratorID")?.Value;
        var remaining = int.TryParse(rs?.Attribute("iteratorRemainingCount")?.Value, out var n) ? n : 0;
        return (string.IsNullOrEmpty(id) ? null : id, remaining);
    }

    /// <summary>
    /// Pull an entity in iterator chunks inside ONE session (iterators are
    /// session-scoped). Small responses avoid the giant-response encoding
    /// failures (UTFDataFormatException/SAXParseException) that corrupt
    /// characters in company data cause, and when a chunk still fails, only
    /// that chunk dies — with a position hint — instead of the whole pull.
    ///
    /// Safety degrade: if the first chunk comes back full with NO iteratorID
    /// (iterator unsupported/misbehaving), re-runs as one unchunked query so
    /// chunking can never silently truncate the data set.
    /// </summary>
    public static ChunkedResult PullChunked(QbSession session, string entity, Action<string> log)
    {
        var all = new List<Dictionary<string, object?>>();
        DateTimeOffset? max = null;
        string? iteratorId = null;
        var chunkIndex = 0;

        while (true)
        {
            chunkIndex++;
            string xml;
            try
            {
                var currentIteratorId = iteratorId;
                xml = session.RunRequest(ms =>
                {
                    dynamic q = AppendQuery(ms, entity);
                    if (currentIteratorId is null)
                    {
                        q.iterator.SetValue(ItStart);
                    }
                    else
                    {
                        q.iterator.SetValue(ItContinue);
                        q.iteratorID.SetValue(currentIteratorId);
                    }
                    SetMaxReturned(q, entity, ChunkSize);
                });
            }
            catch (Exception ex)
            {
                var message = $"chunk {chunkIndex} (after {all.Count} records) failed: {(ex is QbAgentException qex ? qex.Message : ex.Message)}";
                log($"Pull {entity}: {message}");
                return new ChunkedResult(all, max, message);
            }

            var parsed = Parse(entity, xml);
            all.AddRange(parsed.Rows);
            if (parsed.MaxModified.HasValue && (!max.HasValue || parsed.MaxModified > max)) max = parsed.MaxModified;

            var (nextId, remaining) = ReadIteratorAttrs(xml);

            if (iteratorId is null && nextId is null)
            {
                // No iterator support detected. If we clearly got a truncated
                // first page, redo as a single unchunked query — never trust
                // MaxReturned without an iterator to continue from.
                if (parsed.Rows.Count >= ChunkSize)
                {
                    log($"Pull {entity}: iterator not supported here — falling back to one full query.");
                    var fullXml = BuildQueryXml(session, entity);
                    var full = Parse(entity, fullXml);
                    return new ChunkedResult(full.Rows, full.MaxModified, null);
                }
                return new ChunkedResult(all, max, null);
            }

            if (nextId is null || remaining <= 0)
            {
                return new ChunkedResult(all, max, null);
            }
            iteratorId = nextId;
        }
    }

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
