using System.Text.Json;
using System.Xml.Linq;

namespace VoltixQbAgent.QuickBooks;

/// <summary>
/// Document posting (phase Q3, queue-and-confirm): invoices and estimates.
///
/// Idempotency: Voltix assigns each document a unique RefNumber (the
/// receipt/quotation number, 11-char capped). Before adding, the agent
/// queries QB for that RefNumber — if the document already exists (a
/// previous attempt posted but the confirmation never reached Voltix), the
/// existing document is reported instead of creating a duplicate.
///
/// Lines carry Quantity + Amount (NET line totals from the POS — line
/// discounts baked in; QB tenants manage stock/variants/UOMs in Voltix so no
/// UOM or variant data is posted). A document-level discount arrives as
/// payload.discount_line and posts as a negative-amount line using the
/// configured discount item — created in QuickBooks (Other Charge) when it
/// does not exist yet; the resulting ListID rides back in the job result so
/// Voltix can remember it.
/// </summary>
public static class QbPoster
{
    public sealed record PostResult(
        string TxnId, string RefNumber, double? QbTotal, bool AlreadyExisted, string? DiscountItemListId);

    public static PostResult PostInvoice(QbSession session, JsonElement payload, Action<string> log) =>
        PostDocument(session, payload, log, estimate: false);

    public static PostResult PostEstimate(QbSession session, JsonElement payload, Action<string> log) =>
        PostDocument(session, payload, log, estimate: true);

    private static PostResult PostDocument(QbSession session, JsonElement payload, Action<string> log, bool estimate)
    {
        var docWord = estimate ? "estimate" : "invoice";
        var refNumber = Str(payload, "ref_number")
            ?? throw new QbAgentException($"The {docWord} payload has no ref_number.");
        var customerListId = Str(payload, "customer_list_id")
            ?? throw new QbAgentException($"The {docWord} payload has no customer_list_id.");

        var existing = FindByRefNumber(session, refNumber, estimate);
        if (existing != null)
        {
            log($"{char.ToUpper(docWord[0])}{docWord[1..]} {refNumber} already exists in QuickBooks (TxnID {existing.TxnId}) — reporting the existing document.");
            return existing with { AlreadyExisted = true };
        }

        // Resolve (and if needed create) the discount item BEFORE building the
        // document, so a failure there never leaves a half-posted document.
        string? discountListId = null;
        double discountAmount = 0;
        if (payload.TryGetProperty("discount_line", out var dl) && dl.ValueKind == JsonValueKind.Object)
        {
            discountAmount = dl.TryGetProperty("amount", out var da) && da.ValueKind == JsonValueKind.Number ? da.GetDouble() : 0;
            if (discountAmount > 0)
            {
                discountListId = Str(dl, "item_list_id") ?? EnsureItemByName(session, Str(dl, "name") ?? "Discount", log);
            }
        }

        var xml = session.RunRequest(ms =>
        {
            dynamic rq = estimate ? ms.AppendEstimateAddRq() : ms.AppendInvoiceAddRq();
            rq.CustomerRef.ListID.SetValue(customerListId);
            rq.RefNumber.SetValue(refNumber);
            var txnDate = Str(payload, "txn_date");
            if (txnDate != null && DateTime.TryParse(txnDate, out var d)) rq.TxnDate.SetValue(d);
            var memo = Str(payload, "memo");
            if (!string.IsNullOrEmpty(memo)) rq.Memo.SetValue(memo);

            foreach (var line in payload.GetProperty("lines").EnumerateArray())
            {
                dynamic l = estimate
                    ? rq.OREstimateLineAddList.Append().EstimateLineAdd
                    : rq.ORInvoiceLineAddList.Append().InvoiceLineAdd;
                l.ItemRef.ListID.SetValue(Str(line, "item_list_id"));
                if (line.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number)
                    l.Quantity.SetValue(q.GetDouble());
                if (line.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number)
                    l.Amount.SetValue(a.GetDouble());
                var desc = Str(line, "description");
                if (!string.IsNullOrEmpty(desc)) l.Desc.SetValue(desc.Length > 4000 ? desc[..4000] : desc);
            }

            if (discountListId != null && discountAmount > 0)
            {
                dynamic l = estimate
                    ? rq.OREstimateLineAddList.Append().EstimateLineAdd
                    : rq.ORInvoiceLineAddList.Append().InvoiceLineAdd;
                l.ItemRef.ListID.SetValue(discountListId);
                l.Amount.SetValue(-discountAmount);
                l.Desc.SetValue("Discount");
            }
        });

        var status = ReadAddStatus(xml);
        if (status.Severity == "Error")
        {
            throw new QbAgentException($"QuickBooks rejected the {docWord} (status {status.Code}): {status.Message}");
        }
        var retName = estimate ? "EstimateRet" : "InvoiceRet";
        var ret = XDocument.Parse(xml).Descendants(retName).FirstOrDefault()
            ?? throw new QbAgentException(
                $"QuickBooks returned no {retName} for the added {docWord} (status {status.Code}, severity {status.Severity}: {status.Message}).");
        return new PostResult(
            ret.Element("TxnID")?.Value ?? "",
            ret.Element("RefNumber")?.Value ?? refNumber,
            ParseTotal(ret),
            false,
            discountListId);
    }

