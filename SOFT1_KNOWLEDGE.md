# Soft1 Knowledge Reference

Status: **Consolidated reference / not wired into runtime by this document**

Purpose: collect the confirmed Soft1 business/schema knowledge that already exists across the Jarvis codebase and project notes, so it can become the authoritative seed for Atlas/Jarvis knowledge work later.

This document intentionally separates:

- **Confirmed/runtime-backed facts** — already encoded in working code, live schema findings, or explicit project notes.
- **Current Jarvis runtime knowledge** — facts already injected through `JarvisKnowledgeCompanion` / `JarvisBusinessEntityCatalog`.
- **Gaps / future expansion** — areas that should be verified before being promoted to authoritative knowledge.

Do not treat unverified guesses as Soft1 facts.

---

# 1. Current knowledge architecture

The codebase already has a dedicated fact/schema layer:

```text
JarvisAgentContextBuilder
  -> JarvisKnowledgeCompanion
       -> JarvisBusinessEntityCatalog
       -> per-task schema slices
```

`JarvisAgentContextBuilder` explicitly separates behavioral policy from fact/schema knowledge. Policy comes from `JarvisPolicyRegistry`; schema/business facts come from `JarvisKnowledgeCompanion`.

`JarvisKnowledgeCompanion` is the current authoritative fact-only companion shared by agents. For Atlas, or requests containing `query_data`, it supplies the full Soft1 knowledge slice currently encoded in the runtime.

This means Atlas is **already receiving some Soft1 schema knowledge today**. The problem is not absence of a knowledge mechanism; the problem is incomplete coverage.

---

# 2. Trader entities (`TRDR`) and `SODTYPE`

## 2.1 Confirmed trader role mapping

All confirmed trader roles live in the same physical/business table: `TRDR`.

| SODTYPE | Role | Soft1 object | Incoming candidate | Outgoing candidate |
|---:|---|---|---|---|
| 12 | Supplier / Προμηθευτής | `SUPPLIER` | Yes | No |
| 13 | Customer / Πελάτης | `CUSTOMER` | No | Yes |
| 15 | Debtor / Χρεώστης | `DEBTOR` | No | Yes |
| 16 | Creditor / Πιστωτής | `CREDITOR` | Yes | No |

The runtime treats `SODTYPE` as the role discriminator for `TRDR`.

## 2.2 Known `TRDR` fields

Current authoritative knowledge slice:

```text
TRDR
CODE
NAME
AFM
SODTYPE
COMPANY
```

Additional fields used by current runtime flows:

```text
ADDRESS
CITY
IRSDATA
ZIP
JOBTYPETRD
ISACTIVE
```

## 2.3 Important business behavior

- The same AFM may legitimately exist under more than one `SODTYPE` (for example as both customer and supplier).
- Generic AFM lookup may search without `SODTYPE` when the role is not yet known.
- Role-specific lookup must include `COMPANY + AFM + SODTYPE`.
- Automatic creation from AADE currently supports only `SODTYPE 12/13` (Supplier/Customer).
- Unknown `SODTYPE` must not be mapped to a guessed Designer object.

---

# 3. Document entities, `SOSOURCE`, and Soft1 objects

## 3.1 Confirmed `SOSOURCE -> Object` mapping

| SOSOURCE | Soft1 object | Meaning / current Jarvis label |
|---:|---|---|
| 1351 | `SALDOC` | Sales / Invoices — Πωλήσεις / Τιμολόγια |
| 1353 | `LINCUSDOC` | Sales services — Παροχή Υπηρεσιών πωλήσεων |
| 1251 | `PURDOC` | Supplier receipt / purchase document — Παραλαβή / ΔΑ Προμηθευτή |
| 1253 | `LINSUPDOC` | Purchase services — Παροχή Υπηρεσιών αγορών |
| 5151 | `ITEITEDOC` | Internal movement / production — Ενδοδιακίνηση / Παραγωγή |
| 1412 | `BFNSUPDOC` | Transfer/payment to supplier — Έμβασμα σε προμηθευτή |
| 1413 | `BFNCUSDOC` | Transfer/payment from customer — Έμβασμα από πελάτη |
| 2021 | `SOTASK` | CRM action/task; identifier is `SOACTION`, not `FINDOC` |

