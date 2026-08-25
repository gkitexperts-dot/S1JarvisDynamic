using System;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Verified Soft1 document knowledge used by the specialised DR assistant.
    /// Keep this block conservative: known schema and confirmed mappings only.
    /// Historical Soft1 evidence is authoritative whenever a document cannot be
    /// classified safely from the generic mapping alone.
    /// </summary>
    internal static class DrDocumentKnowledge
    {
        internal static string BuildPromptBlock()
        {
            return
                "SOFT1 DOCUMENT KNOWLEDGE (verified):\n" +
                "- TRDR is the trader/account table. TRDR is the id; CODE, NAME and AFM identify the account. " +
                "SODTYPE 12=Supplier and 13=Customer. SODTYPE describes the trader role, not the document type.\n" +
                "- FINDOC contains document movements: FINDOC=id, TRDR=trader id, TRNDATE, FINCODE, SUMAMNT, SERIES, SOSOURCE, COMPANY.\n" +
                "- To resolve the configured series/document name, join SERIES on COMPANY + SERIES + SOSOURCE.\n" +
                "- Confirmed SOSOURCE mappings currently known to Jarvis: " +
                "1351=Sales/Invoices, 1353=Sales Services, 1251=Supplier Receipt/Delivery Note, " +
                "1253=Purchase Services, 5151=Internal Movement/Production, 1412=Payment to Supplier, " +
                "1413=Receipt from Customer, 2021=CRM Task (SOACTION, not FINDOC).\n" +
                "- Never invent another SOSOURCE from generic accounting knowledge. For expenses or any unknown circuit, " +
                "derive the real SOSOURCE/SERIES/BUNIT/cost-centre pattern from the company's historical Soft1 documents; " +
                "if evidence is ambiguous, ask the operator.\n" +
                "- DR handles purchases, sales and expenses with the same base algorithm: extract -> identify trader -> " +
                "duplicate check -> resolve historical posting profile -> resolve lines -> review -> controlled Soft1 write.\n" +
                "- Expense posting may be Detailed or Consolidated. Do not assume one mode from the PDF. Use historical " +
                "documents for the same trader/series/circuit. If history consistently collapses many source rows into one " +
                "Soft1 line, propose Consolidated posting. If history is mixed, ask the operator.\n" +
                "- Before consolidating, totals and VAT treatment must reconcile. Different VAT rates or different cost-centre " +
                "patterns may require more than one consolidated Soft1 line.\n" +
                "- Item resolution order: exact internal MTRL.CODE when appropriate; then supplier-item mapping in MTRSUPCODE " +
                "for the current trader and the supplier code printed on the document; then description-based search/proposal.\n" +
                "- A fuzzy/description match is only a proposal. Mapping to an existing item or creating a new item requires " +
                "operator confirmation. Never claim a write occurred unless deterministic Soft1 code reports success.\n" +
                "- myDATA evidence, when available, is a cross-check of extracted data. A mismatch is a warning/blocker to review, " +
                "not something to hide or silently overwrite.\n" +
                "- Preserve a structured decision audit (evidence, selected ids, confidence, operator decisions). Do not store or " +
                "expose hidden model chain-of-thought.\n";
        }
    }
}
