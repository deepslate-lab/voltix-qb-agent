using System.Text.Json;
using System.Xml.Linq;

namespace VoltixQbAgent.QuickBooks;

/// <summary>
/// Document posting (phase Q3, queue-and-confirm). Invoices only for now.
///
/// Idempotency: Voltix assigns each invoice a unique RefNumber (the receipt
/// number, 11-char capped). Before adding, the agent queries QB for that
/// RefNumber — if the invoice already exists (a previous attempt posted but
/// the confirmation never reached Voltix), the existing document is reported
/// instead of creating a duplicate.
///
/// Lines carry Quantity + Amount (net line totals from the POS) so the
/// document total is exact; QB applies its own item tax codes on top and the
/// resulting total is reported back for server-side divergence warnings.
/// </summary>
public static class QbPoster
{
    public sealed record PostResult(string TxnId, string RefNumber, double? QbTotal, bool AlreadyExisted);

    public static PostResult PostInvoice(QbSession session, JsonElement payload, Action<string> log)
    {
        var refNumber = Str(payload, "ref_number")
            ?? throw new QbAgentException("Invoice payload has no ref_number.");
        var customerListId = Str(payload, "customer_list_id")
            ?? throw new QbAgentException("Invoice payload has no customer_list_id.");

        var existing = FindInvoiceByRefNumber(session, refNumber);
        if (existing != null)
        {
            log($"Invoice {refNumber} already exists in QuickBooks (TxnID {existing.TxnId}) — reporting the existing document.");
            return existing with { AlreadyExisted = true };
        }

        var xml = session.RunRequest(ms =>
        {
            dynamic rq = ms.AppendInvoiceAddRq();
            rq.CustomerRef.ListID.SetValue(customerListId);
            rq.RefNumber.SetValue(refNumber);
            var txnDate = Str(payload, "txn_date");
            if (txnDate != null && DateTime.TryParse(txnDate, out var d)) rq.TxnDate.SetValue(d);
            var memo = Str(payload, "memo");
            if (!string.IsNullOrEmpty(memo)) rq.Memo.SetValue(memo);

            foreach (var line in payload.GetProperty("lines").EnumerateArray())
            {
                dynamic l = rq.ORInvoiceLineAddList.Append().InvoiceLineAdd;
                l.ItemRef.ListID.SetValue(Str(line, "item_list_id"));
                if (line.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number)
                    l.Quantity.SetValue(q.GetDouble());
                if (line.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number)
                    l.Amount.SetValue(a.GetDouble());
                var desc = Str(line, "description");
                if (!string.IsNullOrEmpty(desc)) l.Desc.SetValue(desc.Length > 4000 ? desc[..4000] : desc);
            }
        });

        var status = ReadAddStatus(xml);
        if (status.Severity == "Error")
        {
            throw new QbAgentException($"QuickBooks rejected the invoice (status {status.Code}): {status.Message}");
        }
        var ret = XDocument.Parse(xml).Descendants("InvoiceRet").FirstOrDefault()
            ?? throw new QbAgentException("QuickBooks returned no InvoiceRet for the added invoice.");
        return new PostResult(
            ret.Element("TxnID")?.Value ?? "",
            ret.Element("RefNumber")?.Value ?? refNumber,
            ParseTotal(ret),
            false);
    }

    private static double? ParseTotal(XElement ret) =>
        double.TryParse(ret.Element("TotalAmount")?.Value, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;

    private static PostResult? FindInvoiceByRefNumber(QbSession session, string refNumber)
    {
        var xml = session.RunRequest(ms =>
        {
            dynamic q = ms.AppendInvoiceQueryRq();
            q.ORInvoiceQuery.RefNumberList.Add(refNumber);
        });
        var status = ReadAddStatus(xml);
        // "not found" comes back as a warn-status empty response — only a hard
        // error should stop the pre-check (and then the Add would fail too).
        if (status.Severity == "Error")
            throw new QbAgentException($"Invoice lookup failed (status {status.Code}): {status.Message}");
        var ret = XDocument.Parse(xml).Descendants("InvoiceRet").FirstOrDefault();
        if (ret == null) return null;
        return new PostResult(
            ret.Element("TxnID")?.Value ?? "",
            ret.Element("RefNumber")?.Value ?? refNumber,
            ParseTotal(ret),
            true);
    }

    /// <summary>Status attributes live on the *Rs element (same convention as
    /// the list queries — rejections are statuses, not exceptions).</summary>
    private static (string Severity, string Code, string Message) ReadAddStatus(string xml)
    {
        var doc = XDocument.Parse(xml);
        var rs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("Rs"));
        return (
            rs?.Attribute("statusSeverity")?.Value ?? "Info",
            rs?.Attribute("statusCode")?.Value ?? "0",
            rs?.Attribute("statusMessage")?.Value ?? ""
        );
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
