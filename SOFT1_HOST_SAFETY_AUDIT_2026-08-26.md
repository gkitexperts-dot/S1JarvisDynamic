# S1Jarvis — Soft1 Host Safety Audit (2026-08-26)

## Architectural rule

Soft1 is the host/integration boundary, not the Jarvis UI framework.

Allowed Soft1 responsibilities:
- provide the host panel/container where Jarvis and Licensing are embedded;
- provide the active `XSupport` / session context;
- execute Soft1 Object/business commands when Jarvis explicitly needs ERP work.

Jarvis-owned responsibilities:
- all visual UI inside Jarvis/Licensing;
- file selection, attachment UX, progress, errors and navigation;
- network/provider calls;
- local files and exports;
- any dialog that does not strictly require Soft1 itself.

Hard rules:
1. No `XSupport`, `XModule`, `XTable`, `GetSQLDataSet`, `ExecuteSQL`, `CreateModule`, `PostData` or other Soft1 SDK call inside `Task.Run` / thread-pool work.
2. No in-process modal `OpenFileDialog`, `SaveFileDialog`, `FolderBrowserDialog` or equivalent from a WebView callback. Historical testing already proved that modal dialogs can terminate the Delphi/Soft1 host with `EExternalException`.
3. UI failure must disable only the Jarvis feature; it must never terminate Soft1.
4. Keep exactly one primary `WebMessageReceived` router to avoid duplicate execution/reentrancy.

## Refactor completed in this pass

### DR file selection

The DR curtain no longer relies on Chromium/WebView2 `<input type="file">` as the effective picker path.

`JarvisShell.IsolatedFilePicker.cs` injects a capture-phase UI interception layer and routes the DR browse action to `https://s1jarvis-picker.local/pick`. The C# bridge launches the Windows file picker in a **separate PowerShell STA process**, so the modal window is not created inside the Soft1 process/message loop. Selected files are returned to Jarvis and reconstructed as browser `File` objects for the existing DR pipeline.

Drag/drop remains browser-local and does not open a native modal dialog.

### Main paperclip / attachment icon

The same isolated picker is used by the main composer paperclip. The old hidden `#fileInput` can remain in HTML for compatibility, but capture-phase interception prevents it from opening the WebView2 native picker during normal Jarvis use.

### Duplicate message-router protection

`JarvisShell.HostSafety.cs` re-enforces the synchronous DR router after all `Loaded` handlers complete. This fixes the observed condition where `CoreWebView2InitializationCompleted` installed the DR router first and `JarvisShell_Loaded` later subscribed the legacy async router again, causing the DR entitlement path to execute twice.

Expected log after the fix:

```text
[host-safety] single primary WebMessageReceived router enforced; legacy duplicate removed.
```

A single DR open should then produce only one entitlement sequence.

## Audit findings — remaining risky Soft1/thread-pool crossings

The following existing paths still use `Task.Run` around code that touches `_xSupport` or Soft1-backed helpers and should be refactored next.

### Main Jarvis boot

`CoreWebView2_NavigationCompleted`:
- `JarvisLicenseGuard.CheckAccessSilent(_xSupport)` currently runs in `Task.Run`.
- Move the Soft1/session-dependent portion to the Soft1 integration thread. If the remote Verilic HTTP check needs background work, split the method so only pure network work is asynchronous.

### Dashboard curtain

`HandleDashboardQueryAsync`:
- `DashboardPanels.BuildDashboardText(_xSupport, date)` runs in `Task.Run`.
- This method executes `GetSQLDataSet` for configured dashboard SQL and is therefore unsafe on the thread pool.
- Refactor to synchronous Soft1 data acquisition. Any later formatting can be moved off-thread only after all `XTable` data has been copied into plain CLR objects.

### Email / Calendar curtain

Calendar load:
- `JarvisTools.GetSoactionCalendarEntries(_xSupport, ...)` runs in `Task.Run`.
- Soft1 calendar acquisition must stay on the Soft1 integration thread.
- Microsoft Graph/HTTP calls may remain asynchronous because they do not use Soft1 SDK objects.

### Courier curtain

Remaining risky calls:
- `JarvisLicenseGuard.CheckAccessSilent(_xSupport, AccessConfig.CourierToolName)` in `Task.Run`;
- `JarvisCourier.BuildRequestFromFindoc(_xSupport, findocId)` in `Task.Run`;
- `JarvisCourier.LoadActiveProviders(_xSupport)` in `Task.Run`.

All Soft1 reads must be synchronous. External courier HTTP calls can remain asynchronous after the Soft1 request object has been fully materialized into plain data.

### DR legacy handlers

Although the primary DR recognition/registration router is now synchronous, older fallback methods in `JarvisShell.xaml.cs` still contain thread-pool Soft1 calls, including:
- old `HandleDrStartAsync` entitlement path;
- AADE/Soft1 trader lookup helpers using `_xSupport` in `Task.Run`;
- independent CREATEAADEAFM entitlement check in `Task.Run`;
- company AFM read through `_xSupport` in `Task.Run` before document extraction.

These should either be deleted after routing consolidation or split into synchronous Soft1 acquisition + asynchronous external HTTP/AI work.

## Safe asynchronous work

`Task.Run`/async is acceptable for work that has no Soft1 object dependency, for example:
- pure file parsing after bytes have already been copied out of the host boundary;
- PDF/Office parsing on byte arrays;
- HTTP calls to Verilic, Graph, courier providers or AI providers;
- CPU-only transformations on plain CLR/JSON objects.

The boundary is data ownership: once data is copied from `XSupport`/`XTable` into ordinary CLR values, background work is safe.

## UI curtain review

### DR
- File picker: refactored to external isolated process.
- Soft1 registration/mapping: primary router synchronous.
- Remaining legacy async Soft1 paths: must be removed/refactored.

### Main chat
- Paperclip picker: refactored to external isolated process.
- Office parsing from already-loaded bytes: safe to remain background work.

### Dashboard
- No modal file UI found.
- SQL loading via `Task.Run + XSupport`: unsafe, priority refactor.

### Email/Calendar
- No in-process file picker identified in the reviewed curtain path.
- Soft1 calendar query via `Task.Run`: unsafe.
- Graph calls: safe asynchronous work.

### Courier
- No file picker identified in the reviewed curtain path.
- Soft1 licence/document/provider reads via `Task.Run`: unsafe.

### Browser / Help
- No Soft1-native modal file picker identified in the reviewed paths.
- Continue to keep browser UI and network work independent from Soft1 SDK objects.

## Next implementation order

1. Verify isolated DR picker on the clean machine and confirm the `EExternalException 80000003` is gone while browsing folders.
2. Verify main paperclip uses the isolated picker and does not invoke Chromium's native file picker.
3. Refactor Dashboard `Task.Run + XSupport`.
4. Refactor Email/Calendar Soft1 reads.
5. Refactor Courier entitlement/data reads.
6. Remove or neutralize legacy DR handlers that can still perform Soft1 work off-thread.
7. Refactor main Jarvis licence boot so Soft1 context is never accessed from the thread pool.