The `open_document` runtime whitelist uses these mappings.

## 3.2 Currently writable document circuits

`create_order` currently allows only:

```text
1351  SALDOC
1251  PURDOC
1353  LINCUSDOC
1253  LINSUPDOC
5151  ITEITEDOC
```

Retail is intentionally out of scope because it is not in the write whitelist.

`1412`, `1413`, and `2021` are known/openable object mappings but are not part of the current `create_order` line-document write flow.

---

# 4. `FINDOC` knowledge

## 4.1 Current authoritative fields

```text
FINDOC
TRDR
TRNDATE
FINCODE
SUMAMNT
SERIES
SOSOURCE
COMPANY
INSUSER
INSDATE
```

Additional fields used by confirmed runtime logic include:

```text
FPRMS
ISCANCEL
PRJC
INST
TRDBRANCH
PAYMENT
SHIPMENT
```

## 4.2 Confirmed joins

Trader join:

```sql
TRDR.COMPANY = FINDOC.COMPANY
AND TRDR.TRDR = FINDOC.TRDR
```

Series join currently encoded in companion knowledge:

```sql
SERIES.COMPANY = FINDOC.COMPANY
AND SERIES.SERIES = FINDOC.SERIES
AND SERIES.SOSOURCE = FINDOC.SOSOURCE
```

`FPRMS` relationship currently encoded in `JarvisBusinessEntityCatalog`:

```text
FINDOC.FPRMS = FPRMS.FPRMS
SERIES.FPRMS = FPRMS.FPRMS
```

## 4.3 Identity / navigation

For normal document circuits, Jarvis navigation identity is:

```text
SOSOURCE + FINDOC
```

This is distinct from CRM action `SOSOURCE=2021`, where the relevant record identifier is `SOACTION`, not `FINDOC`.

## 4.4 Known non-table warning

`FINTRD` is explicitly recorded as a known non-table name in the current knowledge companion. Do not assume it can be queried as a normal table.

---

# 5. `SERIES`

Confirmed key behavior:

- The same numeric `SERIES` can exist under multiple `SOSOURCE` circuits.
- Therefore `SERIES` alone is not globally unique for document meaning.
- Runtime code keys series metadata by `(SERIES, SOSOURCE)` and scopes by `COMPANY`.

Known fields:

```text
COMPANY
SERIES
SOSOURCE
NAME
FPRMS
```

Current join identity:

```text
COMPANY + SERIES + SOSOURCE
```

This is important for Atlas: never infer document type from a bare `SERIES` number without the source/circuit context.

---

# 6. Document line tables / Designer tables

Live project findings recorded in code and README establish the following mapping.

## 6.1 Virtual line table per document source

| SOSOURCE | Object | XModule table used for writing |
|---:|---|---|
| 1351 | `SALDOC` | `ITELINES` |
| 1251 | `PURDOC` | `ITELINES` |
| 1353 | `LINCUSDOC` | `LINLINES` |
| 1253 | `LINSUPDOC` | `LINLINES` |
| 5151 | `ITEITEDOC` | `ITELINES` |

Important correction captured in the project:

- `LINCUSDOC` / `LINSUPDOC` use `LINLINES`, not the originally assumed `SRVLINES` in the active write implementation.

## 6.2 Physical line storage

Project notes/code record that the virtual line table names map back to the physical table:

```text
MTRLINES
```

for the relevant material/service line history used by the current implementation.

Current carry-over fields from historical `MTRLINES` rows:

```text
INST
PRJC
CNTR
BUSUNITS
```

## 6.3 Known extra line/table facts

- `ASSLINES` is noted as fixed-assets related and is outside the current document-reader write scope.
- `HEADERLINE` is associated with produced-item handling for `ITEITEDOC` and is outside the current first implementation.
- Mixed documents containing both item and service lines in a single `SALDOC` / `PURDOC` are explicitly not fully supported in the first implementation.

