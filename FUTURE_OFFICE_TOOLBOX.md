# Future Office Toolbox Blueprint

Status: **Preparation only / NOT wired into runtime**

This document is a forward-looking toolbox specification for a future S1Jarvis release.
It intentionally makes **no runtime integration change**. Nothing here is registered in
`JarvisTaskRegistry`, `JarvisToolRegistry`, provider adapters, orchestration, UI, or Soft1 flows.
The purpose is to keep a concrete, implementation-ready library of Office capabilities that can
be activated later without redesigning the architecture.

## Architectural rule

Jarvis remains the orchestrator. Logical agents remain provider-neutral. Office capabilities are
implemented as deterministic tools. The LLM/owner-agent decides **what** should be done; the Office
tool implementation decides **how** the file is changed and validates the result.

Suggested future shape:

```text
User
  -> Jarvis orchestration
  -> owner agent (likely Atlas for document/data work)
  -> scoped Office tools
  -> deterministic Office engine
  -> validated artifact
  -> Jarvis result presentation
```

Do not give an agent unrestricted filesystem or shell access just to edit Office files. Prefer
explicitly registered operations with validated arguments and controlled input/output paths.

---

# 1. Excel Toolbox

## 1.1 Workbook inspection and reading

### `excel_inspect_workbook`
Returns workbook metadata without mutating the file.

Recommended output:
- sheet names and visibility
- used range per sheet
- tables and named ranges
- formulas count and formula locations
- merged ranges
- freeze panes
- charts/pivots presence
- workbook protection state
- approximate row/column counts
- warnings for unsupported or risky features

### `excel_read_range`
Reads a selected range while preserving cell identity.

Recommended inputs:
- workbook reference
- sheet name
- range/address
- value mode: displayed/cached/raw
- include formulas
- include number formats

Recommended output per cell:
- address
- raw value
- displayed/cached value when available
- formula when present
- data type
- number format

### `excel_find`
Searches workbook values/formulas/comments for text or patterns.

### `excel_get_tables`
Returns structured table definitions and table ranges.

### `excel_get_named_ranges`
Returns workbook/sheet named ranges.

## 1.2 Editing values and formulas

### `excel_write_range`
Writes values into a bounded range.

### `excel_set_formula`
Writes formulas into one cell or a validated range.

### `excel_fill_formula`
Copies a formula pattern down/across a target range using correct relative/absolute references.

### `excel_clear_range`
Clears values/formulas while optionally preserving styles.

### `excel_find_replace`
Controlled find/replace in values, formulas, or both.

## 1.3 Sheet operations

### `excel_create_sheet`
Creates a worksheet with a validated unique name.

### `excel_rename_sheet`
Renames a worksheet and updates dependent references where supported.

### `excel_copy_sheet`
Duplicates a worksheet.

### `excel_delete_sheet`
Deletes a worksheet with safety checks.

### `excel_move_sheet`
Changes sheet order.

### `excel_hide_sheet`
Hide/unhide worksheet.

## 1.4 Formatting and presentation

### `excel_format_range`
Applies controlled formatting:
- font/bold/italic/size
- fill
- alignment
- borders
- wrapping
- number/date/percentage/currency format

### `excel_autofit`
Auto-sizes selected columns/rows with configurable max width/height.

### `excel_freeze_panes`
Adds/removes freeze panes.

### `excel_merge_cells`
Merges/unmerges a validated range.

### `excel_apply_header_style`
Convenience operation for consistent business headers.

### `excel_apply_business_table_style`
Applies a standard readable table style without rebuilding the data.

## 1.5 Data operations

### `excel_sort_range`
Sorts by one or more columns.

### `excel_filter_range`
Adds or adjusts filters.

### `excel_create_table`
Converts a range to a structured Excel table.

### `excel_remove_duplicates`
Removes duplicates using selected key columns.

### `excel_fill_missing_values`
Controlled fill strategy for blanks, with explicit policy.

### `excel_text_to_columns`
Splits text by delimiter/fixed pattern.

### `excel_normalize_types`
Normalizes obvious text-number/date issues only when rules are explicit.

## 1.6 Analysis helpers

These tools should remain deterministic; the owner agent decides when to call them.

### `excel_profile_data`
Returns:
- row/column counts
- null/blank counts
- distinct counts
- numeric min/max/avg/median where applicable
- likely date/text/numeric columns
- suspicious type inconsistencies

### `excel_group_and_aggregate`
Group by selected fields and calculate sum/count/avg/min/max.

### `excel_compare_periods`
Compares two selected periods and returns deltas/percent changes.

### `excel_detect_outliers`
Returns candidate outliers using an explicit method and parameters.

### `excel_create_kpi_summary`
Creates a structured KPI result from explicitly supplied measures/dimensions.

### `excel_create_summary_sheet`
Materializes a new summary worksheet from a validated analysis result.

## 1.7 Charts and visual helpers

### `excel_create_chart`
Creates a chart from explicit source ranges.
Supported target types for first version:
- column
- bar
- line
- pie/doughnut
- area
- scatter

### `excel_update_chart`
Updates title/source/labels/placement.