    private static double? ParseTotal(XElement ret) =>
        double.TryParse(ret.Element("TotalAmount")?.Value, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;

    private static PostResult? FindByRefNumber(QbSession session, string refNumber, bool estimate)
    {
        var xml = session.RunRequest(ms =>
        {
            dynamic q = estimate ? ms.AppendEstimateQueryRq() : ms.AppendInvoiceQueryRq();
            if (!TryAddRefNumberFilter(q, refNumber))
                throw new QbAgentException("The RefNumber filter is not supported by this QBFC version.");
        });
        var status = ReadAddStatus(xml);
        // "not found" comes back as a warn-status empty response — only a hard
        // error should stop the pre-check (and then the Add would fail too).
        if (status.Severity == "Error")
            throw new QbAgentException($"{(estimate ? "Estimate" : "Invoice")} lookup failed (status {status.Code}): {status.Message}");
        var ret = XDocument.Parse(xml).Descendants(estimate ? "EstimateRet" : "InvoiceRet").FirstOrDefault();
        if (ret == null) return null;
        return new PostResult(
            ret.Element("TxnID")?.Value ?? "",
            ret.Element("RefNumber")?.Value ?? refNumber,
            ParseTotal(ret),
            true,
            null);
    }

    /// <summary>The RefNumber list lives under differently named OR groups per
    /// query type — probe the known shapes.</summary>
    private static bool TryAddRefNumberFilter(dynamic q, string refNumber)
    {
        try { q.ORInvoiceQuery.RefNumberList.Add(refNumber); return true; } catch { }
        try { q.ORTxnNoAccountQuery.RefNumberList.Add(refNumber); return true; } catch { }
        try { q.ORTxnQuery.RefNumberList.Add(refNumber); return true; } catch { }
        try { q.RefNumberList.Add(refNumber); return true; } catch { }
        return false;
    }

    /// <summary>Find an item by FullName, creating it as an Other Charge item
    /// when absent (used for the configured document-discount item).</summary>
    private static string EnsureItemByName(QbSession session, string name, Action<string> log)
    {
        var xml = session.RunRequest(ms =>
        {
            dynamic q = ms.AppendItemQueryRq();
            q.ORListQueryWithOwnerIDAndClass.FullNameList.Add(name);
        });
        var listId = XDocument.Parse(xml).Descendants()
            .Where(e => e.Name.LocalName.EndsWith("Ret"))
            .Select(e => e.Element("ListID")?.Value)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (!string.IsNullOrEmpty(listId))
        {
            log($"Discount item \"{name}\" found in QuickBooks (ListID {listId}).");
            return listId!;
        }

        log($"Discount item \"{name}\" not found in QuickBooks — creating it as an Other Charge item.");
        var addXml = session.RunRequest(ms =>
        {
            dynamic rq = ms.AppendItemOtherChargeAddRq();
            // QB item names cap at 31 chars.
            rq.Name.SetValue(name.Length > 31 ? name[..31] : name);
        });
        var addStatus = ReadAddStatus(addXml);
        if (addStatus.Severity == "Error")
        {
            throw new QbAgentException(
                $"Could not create the discount item \"{name}\" in QuickBooks (status {addStatus.Code}): {addStatus.Message}. " +
                "Create it manually in QuickBooks (Lists → Item List → New → Other Charge) and run an items sync.");
        }
        var addRet = XDocument.Parse(addXml).Descendants("ItemOtherChargeRet").FirstOrDefault()
            ?? throw new QbAgentException($"QuickBooks returned no ItemOtherChargeRet after creating \"{name}\".");
        var newId = addRet.Element("ListID")?.Value
            ?? throw new QbAgentException($"QuickBooks returned no ListID for the created discount item \"{name}\".");
        log($"Discount item \"{name}\" created in QuickBooks (ListID {newId}).");
        return newId;
    }

    /// <summary>Status attributes live on the request's *Rs element (same
    /// convention as the list queries — rejections are statuses, not
    /// exceptions). The OUTER QBXMLMsgsRs envelope also ends in "Rs" but
    /// carries no status attributes — matching it first masked every real
    /// rejection as Info (v0.2.9 bug).</summary>
    private static (string Severity, string Code, string Message) ReadAddStatus(string xml)
    {
        var doc = XDocument.Parse(xml);
        var rs = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName.EndsWith("Rs") && e.Name.LocalName != "QBXMLMsgsRs");
        return (
            rs?.Attribute("statusSeverity")?.Value ?? "Info",
            rs?.Attribute("statusCode")?.Value ?? "0",
            rs?.Attribute("statusMessage")?.Value ?? ""
        );
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
