# Future External Tool Adapter Architecture

Status: **Blueprint only / not wired into runtime**

Purpose: define a safe, provider-neutral way for Jarvis to adopt external capabilities from GitHub/open source ecosystems without letting third-party code bypass Jarvis orchestration, task ownership, tool policies, Soft1 safety, or provider neutrality.

Primary first use case: **web scraping / structured extraction from the Browser curtain**.

This document is preparation for a future version only. It does not register tools, change tasks, modify routing, add packages, or introduce a runtime dependency.

---

# 1. Core principle

External capabilities must enter Jarvis through an adapter and become normal Jarvis tools.

```text
External capability
    ↓
Jarvis Adapter
    ↓
Jarvis Tool Contract
    ↓
ToolRegistry / TaskRegistry
    ↓
Owner Agent
    ↓
Jarvis orchestration
```

Jarvis remains the orchestration authority.

An external library, scraper, MCP server, Python package, Node package, C# library, or HTTP service must never become a second autonomous orchestration layer inside Jarvis.

Do not design:

```text
Jarvis -> external agent -> external agent decides tools/routing -> Soft1
```

Prefer:

```text
Jarvis -> registered task -> owner agent -> scoped Jarvis tool -> adapter -> external engine
```

---

# 2. Browser and scraping use case

The Browser curtain already separates navigation/presentation from reasoning.

Future scraping should preserve that separation:

```text
User
  ↓
Browser Curtain
  ↓
open_url / browser navigation
  ↓
Jarvis Browser Task
  ↓
Jarvis Scraper Adapter
  ↓
Scraping Provider
  ↓
Structured Result
  ↓
Atlas/Jarvis analysis
```

Important distinction:

```text
Browser navigation != scraping engine
```

WebView2 can remain the visible browser for the operator.
A scraper may use the current page, a URL, DOM content, a headless browser, an HTTP client, or another approved extraction engine depending on implementation.

The rest of Jarvis should not care which engine was used.

---

# 3. Proposed neutral abstraction

Future code concept:

```csharp
internal interface IJarvisExternalToolProvider
{
    string ProviderId { get; }
    string Capability { get; }
    JarvisExternalToolResult Execute(JarvisExternalToolRequest request);
}
```

For scraping specifically:

```csharp
internal interface IJarvisScraperProvider
{
    string ProviderId { get; }

    JarvisScrapeResult ScrapePage(JarvisScrapeRequest request);
    JarvisScrapeResult ScrapeLinks(JarvisScrapeRequest request);
    JarvisScrapeResult ScrapeProducts(JarvisScrapeRequest request);
    JarvisScrapeResult ScrapeTable(JarvisScrapeRequest request);
}
```

Possible implementations later:

```text
IJarvisScraperProvider
  ├── WebViewDomScraperProvider
  ├── LocalHtmlScraperProvider
  ├── PlaywrightScraperProvider
  ├── ExternalHttpScraperProvider
  └── McpScraperProvider
```

Jarvis business/orchestration code must depend on the interface/contract, not on a vendor library.

---

# 4. Proposed Jarvis scraping tools

Potential future tools:

```text
scrape_page
scrape_links
scrape_products
scrape_table
scrape_category
```

These are logical Jarvis tools. They are not names of third-party libraries.

Example responsibilities:

## `scrape_page`

Input:

```text
url/current-page reference
optional extraction instruction
maxItems
```

Output:

```text
pageTitle
canonicalUrl
textSections[]
links[]
structuredData[]
warnings[]
```

## `scrape_products`

Output should prefer structured items:

```json
{
  "items": [
    {
      "name": "...",
      "url": "...",
      "price": 0,
      "currency": "EUR",
      "availability": "...",
      "category": "...",
      "attributes": {}
    }
  ],
  "truncated": false,
  "warnings": []
}
```

## `scrape_table`

Prefer the same general shape already familiar to Jarvis from `query_data` / page table extraction:

```json
{
  "columns": ["..."],
  "rows": [
    {"...": "..."}
  ],
  "totalRowCount": 0,
  "truncated": false
}
```