### `excel_conditional_format`
Adds deterministic conditional-format rules.

### `excel_add_sparkline`
Optional later capability.

## 1.8 Validation and output

### `excel_validate_workbook`
Checks before result delivery:
- workbook opens structurally
- requested sheets/ranges exist
- no accidental `#REF!` introduced by the edit path
- formulas remain syntactically plausible
- original sheets were not silently lost
- output file was created

### `excel_save_as`
Writes a new output artifact by default rather than overwriting the source.

### `excel_export_pdf`
Optional future operation when a reliable rendering engine is available.

## Excel recommended first release

High-value initial set:

```text
excel_inspect_workbook
excel_read_range
excel_write_range
excel_set_formula
excel_fill_formula
excel_create_sheet
excel_rename_sheet
excel_format_range
excel_autofit
excel_sort_range
excel_filter_range
excel_create_table
excel_create_summary_sheet
excel_create_chart
excel_validate_workbook
excel_save_as
```

---

# 2. Word Toolbox

## 2.1 Inspection and reading

### `word_inspect_document`
Returns:
- paragraphs/headings
- tables
- sections
- headers/footers presence
- page setup
- styles used
- comments/footnotes presence when supported

### `word_read_content`
Reads document content as structured blocks rather than flattening everything to one string.

Recommended block types:
- paragraph
- heading
- list item
- table
- page/section break
- caption

### `word_find`
Searches text in body/tables/headers/footers according to scope.

## 2.2 Content editing

### `word_replace_text`
Controlled text replacement.

### `word_insert_paragraph`
Inserts paragraph at a defined anchor.

### `word_insert_heading`
Inserts heading using a defined style level.

### `word_insert_table`
Creates a Word table from structured rows/columns.

### `word_update_table`
Updates an existing table by index/bookmark/anchor.

### `word_insert_list`
Creates numbered or bulleted list.

### `word_insert_page_break`
Adds page break at an explicit anchor.

## 2.3 Formatting

### `word_format_paragraph`
Alignment, spacing, indentation, style.

### `word_format_run`
Bold/italic/font/size/underline.

### `word_apply_style`
Applies a named document style.

### `word_set_page_layout`
Margins, orientation, paper size where supported.

### `word_set_header_footer`
Controlled update of header/footer content.

## 2.4 Business document helpers

### `word_create_report`
Creates a structured report from supplied sections, tables, and evidence.

### `word_create_letter`
Creates a letter from supplied sender/recipient/body metadata.

### `word_create_template_from_document`
Extracts reusable structural placeholders from a known document.

### `word_fill_template`
Fills approved placeholders/bookmarks/content controls.

### `word_insert_chart_image`
Adds a pre-rendered chart/image artifact with caption.

## 2.5 Validation and output

### `word_validate_document`
Checks output package integrity and requested content presence.

### `word_save_as`
Saves a new `.docx` artifact by default.

### `word_export_pdf`
Optional when a reliable renderer is available.

## Word recommended first release

```text
word_inspect_document
word_read_content
word_replace_text
word_insert_paragraph
word_insert_heading
word_insert_table
word_format_paragraph
word_format_run
word_apply_style
word_set_page_layout
word_create_report
word_fill_template
word_validate_document
word_save_as
```

---

# 3. PowerPoint Toolbox

## 3.1 Inspection and reading

### `ppt_inspect_presentation`
Returns:
- slide count
- slide titles
- layouts
- text blocks
- tables/charts/images presence
- speaker notes presence
- theme/master metadata where available

### `ppt_read_slide`
Returns structured content for one slide.

### `ppt_find`
Searches text across slides/notes.

## 3.2 Slide operations

### `ppt_create_presentation`
Creates a new presentation from an approved base/theme/template.

### `ppt_add_slide`
Adds a slide using a selected layout.

### `ppt_duplicate_slide`
Duplicates an existing slide.

### `ppt_delete_slide`
Deletes a slide with explicit index/id.

### `ppt_reorder_slides`
Moves slides to requested positions.

## 3.3 Content operations

### `ppt_set_title`
Sets slide title.

### `ppt_add_text`
Adds text box with controlled placement.

### `ppt_add_bullets`
Adds structured bullet content.

### `ppt_add_table`
Adds a table from structured data.

### `ppt_add_image`
Adds an image artifact with explicit placement/crop behavior.

### `ppt_add_chart`
Creates a supported chart from structured series/categories.

### `ppt_add_kpi_card`
Creates a standardized KPI card component.

### `ppt_add_footer`
Adds controlled footer/page number/date if required.

## 3.4 Layout and styling

### `ppt_apply_layout`
Applies an approved layout/template.

### `ppt_format_shape`
Font/fill/line/alignment settings for supported shapes.

### `ppt_align_objects`
Align/distribute selected objects.

### `ppt_fit_text`
Adjusts text to avoid overflow using bounded rules.

### `ppt_apply_company_theme`
Applies a centrally defined company theme/template contract.

## 3.5 Presentation generation helpers

### `ppt_create_executive_summary`
Creates a concise executive-summary slide from supplied facts only.

