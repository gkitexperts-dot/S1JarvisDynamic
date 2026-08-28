# Milestone — Embedded Jarvis Courier

**Date:** 26/08/2026  
**Repository:** `S1JarvisDynamic`  
**Branch:** `feature/multi-ai-provider`

## Goal

Remove the runtime dependency of Jarvis on the standalone `S1Courier.dll`, while keeping Courier as an independently licensed capability through `JARVISCOURIER`.

The main production problem was coexistence on Soft1 installations that already had the standalone S1Courier product. Jarvis also referenced `S1Courier.dll`, which could lead to duplicate/conflicting assembly loading during Soft1 startup.

## Final architecture

Courier functionality is now physically embedded in `S1Jarvis.dll` and owned by the Jarvis codebase.

Native providers:

- `JarvisCourierCenterProvider`
- `JarvisEltaCourierProvider`
- `JarvisAcsCourierProvider`
- `JarvisGenikiCourierProvider`

Shared Jarvis-owned contracts and infrastructure:

- `IJarvisCourierProvider`
- `JarvisCourierProviderConfig`
- `JarvisCourierShipmentRequest`
- `JarvisCourierShipmentResult`
- `JarvisCourierCancelResult`
- `JarvisCourierTrackingResult`
- `JarvisCourierProviderFactory`
- `JarvisCourierPdfHelper`

The provider factory routes directly to the four Jarvis-native implementations. There is no `S1Courier.dll` reference in `S1Jarvis.csproj`.

## Licensing boundary

The physical implementation moved into `S1Jarvis.dll`, but the commercial capability remains independent.

`JARVISCOURIER` continues to be checked through the existing Verilic/Jarvis licensing path before Courier functionality is made available. No Verilic publish or licensing-model change was required for this migration.

This gives us:

- one Jarvis runtime assembly,
- independent Courier entitlement,
- no dependency on the standalone Courier product,
- ability for standalone `S1Courier.dll` and `S1Jarvis.dll` to coexist without Jarvis loading S1Courier as a dependency.

## Provider behavior preserved

### ACS Courier

Native support includes voucher creation, cancellation, voucher PDF retrieval, multipart voucher handling, tracking, COD options, Saturday delivery and appointment-until-time behavior.

### ELTA Courier

Native support includes authorization/APIKEY handling, voucher creation, cancellation, PDF retrieval and tracking.

### Courier Center

Native support includes direct Courier Center API operations for creation, cancellation, voucher retrieval, tracking and delivery-days lookup.

### Geniki Taxydromiki

Native support includes SOAP authentication, `CreateJob`, `CancelJob`, `GetVouchersPdf`, `TrackAndTrace` and preservation of `ProviderJobId` for cancellation.

## Runtime validation completed

The migration was validated on a real Soft1 runtime.

1. `S1Courier.dll` was removed/renamed from the Soft1 runtime path.
2. Soft1 started normally.
3. The Jarvis Courier curtain opened normally.
4. Jarvis located sales document `ΠΡΓΕ00000245`.
5. Jarvis loaded the ACS configuration and requested user confirmation.
6. ACS voucher `9801436945` was created successfully.
7. The voucher PDF was returned through the Jarvis flow.
8. Jarvis rediscovered the active shipment from the Soft1 document.
9. The same ACS shipment was cancelled successfully after explicit user confirmation.

This validates the end-to-end path:

`Soft1 → Jarvis → JARVISCOURIER entitlement → native provider → Courier API → Soft1 document update`

without the standalone `S1Courier.dll` being present.

## Cleanup completed

- Removed the `S1Courier.dll` project reference.
- Replaced standalone provider execution with four Jarvis-native implementations.
- Added Jarvis-prefixed provider and contract names so the embedded implementation is clearly distinct from the standalone product.
- Added Jarvis-owned PDF helper support.
- Renamed the final provider factory source to `JarvisCourierProviderFactory.cs`.
- Removed the obsolete `JarvisCourierLegacyAdapter.cs` filename.
- Kept the existing `JarvisCourier` orchestration/UI/chat behavior unchanged during the migration to reduce regression risk.

`JarvisCourierCompatibility.cs` is now only an **internal source-level orchestration bridge inside S1Jarvis.dll**. It does not reference or load the standalone assembly. A later cosmetic refactor may replace the old orchestration type names directly inside the large `JarvisCourier.cs`; this is not required for runtime independence and is intentionally separated from this validated milestone.

## WebView2 / Application Server deployment hardening

A second deployment conflict was identified during the same milestone. Soft1 already owns and deploys its WebView2 assemblies, while the Jarvis project previously referenced the `Microsoft.Web.WebView2` NuGet package directly. A normal Jarvis compile could therefore copy a different WebView2 build into `Soft1Core`.

On installations using Soft1 Application Server this produced binary drift: the Application Server detected a difference in the WebView2 file and refreshed/downloaded the server-approved copy to the terminal.

The Jarvis project was changed so that WebView2 is now treated as a **host-owned dependency**:

- the `Microsoft.Web.WebView2` PackageReference was removed from Jarvis,
- Jarvis compiles against `Microsoft.Web.WebView2.Core.dll` supplied by `Soft1Core`,
- Jarvis compiles against `Microsoft.Web.WebView2.Wpf.dll` supplied by `Soft1Core`,
- both references use `Private=False` / Copy Local disabled,
- Jarvis no longer deploys or overwrites the Soft1-owned WebView2 assemblies during build.

### Application Server smoke test

The isolation was validated with a real Application Server workflow:

1. The client first connected through Application Server and Soft1 refreshed the WebView2 file to the server-approved version.
2. Jarvis was then compiled locally.
3. Soft1 was started locally and worked normally.
4. The same terminal connected again through Application Server.
5. No second WebView2 refresh occurred.

This demonstrates that a Jarvis compile no longer introduces WebView2 binary drift after the server/client copy has been synchronized.

Deployment rule: **WebView2 belongs to the Soft1 host environment; Jarvis consumes it but does not deploy it.** This is especially important for Application Server installations where server-side file signatures/versions are authoritative.

## Deployment / coexistence rule

For new Jarvis deployments, `S1Courier.dll` is **not a Jarvis prerequisite**.

If a customer owns the standalone S1Courier product, its DLL may remain installed for that standalone product. Jarvis must continue to function independently from it.

Similarly, WebView2 is **not a Jarvis-owned deployment artifact**. The WebView2 assemblies already supplied by Soft1/Application Server must remain authoritative.

Before broad rollout, perform one dedicated coexistence smoke test with both the standalone S1Courier product and the new Jarvis active in the same Soft1 installation. The critical Jarvis-without-S1Courier runtime test and the Application Server WebView2 isolation test have already passed.

## Milestone status

**COMPLETE — runtime independence and Application Server dependency isolation validated.**

The remaining standalone-S1Courier coexistence smoke test is a deployment validation, not a blocker for the embedded Courier architecture.