The repository contains `S1_HeaderLines_Mapping.xlsx` and `S1_HeaderLines_Mapping_v2.xlsx` as detailed reference artifacts produced from Designer screenshots, BlackBook examples, live `GetXStrings('SOSOURCE')` / `GetXStrings('SODTYPE')`, and `getObjectTables` investigation.

---

# 7. `TRDTRN`

Confirmed project/schema facts:

Known fields used by the runtime:

```text
FINDOC
FINCODE
TRNDATE
COMMENTS
TRDR
SOSOURCE
COMPANY
```

Important relationship:

```text
TRDTRN.FINDOC -> FINDOC.FINDOC
```

The project explicitly records that `TRDTRN` does **not** contain the same audit fields assumed for `FINDOC` (for example `INSDATE`/`REMARKS` are not used there in the duplicate-detection logic).

Current duplicate detection checks both `FINDOC` and `TRDTRN` and combines document number matching with date matching.

---

# 8. Materials/items and supplier code mapping

## 8.1 `MTRL`

Known fields used by current code:

```text
MTRL
CODE
NAME
COMPANY
MTRUNIT1
ISACTIVE
```

## 8.2 `MTRSUPCODE`

Used to map a supplier's item code to the company's Soft1 material/item:

```text
MTRSUPCODE.TRDR
MTRSUPCODE.MTRL
MTRSUPCODE.MTRSUPCODE
MTRSUPCODE.COMPANY
MTRSUPCODE.ISACTIVE
```

Current matching relationship:

```text
MTRSUPCODE.MTRL -> MTRL.MTRL
```

## 8.3 `MTRUNIT`

Used to resolve the display unit:

```text
MTRL.MTRUNIT1 -> MTRUNIT.MTRUNIT
```

with company scoping:

```text
MTRUNIT.COMPANY = MTRL.COMPANY
```

Known display field:

```text
MTRUNIT.SHORTCUT
```

---

# 9. Trader balances

Current companion knowledge includes `TRDBALSHEET`.

Known fields:

```text
TRDR
FISCPRD
LDEBIT
LCREDIT
```

Purpose recorded in runtime knowledge:

> progressive trader balances by fiscal year/period

This is a known schema slice, not yet a full accounting ontology.

---

# 10. Users and contacts

## `USERS`

Known fields:

```text
USERS
NAME
```

## `PRSN`

Known contact fields in companion/runtime flows:

```text
NAME
NAME2
EMAIL
EMAIL1
```

Additional contact display fields are used elsewhere in the UI/tool flow, but should be promoted to this authoritative section only after direct schema confirmation.

---

# 11. CRM actions / `SOACTION`

Known object mapping:

```text
SOSOURCE 2021 -> SOTASK
```

Important identity rule:

```text
SOACTION id != FINDOC id
```

Current project notes also establish that when creating `SOTASK`, fields such as `SODTYPE`, `SOSOURCE`, and `SERIESNUM` should not be manually forced when the Soft1 module/series can populate them correctly. The runtime sets `SERIES` and relies on the Soft1 object to derive the appropriate classification fields.

Fields used by current flows include:

```text
SOACTION
SERIES
COMMENTS
REMARKS
ACTOR
ORDEREDBY
ACTSTATUS
FROMDATE
DURATION
SOSMALLINT
```

This list is practical runtime knowledge, not a complete `SOACTION` schema.

---

# 12. Custom/company tables currently known

## `CCCLOADING`

Confirmed execution date field:

```text
Insdate
```

Known invalid assumptions recorded by the runtime:

```text
StartTime
Executiondate
```

## `CCCLOADCOMPS`

Known as loading lines/compartments with parent:

```text
CCCLOADING
```

These are company-specific/custom entities and should remain separated conceptually from generic Soft1 core schema.

---

# 13. Company and tenancy rules

Current runtime consistently scopes many Soft1 queries by:

```text
COMPANY = xSupport.ConnectionInfo.CompanyId
```

Important examples:

- `TRDR`
- `FINDOC`
- `SERIES`
- `MTRL`
- `MTRUNIT`
- custom tables where applicable

Atlas should not assume an unscoped ID is globally meaningful across companies.

---

# 14. Runtime schema discovery already used by Jarvis

