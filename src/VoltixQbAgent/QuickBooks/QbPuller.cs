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

    // ENIteratorType numeric values are undocumented for late binding and we
    // guessed wrong once (QB treated 1 as "Continue" — status 3150 asked for
    // the missing iteratorID). Empirically: 1 = Continue; Start is probed at
    // runtime from the remaining candidates and cached for the process.
    private const int ItContinue = 1;
    private static readonly int[] ItStartCandidates = { 2, 0 };
    private static int? _itStartValue;

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

    /// <summary>QB writes request outcomes as ATTRIBUTES on the *QueryRs
    /// element — a rejected request is an "empty" response with an Error
    /// status, not an exception. Ignoring this is how a pull can silently
    /// report 0 rows.</summary>
    private static (string Severity, string Code, string Message) ReadRsStatus(string xml)
    {
        var doc = XDocument.Parse(xml);
        var rs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("QueryRs"));
        return (
            rs?.Attribute("statusSeverity")?.Value ?? "Info",
            rs?.Attribute("statusCode")?.Value ?? "0",
            rs?.Attribute("statusMessage")?.Value ?? ""
        );
    }

    /// <summary>One plain unchunked query with status checking. Never throws —
    /// a giant-response serializer crash (the UTFDataFormatException family)
    /// comes back as an error result instead.</summary>
    private static ChunkedResult PullFullSafe(QbSession session, string entity)
    {
        try
        {
            var xml = BuildQueryXml(session, entity);
            var status = ReadRsStatus(xml);
            if (status.Severity == "Error")
            {
                return new ChunkedResult(new List<Dictionary<string, object?>>(), null,
                    $"QuickBooks rejected the query (status {status.Code}): {status.Message}");
            }
            var parsed = Parse(entity, xml);
            return new ChunkedResult(parsed.Rows, parsed.MaxModified, null);
        }
        catch (Exception ex)
        {
            return new ChunkedResult(new List<Dictionary<string, object?>>(), null,
                $"full query failed: {(ex is QbAgentException qex ? qex.Message : ex.Message)}");
        }
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
    /// <summary>
    /// Pull with a shrinking-chunk retry ladder: 100 → 10 → 1 records per
    /// chunk. Big chunks are fast; when a corrupt record (invalid byte in
    /// company data) crashes QB's serializer, smaller chunks isolate it, and
    /// at chunk size 1 the failure index identifies THE record — which a
    /// names-only probe then tries to name so the user can fix it in QB.
    /// </summary>
    public static ChunkedResult PullResilient(QbSession session, string entity, Action<string> log)
    {
        ChunkedResult? last = null;
        foreach (var size in new[] { 100, 10, 1 })
        {
            last = PullChunked(session, entity, log, size);
            if (last.Error is null) return last;
            if (size != 1) log($"Pull {entity}: retrying with chunk size {(size == 100 ? 10 : 1)} to isolate the failing record…");
        }

        // Failed even one-by-one: last.Rows.Count records succeeded, so the
        // culprit is record #(count+1) in QB's default sort order. Try to
        // name it with a names-only query (tiny fields usually dodge the bad
        // byte — unless the name itself carries it).
        var index = last!.Rows.Count;
        List<string>? names = null;
        string culprit;
        try
        {
            names = FetchNamesOnly(session, entity);
            culprit = index < names.Count
                ? $"\"{names[index]}\" (record {index + 1})"
                : $"record {index + 1}";
        }
        catch
        {
            culprit = $"record {index + 1} (the corrupt byte may be in its name — QuickBooks could not even list names)";
        }
        log($"Pull {entity}: corrupt record identified — {culprit}.");

        // One corrupt record must not hold the rest of the list hostage:
        // iterators can't skip, but a by-name query fetches exactly one
        // record — so pull everything after the culprit individually,
        // skipping any further corrupt ones the same way.
        if (names != null && index < names.Count && SupportsByNameFetch(entity))
        {
            var rows = new List<Dictionary<string, object?>>(last.Rows);
            var max = last.MaxModified;
            var corrupt = new List<string> { names[index] };
            var total = names.Count - index - 1;
            log($"Pull {entity}: skipping the corrupt record and recovering the remaining {total} by name…");
            for (var i = index + 1; i < names.Count; i++)
            {
                var one = FetchOneByName(session, entity, names[i]);
                if (one.Error != null)
                {
                    corrupt.Add(names[i]);
                    log($"Pull {entity}: \"{names[i]}\" (record {i + 1}) is also corrupt — skipped.");
                    continue;
                }
                rows.AddRange(one.Rows);
                if (one.MaxModified.HasValue && (!max.HasValue || one.MaxModified > max)) max = one.MaxModified;
                if ((i - index) % 200 == 0) log($"Pull {entity}: by-name recovery {i - index}/{total}…");
            }
            log($"Pull {entity}: by-name recovery finished — {rows.Count - last.Rows.Count} recovered, {corrupt.Count} corrupt skipped.");
            var corruptDesc = corrupt.Count <= 10
                ? string.Join(", ", corrupt.Select(n => $"\"{n}\""))
                : string.Join(", ", corrupt.Take(10).Select(n => $"\"{n}\"")) + $" and {corrupt.Count - 10} more";
            return new ChunkedResult(rows, max,
                $"all records synced except {corrupt.Count} corrupt one{(corrupt.Count == 1 ? "" : "s")}: {corruptDesc}. " +
                "Open each in QuickBooks, retype any pasted/special characters (name, addresses, notes, contacts), save, and sync again.");
        }

        return last with
        {
            Error = $"{last.Error} — the corrupt record is {culprit} in QuickBooks' default sort order. " +
                    "Open it in QuickBooks, retype any pasted/special characters (names, addresses, notes), save, and sync again.",
        };
    }

    private static bool SupportsByNameFetch(string entity) =>
        entity is "customers" or "vendors" or "items";

    /// <summary>Fetch exactly one record by FullName. Never throws — a
    /// serializer crash on this record's data comes back as an error result.</summary>
    private static ChunkedResult FetchOneByName(QbSession session, string entity, string name)
    {
        try
        {
            var xml = session.RunRequest(ms =>
            {
                dynamic q = AppendQuery(ms, entity);
                switch (entity)
                {
                    case "customers": q.ORCustomerListQuery.FullNameList.Add(name); break;
                    case "vendors": q.ORVendorListQuery.FullNameList.Add(name); break;
                    case "items": q.ORListQueryWithOwnerIDAndClass.FullNameList.Add(name); break;
                    default: throw new QbAgentException($"By-name fetch not supported for {entity}");
                }
            });
            var status = ReadRsStatus(xml);
            if (status.Severity == "Error")
            {
                return new ChunkedResult(new List<Dictionary<string, object?>>(), null,
                    $"QuickBooks rejected the by-name query (status {status.Code}): {status.Message}");
            }
            var parsed = Parse(entity, xml);
            return new ChunkedResult(parsed.Rows, parsed.MaxModified, null);
        }
        catch (Exception ex)
        {
            return new ChunkedResult(new List<Dictionary<string, object?>>(), null,
                ex is QbAgentException qex ? qex.Message : ex.Message);
        }
    }

    private static List<string> FetchNamesOnly(QbSession session, string entity)
    {
        var xml = session.RunRequest(ms =>
        {
            dynamic q = AppendQuery(ms, entity);
            q.IncludeRetElementList.Add("FullName");
        });
        var status = ReadRsStatus(xml);
        if (status.Severity == "Error") throw new QbAgentException(status.Message);
        return XDocument.Parse(xml)
            .Descendants()
            .Where(e => e.Name.LocalName.EndsWith("Ret"))
            .Select(e => e.Element("FullName")?.Value ?? e.Element("Name")?.Value ?? "?")
            .ToList();
    }

    public static ChunkedResult PullChunked(QbSession session, string entity, Action<string> log, int chunkSize)
    {
        // AccountQueryRq does not support iterators at all — plain query.
        if (entity == "accounts")
        {
            return PullFullSafe(session, entity);
        }

        var all = new List<Dictionary<string, object?>>();
        DateTimeOffset? max = null;
        string? iteratorId = null;
        var chunkIndex = 0;
        // Runtime-probed "Start" value: try each candidate until QB accepts
        // one (wrong values come back as status errors, e.g. 3150 asking for
        // the iteratorID a Continue would need). Cached once discovered.
        var startCandidates = _itStartValue.HasValue ? new[] { _itStartValue.Value } : ItStartCandidates;
        var startCandidateIdx = 0;

        while (true)
        {
            chunkIndex++;
            string xml;
            try
            {
                var currentIteratorId = iteratorId;
                var startValue = startCandidates[startCandidateIdx];
                xml = session.RunRequest(ms =>
                {
                    dynamic q = AppendQuery(ms, entity);
                    if (currentIteratorId is null)
                    {
                        q.iterator.SetValue(startValue);
                    }
                    else
                    {
                        q.iterator.SetValue(ItContinue);
                        q.iteratorID.SetValue(currentIteratorId);
                    }
                    SetMaxReturned(q, entity, chunkSize);
                });
            }
            catch (Exception ex)
            {
                var message = $"chunk {chunkIndex} (after {all.Count} records) failed: {(ex is QbAgentException qex ? qex.Message : ex.Message)}";
                log($"Pull {entity}: {message}");
                return new ChunkedResult(all, max, message);
            }

            // A rejected request is an EMPTY response with an Error status,
            // not an exception — check before trusting the row count.
            var status = ReadRsStatus(xml);
            if (status.Severity == "Error")
            {
                if (iteratorId is null && startCandidateIdx < startCandidates.Length - 1)
                {
                    // Wrong Start candidate — try the next one.
                    log($"Pull {entity}: iterator value {startCandidates[startCandidateIdx]} rejected (status {status.Code}: {status.Message}) — trying next candidate.");
                    startCandidateIdx++;
                    chunkIndex--;
                    continue;
                }
                if (iteratorId is null)
                {
                    // No candidate accepted — plain unchunked query.
                    log($"Pull {entity}: chunked query rejected (status {status.Code}: {status.Message}) — falling back to one full query.");
                    return PullFullSafe(session, entity);
                }
                var message = $"chunk {chunkIndex} (after {all.Count} records) rejected by QuickBooks (status {status.Code}): {status.Message}";
                log($"Pull {entity}: {message}");
                return new ChunkedResult(all, max, message);
            }

            if (iteratorId is null)
            {
                // This Start value worked — remember it for every later pull.
                if (_itStartValue != startCandidates[startCandidateIdx])
                {
                    _itStartValue = startCandidates[startCandidateIdx];
                    log($"Pull {entity}: iterator Start value = {_itStartValue} confirmed.");
                }
            }

            var parsed = Parse(entity, xml);
            all.AddRange(parsed.Rows);
            if (parsed.MaxModified.HasValue && (!max.HasValue || parsed.MaxModified > max)) max = parsed.MaxModified;

            var (nextId, remaining) = ReadIteratorAttrs(xml);
            if (chunkIndex == 1)
            {
                log($"Pull {entity}: chunk 1 -> {parsed.Rows.Count} rows, status {status.Code}, iterator {(nextId != null ? $"active ({remaining} remaining)" : "none")}.");
            }

            if (iteratorId is null && nextId is null)
            {
                // No iterator support detected. Never trust MaxReturned
                // without an iterator to continue from: a full first page is
                // clearly truncated, and an EMPTY first page is just as
                // suspicious (a misunderstood iterator flag can yield 0 rows
                // with status 0) — verify either way with one plain query.
                if (parsed.Rows.Count >= chunkSize || parsed.Rows.Count == 0)
                {
                    log($"Pull {entity}: iterator inactive ({parsed.Rows.Count} rows in chunk 1) — verifying with one full query.");
                    return PullFullSafe(session, entity);
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