The LLM decides **what it wants extracted**.
The deterministic provider decides **how extraction is performed**.

---

# 5. Structured result contract

External tools should return bounded, deterministic, structured results.

Suggested common envelope:

```text
JarvisExternalToolResult
- Success
- ProviderId
- Capability
- Source
- Data
- Warnings[]
- Truncated
- DurationMs
- ArtifactRefs[]
```

For scraper results:

```text
JarvisScrapeResult
- SourceUrl
- FinalUrl
- PageTitle
- Items[]
- Links[]
- Tables[]
- TextSections[]
- Pagination
- Truncated
- Warnings[]
```

Do not let external providers return arbitrary instructions that can alter Jarvis routing or policy.

External output is data, not authority.

---

# 6. Security boundary

Third-party capability code must be treated as untrusted until approved.

Minimum future controls:

```text
allowed domains
max pages
max crawl depth
request timeout
execution timeout
rate limit
response-size limit
memory limit
CPU limit
network policy
filesystem policy
process policy
```

Default stance:

```text
network: only what the capability needs
filesystem: temporary workspace only
Soft1 access: none by default
credentials: none by default
arbitrary process execution: denied by default
```

A scraper should not automatically receive:

- Soft1 database credentials
- Verilic/provider credentials
- Outlook credentials/tokens
- unrestricted local filesystem access
- arbitrary shell access
- write tools

---

# 7. Soft1 safety rule

Community/external tools must never get implicit Soft1 write capability.

Example:

```text
community skill asks for create_order
```

This is only a declared requirement.
It does not grant permission.

The existing Jarvis authorities remain authoritative:

```text
JarvisTaskRegistry
JarvisToolRegistry
confirmation policy
authorization policy
company/document scope validators
```

External code cannot bypass them.

---

# 8. Provider-neutral design

Do not create architecture tied to:

```text
ClaudeScraper
OpenAIScraper
GeminiScraper
```

Use logical capability names:

```text
Scraping
BrowserExtraction
DocumentConversion
Forecasting
OCR
OfficeRendering
```

Provider-specific behavior belongs only in adapters.

Example:

```text
JarvisScraperAdapter
   ├── Provider A
   ├── Provider B
   └── Provider C
```

The rest of Jarvis should see the same request/result contract.

---

# 9. External package types Jarvis could support later

The adapter model can cover more than scraping.

## Native library

```text
C# assembly / local library
```

Wrapped behind a Jarvis adapter.

## External local process

```text
Python
Node.js
other CLI
```

Runs only in an isolated worker/sandbox with bounded input/output.

## HTTP service

```text
local service
on-prem service
approved cloud API
```

Adapter handles transport and converts responses into Jarvis contracts.

## MCP server

```text
MCP tool definitions
    ↓
Jarvis MCP Adapter
    ↓
Jarvis Tool Contract
```

MCP should be treated as a transport/provider mechanism, not as the orchestration authority.

## Instructions-only skill

Some GitHub skills contain no runtime code.
They may provide:

```text
instructions
knowledge
examples
required tool names
```

These can be adapted into Jarvis knowledge/training context without executing third-party code.

---

# 10. Future skill package model

Potential Jarvis package structure:

```text
Skills/
  WebProductResearch/
    skill.json
    SKILL.md
    examples/
    tests/

  FinancialAnalysis/
    skill.json
    SKILL.md
    examples/
    tests/
```

Example manifest:

```json
{
  "id": "web_product_research",
  "name": "Web Product Research",
  "version": "1.0",
  "ownerAgent": "Atlas",
  "domains": ["Browser", "Research"],
  "requiresTools": ["scrape_products"],
  "execution": "instructions-only",
  "network": "approved-domains"
}
```

A package may declare requirements, but Jarvis decides whether those requirements are available/allowed.

---

# 11. Import / approval flow

Never auto-install random GitHub repositories directly into the live Jarvis runtime.

Recommended future process:

```text
Discover
   ↓
Download
   ↓
Inspect
   ↓
License check
   ↓
Dependency scan
   ↓
Security review
   ↓
Compatibility analysis
   ↓
Jarvis adapter / manifest generation
   ↓
Admin approval
   ↓
Test sandbox
   ↓
Register in Jarvis
```