### `ppt_create_data_story`
Builds a sequence of slides from an explicit outline and validated datasets.

### `ppt_create_report_deck`
Creates a report deck with title, KPI, trend, breakdown, findings, and next steps.

### `ppt_update_existing_deck`
Edits an existing deck while preserving unrelated slides and theme elements.

## 3.6 Validation and output

### `ppt_validate_presentation`
Checks:
- package integrity
- slide count
- no missing requested assets
- text overflow candidates
- invalid image references
- requested charts/tables created

### `ppt_save_as`
Saves a new `.pptx` artifact by default.

### `ppt_render_preview`
Optional future visual QA operation if a trusted renderer exists.

### `ppt_export_pdf`
Optional future operation.

## PowerPoint recommended first release

```text
ppt_inspect_presentation
ppt_read_slide
ppt_create_presentation
ppt_add_slide
ppt_duplicate_slide
ppt_reorder_slides
ppt_set_title
ppt_add_text
ppt_add_bullets
ppt_add_table
ppt_add_image
ppt_add_chart
ppt_fit_text
ppt_apply_company_theme
ppt_validate_presentation
ppt_save_as
```

---

# 4. Shared Office contracts

Future implementation should share a small set of provider-neutral contracts.

## `OfficeArtifactRef`

Suggested fields:

```text
ArtifactId
FileName
MimeType
SourcePathOrHandle
OutputPathOrHandle
Sha256
SizeBytes
CreatedUtc
```

Do not store AI provider credentials or provider-specific state in artifact metadata.

## `OfficeToolResult`

Suggested fields:

```text
Success
Operation
InputArtifact
OutputArtifact
Warnings[]
ValidationErrors[]
ChangedObjects[]
Summary
```

## `OfficeEditPolicy`

Recommended defaults:
- never overwrite original unless explicitly permitted
- output to controlled workspace
- validate extension/MIME
- cap input size
- reject password-protected/encrypted files unless a future approved flow exists
- reject macros in first version or strip/avoid macro-enabled output
- do not follow external links automatically
- do not execute embedded scripts/macros
- preserve unaffected content where technically possible
- fail closed on ambiguous sheet/slide/document target

---

# 5. Suggested future code layout

This is only a naming/layout proposal; nothing is implemented or referenced today.

```text
Core/Office/
  Common/
    OfficeArtifactRef.cs
    OfficeToolResult.cs
    OfficeEditPolicy.cs

  Excel/
    JarvisExcelInspector.cs
    JarvisExcelReader.cs
    JarvisExcelEditor.cs
    JarvisExcelFormatter.cs
    JarvisExcelAnalyzer.cs
    JarvisExcelChartBuilder.cs
    JarvisExcelValidator.cs

  Word/
    JarvisWordInspector.cs
    JarvisWordReader.cs
    JarvisWordEditor.cs
    JarvisWordFormatter.cs
    JarvisWordTemplateEngine.cs
    JarvisWordValidator.cs

  PowerPoint/
    JarvisPptInspector.cs
    JarvisPptReader.cs
    JarvisPptEditor.cs
    JarvisPptChartBuilder.cs
    JarvisPptLayoutEngine.cs
    JarvisPptValidator.cs
```

No class in this proposed layout should call an AI provider directly. AI execution remains in the
existing Jarvis provider-neutral agent/orchestration path.

---

# 6. Future task ownership proposal

This is intentionally not registered yet.

Likely ownership:

```text
Office data analysis / Excel       -> Atlas
Document/report construction       -> Atlas
Presentation construction          -> Atlas
Business-data acquisition          -> existing domain owner as today
Emailing an Office artifact        -> Echo after deterministic dependency binding
Soft1 write operations              -> existing registered domain owner
```

Jarvis remains user-facing and owns the execution graph. Agents do not call other agents.
Cross-task artifacts should be passed through deterministic Jarvis dependency binding, e.g.:

```text
ReportData.dataset
  -> ExcelWorkbook.source_data
  -> ExcelWorkbook.file_artifact
  -> SendEmail.artifact_reference
```

---

# 7. Recommended rollout order

## Phase A - Safe editing core

Excel:
- inspect/read/write/formulas/basic formatting/sheets/save/validate

Word:
- inspect/read/replace/paragraphs/headings/tables/styles/save/validate

PowerPoint:
- inspect/read/create/add slide/text/table/image/basic layout/save/validate

## Phase B - Business-quality output

Excel:
- tables/charts/conditional formatting/summaries

Word:
- templates/reports/page layout/headers-footers

PowerPoint:
- themes/charts/KPI cards/report decks

## Phase C - Advanced automation

- structured analytics
- company templates
- richer charting
- Office artifact chaining between Jarvis tasks
- render/preview QA when reliable engines are available

---

# 8. Explicit non-goals for the preparation document

This file does **not**:
- register new tasks
- register new tools
- change agent ownership
- alter provider/model routing
- change BOOT/HEALTH behavior
- add NuGet packages
- modify current XLSX/DOCX readers or writer
- change UI
- change Soft1 integration
- introduce Python/Docker requirements

It is a future toolbox contract only, ready to guide the next implementation phase.