Jarvis already has a controlled read-only discovery path through `query_data`.

Properties:

- accepts only `SELECT`
- rejects multi-statement SQL
- blocks mutation/admin keywords
- executes through `XSupport.GetSQLDataSet`
- converts results to a generic `DataTable`
- returns dynamic columns/rows
- caps conversational result data to protect model context

This means future Soft1 expertise should combine:

```text
static confirmed knowledge
        +
runtime read-only schema/data discovery
```

rather than placing every possible Soft1 field/table in the model prompt.

---

# 15. Existing source material in this repository

Primary sources consolidated here:

```text
Core/JarvisKnowledgeCompanion.cs
Core/JarvisBusinessEntityCatalog.cs
Core/JarvisTools.cs
Core/JarvisAgentContextBuilder.cs
README.md
S1_HeaderLines_Mapping.xlsx
S1_HeaderLines_Mapping_v2.xlsx
```

Secondary project docs may contain additional implementation history, but facts should be promoted here only when they are traceable to runtime code, live schema discovery, SDK/Designer evidence, or explicit verified project notes.

---

# 16. What Atlas already knows today

Through `JarvisKnowledgeCompanion`, Atlas currently receives full Soft1 knowledge when:

- the logical agent is `Atlas`, or
- the request exposes `query_data`, or
- the registered task is one of the query/data/report/document tasks that request the full knowledge slice.

Current injected knowledge includes:

```text
businessEntities (TRDR roles + FINDOC identity/classification metadata)
TRDR
FINDOC
SERIES
TRDBALSHEET
CCCLOADING
CCCLOADCOMPS
USERS
PRSN
documentSources
```

So the future work is best described as **expanding and normalizing the companion knowledge**, not creating Atlas knowledge from zero.

---

# 17. Important gaps before calling Atlas a Soft1 expert

The following should be expanded/verified next:

1. Complete `SODTYPE` dictionary beyond trader roles 12/13/15/16.
2. Broader authoritative `SOSOURCE` dictionary, including circuits not currently supported by Jarvis.
3. Complete object-to-table mappings from `getObjectTables` results.
4. `FPRMS` semantics and reliable classification of document types.
5. `MTRL` / stock / warehouse / availability tables and joins.
6. Accounting/financial tables and semantics beyond `TRDBALSHEET`.
7. Pricing/discount/tax/VAT semantics.
8. `SERIES` business rules and important series fields beyond the current join/display subset.
9. Projects/installations/branches (`PRJC`, `INST`, `TRDBRANCH`) with authoritative joins and meanings.
10. CRM (`SOACTION`, `ACTLINES`) fuller schema.
11. Contacts (`PRSN`) fuller confirmed schema.
12. Company-custom `CCC*` tables kept in a separate company-specific knowledge section.
13. Clear distinction between physical DB tables, XModule table names, Designer object names, and logical business entities.
14. Confidence/provenance tag per fact (`runtime`, `live schema`, `Designer`, `BlackBook`, `company custom`).

---

# 18. Recommended knowledge model for the next version

Do not turn this whole file into one giant system prompt.

Recommended structure:

```text
Soft1 Core Knowledge
  - Objects
  - SODTYPE
  - SOSOURCE
  - Tables
  - Relations
  - Fields
  - Business semantics

Company Knowledge
  - CCC tables
  - custom fields
  - configured series/forms
  - local business rules

Runtime Discovery
  - query_data
  - safe metadata/schema queries
```

A future structured representation can use entries such as:

```json
{
  "kind": "document_source",
  "sosource": 1351,
  "object": "SALDOC",
  "headerTable": "FINDOC",
  "lineTable": "ITELINES",
  "physicalLineTable": "MTRLINES",
  "status": "confirmed",
  "evidence": ["runtime", "getObjectTables"]
}
```

and:

```json
{
  "kind": "trader_role",
  "sodtype": 12,
  "role": "Supplier",
  "object": "SUPPLIER",
  "table": "TRDR",
  "status": "confirmed"
}
```

This makes the knowledge reusable by Atlas, validators, schema discovery, Help/Sage, and future tools without duplicating facts in prompts.