No external package should become active because an AI model decided to install it.

---

# 12. Suggested compatibility classification

When evaluating a GitHub project, classify it as one of:

```text
A. Instructions-only skill
B. Pure library
C. Local executable/service
D. MCP server
E. Full agent/framework
```

Preferred compatibility order:

```text
A -> easiest / lowest risk
B -> good candidate for adapter
C -> usable with sandbox/service boundary
D -> usable through MCP adapter
E -> extract capability only; do not embed its orchestration
```

For a full external agent/framework, prefer taking its useful engine/library and exposing it as a Jarvis tool instead of nesting the whole agent.

---

# 13. Browser curtain integration

Possible future Browser task flow:

```text
User: "Βρες μου όλα τα προϊόντα αυτής της κατηγορίας κάτω από 50€"

Jarvis
  ↓
Browser task
  ↓
open_url
  ↓
scrape_products
  ↓
structured product list
  ↓
Atlas analysis/filter/compare
  ↓
optional table/chart/export
```

For sites where current DOM/table extraction is enough, Jarvis should use existing lightweight tools.

A heavier scraper should only be used when needed:

```text
simple visible text -> read_page_content
real HTML table -> extract_page_tables
complex product/category page -> scrape_products
multi-page crawl -> approved scraper provider
```

This avoids unnecessary complexity and cost.

---

# 14. Suggested future code layout

Preparation concept only:

```text
Core/ExternalTools/
  Contracts/
    JarvisExternalToolRequest.cs
    JarvisExternalToolResult.cs

  Scraping/
    IJarvisScraperProvider.cs
    JarvisScrapeRequest.cs
    JarvisScrapeResult.cs
    JarvisScraperCoordinator.cs
    WebViewDomScraperProvider.cs

  Mcp/
    JarvisMcpToolAdapter.cs

  Sandbox/
    JarvisExternalProcessHost.cs
    JarvisExternalToolPolicy.cs
```

Do not add these files to the current project until an implementation phase is explicitly started.

---

# 15. Ownership and orchestration

No new AI owner agent is required for this architecture.

Likely ownership by task:

```text
Atlas -> research, comparison, analysis, scraping-heavy data tasks
Scout -> internet research tasks where already appropriate
Jarvis -> orchestration, validation, dependencies, presentation
```

The external engine is never an agent.

It is a capability provider behind a registered tool.

---

# 16. Failure behavior

External tool failure should be explicit and bounded.

Example result:

```json
{
  "success": false,
  "providerId": "playwright-local",
  "capability": "scrape_products",
  "warnings": ["Page requires authentication"],
  "truncated": false
}
```

Jarvis decides whether to:

- retry with another approved provider
- fall back to `read_page_content`
- ask the user to navigate/login manually
- stop with a clear error

The provider itself does not decide the next orchestration step.

---

# 17. Observability

Future adapters should expose operational metadata without leaking secrets:

```text
provider id
capability
duration
item count
truncated flag
warning codes
failure category
```

Never log:

```text
credentials
session cookies
auth tokens
private page contents beyond approved diagnostics
```

---

# 18. First practical implementation target

When implementation begins, the safest first version would be:

```text
Phase A
- define neutral scraper contracts
- implement provider over current WebView DOM/current page
- add scrape_links / scrape_products-like structured extraction
- no external process yet

Phase B
- add one approved external scraper provider
- sandbox/timeouts/domain policy
- provider fallback

Phase C
- optional MCP adapter
- package/skill manifest support
- admin import/approval workflow
```

This lets Jarvis gain the architecture without immediately taking on arbitrary external-code execution risk.

---

# 19. Non-goals for this preparation document

This blueprint does **not**:

- register any new tool
- modify `JarvisToolRegistry`
- modify `JarvisTaskRegistry`
- change owner agents
- change Browser curtain behavior
- add NuGet/npm/pip dependencies
- add MCP runtime support
- execute external processes
- grant network permissions
- grant Soft1 access to third-party code
- alter provider/model routing

It is only a future architecture reference.
