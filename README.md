# Disorderly

Disorderly is a Windows utility intended to combine two complementary capabilities:

- A user-configurable contextual command/hotkey launcher for navigating shortcuts, selecting commands and entering arguments.
- Incremental, interruption-friendly file organization that can pause for decisions and resume safely.

## Architecture and Design

The authoritative specification is [Architecture and Design Decisions](docs/design-decisions.md). It records the settled contracts, implementation boundaries, glossary and an illustrative checkpoint roadmap.

The key invariants are:

- Exactly one global activation hotkey; all contextual bindings operate inside Disorderly's focused interface.
- Explicit context transitions with strict local bindings, stable identities and a navigation stack recording the actual path.
- Every hidden activation opens at Root. Resume suggestions restore drafts only on explicit selection, without executing them.
- Invocation drafts and accepted operations have different lifecycles; dismissing UI does not inherently cancel unfinished work.
- Initially, only one file-mutating step executes globally. Manual Resume receives bounded priority with fairness; future independent-resource parallelism must preserve the same safety contracts.
- Awaiting Input stops the entire operation. Cancellation preserves completed changes; Undo is a separate operation.
- Revalidate filesystem identity and state at the point of use and across waits; use appropriate atomic/handle-based safeguards and verify actual outcomes.
- Durable operation intent belongs in a SQLite journal, distinct from diagnostics. Uncertain outcomes require reconciliation; mutating work requires explicit Resume after restart.

**Implemented today — Foundation I:** A tray-resident .NET 10 WPF application with a compact root palette, provisional **Ctrl+Shift+D** activation, **Open Command Palette** in the tray menu, focus/dismissal handling and per-monitor DPI-aware positioning.

**Implemented — Context Routing I:** A UI-independent engine in `Disorderly/Routing` with routing domain models, context-local bindings, a reusable acyclic graph, navigation stack, structured validation, immutable configuration snapshots and typed routing outcomes. It can be exercised directly in NUnit without windows or native hotkeys.

**Implemented — Command Model I:** A UI-independent command catalog with reusable definitions, typed required/optional parameters and arguments, binding presets, completion policies, icon metadata, immutable invocation/attempt contracts and compatible routing/catalog snapshots. Validation can prepare an execution-ready request, with executable behavior supplied separately by Command Execution I.

**Implemented — Palette Interaction I:** The reusable WPF palette now owns a fresh compatible routing/command interaction on each activation. It displays the active context, actual breadcrumb and local bindings, navigates with contextual gestures and Escape, and previews command definitions/argument readiness without executing them. Visible interactions retain their original snapshot. The startup fallback is an empty Root/catalog; User Configuration I can replace it with a validated user file. Graphical editing remains deferred.

**Implemented — Command Execution I:** Commands may opt into `LaunchExecutable` through a declared required Path parameter. An explicit Run/Retry control submits an existing immutable invocation through an identified attempt and a deduplicating coordinator. The executable launches directly, with no shell or extra arguments. Outcomes distinguish success, pre-execution rejection, known non-start failure and uncertainty. Successful completion applies the retained invocation policy; errors preserve the selection, and uncertainty offers no blind retry. Without a user file, startup retains an empty Root/catalog; graphical command creation/configuration is not available.

**Implemented — Argument Editing I:** The command preview now displays fields in parameter declaration order. Text, Path and signed Integer use text input with explicit supplied/absent state; Boolean offers Not supplied / False / True. Valid edits preserve invocation identity, origin, completion policy and catalog. Invalid raw input stays visible and blocks Run independently of typed-argument readiness. Binding presets are displayed unchanged and read-only in this initial editor; broader preset editing/override policy is still deferred.

Meaningful unfinished edits are retained in a multiple-entry **in-memory draft store**, including invalid raw text. Navigation, selection-only previews and successful executions do not create drafts. Every new activation still starts at Root. **Implemented — Draft Management I:** Root offers the newest compatible unfinished command without exposing argument values. Unmodified Tab on the contextual Root surface resumes it only when no explicit Root Tab binding claims that gesture. Resume and Browse drafts actions are keyboard accessible; the browser lists independent drafts newest-first with explicit Resume/Discard. Restoring reuses the original invocation, typed/raw input, presets, retry provenance, actual context path and saved editing position, with fresh editor-event identity. Restoration never launches anything. Nothing is saved across restart; the fallback configuration remains empty.

**Implemented — Operation Infrastructure I:** `Disorderly/Operations` is a UI-independent, process-local foundation for accepted simulated work. Typed operation, step and input-request IDs are distinct from invocation/attempt identities. Accepted work begins Queued; explicit validated transitions cover Running, Pausing, Paused, AwaitingInput, CancellationRequested, Succeeded, Failed, Canceled and NeedsRecovery. Immutable records and Activity projections retain factual verified-step counts, optional totals, input requests, diagnostic status and process-lifetime history.

`OperationCoordinator` combines the volatile store and explicit scheduler; `AcceptSimulation` requires an injected side-effect-free/simulated runner. There is no production operation runner, background worker loop, app-startup registration or new command execution kind. Direct executable launching and command draft Resume retain their existing independent paths. The next intended workstream is **User Configuration I**, using the existing validated command/context/binding/preset snapshots; it is not implemented here.

**Implemented — User Configuration I:** UTF-8 JSON version 1 can now define contexts, bindings, reusable command definitions and typed presets. Startup loads `%APPDATA%\Disorderly\config.json` into the palette's shared validated publication store. The tray provides **Reload Configuration** and a non-modal status line. Invalid/missing files retain the last successful publication; active interactions and drafts keep their existing interpretation. A programmatic revision-checked Save API stages, flushes and replaces the complete file before publication. There is no graphical editor, automatic file watcher or configuration migration.

**Planned, not implemented:** Preset editing/overrides, parameter pills, guided prompting, autocomplete/path completion, typed command search, draft persistence, graphical configuration UI, migrations and automatic watching, additional execution kinds and argument vectors, scripts/shell commands, Activity UI, production operations and their command handlers, SQLite journaling/recovery, file mutation/Undo, icon extraction/customization and durable execution deduplication across crashes.

## Build and automated validation

On Windows with the .NET 10 SDK:

```powershell
dotnet restore Disorderly.slnx
dotnet build Disorderly.slnx --configuration Release --no-restore
dotnet test Disorderly.slnx --configuration Release --no-build --no-restore
```

The existing Windows GitHub Actions workflow runs restore, Release build and NUnit tests on push. Tests cover registration ownership/failure, palette lifecycle with a fake view, physical placement at multiple scales/origins, hidden-versus-visible native operation ordering, monitor-relative maximize bounds, and the existing MainWindow construction test. Fake registrations never reserve a real global hotkey. Interactive behavior must be verified separately.

## Implementation notes

- `ReactiveUI.Primitives.ObservableEvents` is pinned to **9.0.0**, preserving `PrivateAssets="all"`. NuGet's exact-package flat-container index lists both 8.4.0 and 9.0.0; 9.0.0 is the latest stable listed version at implementation time (published October 3, 2026). No unrelated dependency versions changed.
- `app.manifest` establishes PerMonitorV2 awareness before WPF initialization, with PerMonitor fallback. WPF owns DPI scaling and WM_DPICHANGED processing.
- Desktop monitor/work rectangles and native window placement use physical pixels, including negative virtual coordinates. Only DIP dimensions, pointer anchors and margins are scaled. Palette positioning first moves its hidden HWND to the selected monitor, reads that window's DPI, measures WPF content, clamps dimensions, then anchors to the work area's upper-right corner. Each activation reselects the foreground application's monitor; cursor and primary-monitor fallbacks never depend on the palette's previous location. Visible palettes re-anchor on DPI/display/work-area changes using their current HWND DPI after WPF processes the notification. Only hidden windows use temporary DPI preparation; activation applies final bounds before Show, with no second placement afterward. Content is measured in DIPs and native sizing is applied once to the final rectangle (SizeToContent is Manual to avoid competing automatic resizes).
- Custom desktop windows use monitor-relative native maximize bounds, supported visual DPI, and physical drag positioning. No reflected SystemParameters DPI or hardcoded DPI padding tables remain. WM_GETMINMAXINFO reads fresh monitor-relative work bounds on native sizing requests; DPI/display changes are left to Windows/WPF without artificial restore/maximize transitions or queued refresh callbacks.
- A dedicated message-only HWND receives WM_HOTKEY on the WPF dispatcher. Only Ctrl+Shift+D is registered, with MOD_NOREPEAT. The registration and hook are released on shutdown. Conflicts are logged without retries or fallback hooks; the tray remains available.
- One controller owns one reusable lightweight palette window. Every hidden-to-open transition resets root presentation; repeated activation toggles closed. Escape and external focus loss dismiss without exiting. Owned windows/popups are allowed to take focus. Visibility remains separate from routing and editing state; Resume appears only for real suspended work.
- Foreground activation is requested through normal Windows/WPF APIs. If Windows refuses focus, the palette logs the failure and hides; the next explicit activation may retry. No input injection or foreground-lock bypass is used.

## Manual verification checklist (Windows required)

These interactive checks cannot be established by the automated tests or a noninteractive Linux build environment. Run them on Windows; do not treat CI success as UI verification.

- [ ] Launch Disorderly: tray icon appears, no management window opens. Dismissing the palette leaves the process/tray running.
- [ ] In Explorer press Ctrl+Shift+D: palette opens at root and its input has keyboard focus without clicking. Press the hotkey again: palette closes. Hold the combination: no repeated toggle from key repeat.
- [ ] With the palette hidden, ordinary typing and shortcuts in Explorer, VS Code and other applications are unaffected.
- [ ] Open, press Escape, reopen: root is shown. Open, switch/click into another application: palette disappears. Reopen: root is shown again.
- [ ] Use the tray's Open Command Palette entry, including after a failed hotkey registration. Verify focus is acquired and Escape works.
- [ ] Activate from applications on primary and secondary monitors; verify the palette uses that application's monitor, remains entirely inside its working area and respects taskbars on different edges.
- [ ] Repeat at 100%, 150% and 200% scaling, including monitors left of and above primary. Check size, sharpness and margins; move a custom desktop window between monitors and test drag, resize, maximize, restore and drag from maximized.
- [ ] Change scaling/resolution, move the taskbar or disconnect a monitor with the palette open and hidden; verify visible placement recovers and subsequent activations reselect/recompute correctly.
- [ ] Start a second Disorderly instance (or reserve Ctrl+Shift+D with a test utility) to provoke a conflict: check the useful error in the application logs, continued tray operation, and tray-based activation. Exit both instances and relaunch to verify cleanup/re-registration.
- [ ] Exit through the tray: process ends and registration is released. Closing/dismissing only the palette must never exit.
- [ ] Where Windows foreground restrictions apply, verify a refused activation logs and dismisses cleanly, and a later explicit activation works.

### Surgical windowing correction checks

These require actual Windows desktop interaction and have not been performed in the implementation environment. Geometry unit tests do not prove native maximize/restore behavior or absence of visible flicker.

- [ ] Activate on a 100% monitor, dismiss, then activate from an application on a 150% monitor; repeat in reverse, including monitors above/left of primary. Check upper-right placement, margins, focus and dismissal.
- [ ] While the palette is visible, change resolution/scaling and taskbar placement/work area. It must move/resize directly without a 1-by-1 collapse, upper-left jump or unnecessary flicker. Disconnect a monitor, then activate again and check recovery with no normal-transition errors.
- [ ] With a BorderlessWindow maximized, change resolution/scaling and work area. Verify it remains maximized without an artificial StateChanged sequence; restore and compare normal bounds with the original position/size. This class has no production management-window entry point yet; use a local development host to exercise it without expanding the application UI.
- [ ] Move that window between differently scaled monitors, repeat maximize/restore, and drag from maximized. Check resize, custom buttons and system menu behavior, with no restore-geometry corruption.


### Palette Interaction I — manual Windows checks

These require a real Windows desktop and have **not** been performed in the implementation environment. CI does not establish real keyboard-layout, focus or multi-monitor correctness. Keep the Foundation I checks above.

Use a local development test host that constructs `CommandPaletteController(new CommandPaletteWindow(), store)`. Create the store with `PaletteConfiguration.CreateStore()` and publish an in-memory fixture equivalent to `PaletteInteractionTests.Configuration`: Root D → Development, W → Work; both enter shared Servers through S; Development C → a command with required arguments/presets. Do not change ordinary production startup or add permanent sample commands. The NUnit fixture is an example for the test host, not a shipped configuration.

- [ ] Ctrl+Shift+D opens Root; D/S navigate and update title, actual breadcrumb and local bindings. Escape returns through Development; repeat through Work and verify the shared Servers context returns through Work. Escape at Root dismisses.
- [ ] Backspace and unassigned keys leave the context unchanged. Parent shortcuts are absent and do not resolve in children. No implicit free-text search starts.
- [ ] C in Development displays the correct name, description and incomplete argument status/presets. A complete command without an execution specification also says execution is unavailable. Selection alone never launches, opens, submits an attempt or applies completion policy. Subsequent shortcuts still resolve in Development.
- [ ] Global activation toggles closed from nested contexts. External focus loss dismisses immediately. Reopening starts at Root with no old selection. Keyboard input in Explorer/VS Code is unaffected while hidden; exactly one global registration remains.
- [ ] Publish a replacement fixture while visible, assigning the same gesture a different destination. The open interaction keeps its meaning; a new activation uses the replacement. Invalid replacements preserve the valid configuration.
- [ ] Test letters, digits, named keys, Ctrl/Shift/Alt combinations and shifted punctuation with each relevant installed layout (for example US and German). Confirm punctuation follows effective layout rather than US key positions. Holding keys does not repeat navigation/selection; key and text events never double-dispatch.
- [ ] Test dead-key composition, IME composition/cancellation and AltGr. Ambiguous/composition/AltGr input intentionally does not route. Escape consumed by composition cancellation must not also navigate. Test again after dismiss/reopen.
- [ ] With a long binding list and command preview, verify bounded scrolling, usable keyboard focus and readable labels. Navigate between short/long contexts on 100%, 150% and 200% monitors; confirm upper-right work-area anchoring without a 1×1 collapse, temporary upper-left jump or hide/show. Repeat after taskbar/display changes and disconnection.

The adapter dispatches only PreviewKeyDown, ignores repeats and uses `ToUnicodeEx` flag 4 (non-mutating translation, Windows 10 1607+) on the current UI-thread layout. Shift/Caps/NumLock affect printable output; modifiers remain explicit in `LogicalGesture` (for example Shift+`>` on a layout producing that symbol). Ctrl/Alt are removed only from the translation-state copy to avoid control characters. AltGr, dead-key/composition and multi-scalar/unsupported output fail safely. Older Windows cannot translate printable shortcuts through this adapter. Synthetic translation tests cover policy, not every installed layout. See Microsoft's [ToUnicodeEx documentation](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-tounicodeex) and [WPF input overview](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/input-overview).


### Command Execution I — manual Windows checks

Not performed in the implementation environment. Automated tests use fake launchers; CI cannot establish real foreground/focus or executable behavior.

Use a temporary development host with `PaletteConfiguration.CreateStore()`, publishing a command with `Parameters = [new("executable", "Executable", ParameterValueType.Path, true)]` and `Execution = new CommandExecutionSpecification.LaunchExecutable("executable")`. Bind it in Root or a nested context with a typed Path preset to a fully qualified executable you explicitly choose. Construct the controller with that store and `SubmissionCoordinator.CreateDefault()`. Do not install demonstration commands into ordinary application startup. `ExecutionFixture.Store` illustrates the test-only configuration.

- [ ] Selecting the command shows its name, description, argument readiness and Run. Selection alone does nothing externally.
- [ ] Run launches the chosen executable once. Hold Enter/Space while Run has focus and try rapid clicks; confirm no duplicate launch. Tab/Shift+Tab reach/leave Run when Tab is unassigned in the context; focused Run owns Enter/Space and focus traversal. Enter on the context surface remains available to configured contextual bindings.
- [ ] A successful Dismiss command hides the relevant palette. A successful Stay Open command retains its invoking context/path if foreground focus remains. If the launched application takes focus, normal dismissal wins; Disorderly must not reacquire focus.
- [ ] A missing executable shows a useful inline non-start error without crashing. Invocation and arguments remain; only explicit Retry creates another attempt. An uncertain outcome must show no Retry.
- [ ] Missing/invalid arguments and commands without execution specifications cannot run. Relative paths, documents, URLs, scripts and argument-bearing strings are rejected without launching.
- [ ] Escape remains Back/dismiss rather than cancellation; the activation hotkey still toggles. Hide/close during launch and verify the accepted launch continues and no child is terminated. Reopen at Root without stale feedback; old completions must not overwrite a newer selection.
- [ ] Check keyboard focus, accessibility labels and bounded scrolling with Run/progress/errors on mixed-DPI monitors. Confirm unchanged tray behavior and exactly one global registration, without keyboard hooks.

Execution conventions and limits: launch targets use explicit Windows drive/UNC paths ending in `.exe`; device namespaces and unresolved `.`/`..` segments are rejected. No existence check claims identity/race protection. The OS decides what actually starts, and external applications can themselves change files. Process-start acceptance is success; Disorderly neither waits for exit nor supervises the child. Local handles are disposed. Unexpected errors are uncertain; only known non-start failures allow Retry. Diagnostics include identities/outcome codes, not argument paths or raw stack traces.

The app-owned coordinator retains compact attempt-ID tombstones and no completed requests/results. Its default acceptance budget is 16,384 per process lifetime (injectable for testing); it rejects further new attempts rather than evicting IDs and permitting replay. Restart resets this in-memory budget and protection. No exactly-once guarantee spans a crash/restart. Accepted launches have no palette cancellation token; logging is best effort at shutdown, without blocking application exit or adding a child-process monitor.

Direct launch behavior follows [.NET ProcessStartInfo.UseShellExecute](https://learn.microsoft.com/dotnet/fundamentals/runtime-libraries/system-diagnostics-processstartinfo-useshellexecute) and [Process.Start](https://learn.microsoft.com/dotnet/api/system.diagnostics.process.start).


### Argument Editing I — manual Windows checks

Not performed in the implementation environment. STA tests check control projection, native-control capabilities, ownership discovery and stable control/selection identity; they do not prove real keyboard, IME, foreground-focus or mixed-DPI behavior.

Use a temporary injected configuration equivalent to `EditingFixture.Store` (tests only): declare Text, optional Boolean, optional Integer and a required Path; use `LaunchExecutable("executable")` for the required Path. Toggle `requiredText` / `presetPath` as needed. Choose an executable path deliberately for real launch checks. Ordinary production startup still has no commands or bindings.

- [ ] Select a command with a missing required Text field; check **Supplied** to supply explicit empty Text, then type Unicode and spaces. Empty Text is present; unchecking Supplied returns to absent. Verify Run readiness and inline missing-value diagnostics.
- [ ] Repeat with a missing required Path. Type a nonexistent path: editing preserves the exact text and does not resolve/enumerate/check filesystem resources. Blank/NUL-containing supplied paths are invalid; execution separately checks executable syntax and asks Windows to launch.
- [ ] Enter negative integers, zero, both Int64 boundaries, malformed `-` / fractions / letters, and overflow. Invalid raw text must remain visible and disable Run; correcting or explicitly clearing an optional value restores readiness. Parsing uses invariant ASCII digits and an optional sign, without trimming or coercion.
- [ ] Cycle optional Boolean through Not supplied, False and True. Check that False remains a present assignment. Verify supplied presets remain displayed/read-only and unrelated assignments survive additional editing.
- [ ] Type a letter also bound to a context shortcut into Text, Path and Integer fields. It must stay in the field, not navigate. Check Backspace, Delete, arrows, Ctrl+A, copy/paste, Tab and Shift+Tab. Enter in a field must not submit; explicitly focused Run still supports Enter/Space.
- [ ] Test IME/dead-key input and cancellation with relevant installed layouts. Escape must cancel composition/dropdown first where applicable, otherwise leave the field for the contextual surface; it must not also navigate/dismiss. A subsequent Escape on the surface uses normal Back/dismiss. Holding Escape must not traverse contexts.
- [ ] Validate without losing caret, selection, focus, composition or scroll position. Test longer forms with the existing upper-right anchoring, taskbar/work-area bounds and 100%/150%/200% DPI checks.
- [ ] Run the latest edited executable path using the existing launcher. Only the declared executable Path is consumed by LaunchExecutable; Text/Boolean/Integer are retained typed invocation values, **not command-line arguments**. Check freeze while launching, duplicate suppression, Dismiss/Stay Open and external-focus dismissal.
- [ ] Cause a known non-start failure, correct an editable user-entered executable path and explicitly Retry. Preserve the invocation ID and old-attempt provenance while creating a distinct retry attempt. An uncertain result must freeze that attempted invocation and offer no blind Retry.
- [ ] Dismiss or navigate away after meaningful valid/invalid edits. Inspect the injected `IInvocationDraftStore.Drafts` in the development host: separate invocations remain independent, raw invalid text is retained, and preview/navigation alone creates no draft. Reopen at Root without automatic restoration. Successful or uncertain execution must not create an unfinished draft. A late known non-start failure may retain its edited state without restoring old UI.
- [ ] Interrupt Run after mouse-down or Space-down; dismiss/reopen, select another invocation or change retry state before release. Also cancel with Tab/focus transfer or drag outside before mouse-up. Old input must not submit the current Run/Retry. A fresh mouse/Enter/Space or automation activation must work after cancellation; repeat/double-click must not create duplicate attempts.

Editing state uses activation + invocation + editing-session identity and an expected revision. Submission carries the latest immutable invocation version and WPF supplies its editing revision. Preset preservation is a limited implementation boundary, not a permanent locking policy. Drafts retain command/catalog interpretation, presets/user input, raw buffers, last parameter/caret/selection position and prior non-start attempt metadata entirely in memory, without logging raw values or imposing an expiry/count limit. `ArgumentEditorSession.Restore` reconstructs model state with the original invocation ID and a fresh editing-event identity during explicit Resume; it does not open a window or execute anything. Filesystem resources must still be revalidated at execution/restoration boundaries in future workflows; edited Path text proves no availability or identity guarantee.


## Draft Management I manual verification (Windows required; not performed in the implementation environment)

Use an explicitly injected temporary configuration/test host, never permanent production demonstration bindings. Automated fake-launcher and STA control tests do not establish native focus, caret, keyboard-layout or monitor behavior.

- [ ] Select a command with several parameters, enter only part of the input, and dismiss using the global hotkey or external focus loss.
- [ ] Reopen: Root appears first, with an unfinished-command suggestion and no raw argument values; ordinary Root shortcuts still work.
- [ ] Press unmodified Tab from the contextual Root surface or explicitly select Resume. Verify the same invocation/input, original field and caret/selection return, without launching anything.
- [ ] Continue editing, dismiss and resume immediately without further changes: the same draft remains available with updated recency.
- [ ] Create a second invocation of the same command; it becomes newest. Browse drafts, restore the older one and verify independent arguments/identities.
- [ ] Explicitly discard one draft and verify unrelated drafts remain. Open an empty browser; Escape returns to Root without dismissing or changing recency.
- [ ] Configure an ordinary Root Tab binding and verify it wins. With no suggestion/binding, Tab can traverse focus normally; Shift+Tab and Ctrl+Tab do not resume.
- [ ] Test Tab/Shift+Tab in Text, Path, Integer, Boolean, Run and browser controls. Holding Tab must not restore another draft.
- [ ] Test Escape in the browser, an editor (including IME/dead-key composition), a nested context and Root; each keypress performs only the relevant action.
- [ ] Restore invalid integer text such as `-`, Boolean absence/false and opaque path input; correct errors and verify readiness recovers without losing presets.
- [ ] Use mouse and keyboard Resume/Discard with identically named drafts; interrupt a press with dismissal, re-selection or re-suspension and verify stale actions are ignored.
- [ ] Resume work entered through both routes to a shared context; verify breadcrumbs and Escape follow the original route.
- [ ] Publish an equivalent configuration while suspended: restoration keeps the original catalog interpretation. Change/remove its command, parameter contract, presets or saved navigation edges: restoration is blocked with an explanation and input remains stored. The current global configuration is not rolled back.
- [ ] Explicitly Run a completed restored invocation using a safe test executable; success removes its draft only. Verify Dismiss/Stay Open and external focus-loss behavior.
- [ ] Simulate a known non-start failure, suspend/resume, correct editable input and explicitly Retry with preserved previous-attempt provenance. Uncertain outcomes must not offer blind Retry.
- [ ] Dismiss/navigate during an accepted launch, then reopen: the launch continues and late success/failure cannot reopen or overwrite the new interaction. Success leaves no stale Resume suggestion on the next activation.
- [ ] Repeat with many drafts and long forms on mixed-DPI monitors: bounded scrolling, focus, upper-right anchoring and work-area constraints remain correct.
- [ ] Verify no extra global hotkeys/hooks, automatic retries, automatic expiration or disk persistence have appeared. Exit/restart removes all in-memory drafts.

Restoration compatibility is intentionally conservative: command parameter IDs/types/requiredness/order, execution/default policy, saved navigation edges and originating command binding must match; display-name-only command/context renames and unrelated graph changes are permitted. Incompatible drafts remain stored for later reconciliation or explicit discard; there is no migration engine. Restored navigation uses the retained immutable pair, not a global configuration rollback. Path existence/identity is not guaranteed by restoring text; execution still revalidates its target.

Root suggests the newest configuration-compatible draft. Incompatible drafts remain in the newest-first browser, where explicit Resume explains the blocked restoration; they do not steal unassigned Tab. If every draft is blocked, Root shows a non-sensitive explanation and Browse drafts remains available.


## Operation Infrastructure I contracts and validation

- `RunNextAsync` reserves and runs at most one eligible safe segment per call. Concurrent calls may execute non-mutating simulations concurrently, while a single simulated mutating segment owns the global mutation slot through required verification/finalization. Nothing is dispatched merely by accepting or restoring a command draft.
- Queue sequence numbers establish FIFO consideration. Manual Resume has one consumable priority token, checked against resource eligibility. The constructor's locally configurable `manualResumeQuota` defaults to **2**: after two priority selections, eligible normal work must receive the next turn. Blocked/mutation-slot-ineligible work does not prevent other eligible steps. This quota is an implementation parameter, not a permanent product policy. Repeated Pause/Resume does not bank tokens; automatic input resolution enqueues normally.
- Pause and Cancel use identified, idempotent requests. Running work receives cooperative intent, exposed through `CheckpointRequested`; there is no forced interruption. Cancellation takes precedence over pause at a safe boundary, preserves verified completed steps and never performs Undo. Failure/uncertainty cannot be hidden by either request. Input encountered while pausing remains discoverable with pause intent; its final response yields Paused rather than automatic continuation.
- Input requests have unique owning-operation/request identities, structured Accept/Decline choices, copied allowed-response collections and explicit outstanding/resolved/invalidated state. Choices are stored for the simulated runner; they do not themselves authorize filesystem behavior. All outstanding requests block the entire operation. The last valid response revalidates normal eligibility; explicit pause, cancellation, recovery and shutdown take precedence. `Reevaluate` can revisit input-resolved work blocked by eligibility without granting manual priority.
- Scheduling admissions serialize eligibility/selection, then release their gate before runners execute. The injected eligibility boundary runs outside state locks before manual Resume, dispatch and automatic eligibility after input. Default eligibility permits simulations only; real resource checks and race-safe filesystem revalidation are **not implemented**. Proposals/result collections must not be mutated while being consumed; published snapshots defensively copy their contents.
- A runner's task must encompass all required safe finalization. Only an explicitly reported safe checkpoint permits release, suspension or yielding. `Completed` also requires verified simulated completion; task completion/yield alone proves neither. Unexpected exceptions, contradictory/unverified results and uncertain effects require recovery without retry. If safe release cannot be established, mutation ownership remains blocked and graceful shutdown remains incomplete pending a future reconciliation facility; no timeout silently abandons unsafe work. Non-mutating eligible work can still proceed.
- Activity notifications are immutable, revisioned and ordered, delivered outside internal locks on the publishing/draining thread. Subscribers must marshal to their own UI and avoid blocking callbacks. Observer exceptions are isolated. Snapshots are the source for future Activity UI, independently of palette visibility; no percentages, duration estimates, durable completion markers, toast or modal UI are invented.
- `RequestShutdownAsync` stops acceptance/dispatch immediately, requests cooperative pause and completes after active/checking work reaches safe release boundaries. It neither cancels child applications nor saves volatile records. The application does not yet compose production operations, so its existing WPF shutdown behavior is unchanged.

NUnit uses controlled task completions and fake runners, without real file mutation or timed sleeps, to cover lifecycle validation, concurrent controls/dispatch, FIFO/fairness, exclusive finalization, concurrent non-mutating work, input responses/revalidation, immutable Activity, exceptions and shutdown. All earlier execution/draft/palette tests remain applicable. These simulations do **not** prove filesystem mutation safety, durable acceptance, crash reconciliation or recoverable shutdown. SQLite journaling, real handlers, durable intent/effect recording, cross-volume procedures, Activity UI/notifications, Undo, persistent history and independent-resource parallel mutation remain deferred.


## User Configuration I: JSON version 1

The canonical file is `Path.Combine(AppMeta.AppDataDirectory, "config.json")`, normally **`%APPDATA%\Disorderly\config.json`**. It is user-owned and is never installed in the application directory or Git checkout. First launch creates no config file or demonstration commands. After starting Disorderly once, manually create the file using [the complete, tested example](examples/config.v1.json). Adjust its illustrative executable path for your Windows installation. Loading it never launches Notepad; selecting its shortcut previews the command, and only explicit Run launches it.

The example is embedded in the NUnit suite, parsed, validated and round-tripped. It is documentation only and is not installed into production configuration.

| Object | Required fields | Optional fields |
| --- | --- | --- |
| Root | `version: 1`, `rootContextId`, `contexts`, `commands` | None |
| Context | `id`, `displayName`, `bindings` | None |
| Binding | `id`, `gesture`, `destination` | None; owner is the enclosing context ID |
| Gesture | `key`, `modifiers` | None; use logical keys, e.g. `"+"` rather than parsing a display string |
| Command | `id`, `displayName`, `parameters` | `description`, `defaultCompletionPolicy`, `icon`, `execution` |
| Parameter | `id`, `name`, `type` | `required` (defaults false), `description` |
| Preset assignment | `parameterId`, `type`, `value` | None |

Property/discriminator spelling is case-sensitive. Modifiers are an array of unique `"Control"`, `"Shift"`, `"Alt"`, `"Meta"` names, in any order; output uses that canonical order. Keys use existing logical normalization and named/Unicode scalar validation. No keyboard-layout translation happens while loading. Escape and exact Ctrl+Shift+D remain reserved through the existing palette-aware validator; other modifier combinations are independent.

Destination shapes are `{"kind":"context","contextId":"..."}` or `{"kind":"command","commandId":"...","presets":[...],"completionOverride":"dismiss"}`. Only command destinations support optional `presets` and `completionOverride`. Parameter/preset types are lowercase `text`, `boolean`, `integer`, `path`. Presets carry actual JSON string, Boolean or signed 64-bit integer values; no coercion occurs. Empty text, false and zero remain present assignments, distinct from omitted assignments. Duplicate assignments are rejected by domain validation. Parameter declaration order is preserved.

Completion policy strings are `dismiss` and `stay-open`, retaining Binding → Command → Dismiss resolution. Icons are `{"kind":"automatic"}`, `{"kind":"custom-file","path":"..."}` or `{"kind":"built-in","id":"..."}`. Execution is optionally `{"kind":"launch-executable","executableParameterId":"..."}` and must reference a required Path parameter. Descriptions, policy, icon and execution may be omitted or null; other nonnullable fields and present arrays must not be null. Missing presets and an empty preset array both supply no values. Paths/icon paths remain opaque: loading does not inspect targets, require their existence or access associations. Existing execution-time validation is unchanged.

Documents reject duplicate properties (including escaped equivalent names), unknown fields/kinds/enum values, malformed UTF-8/Unicode, unsupported versions and numeric overflow. There is no last-property-wins or silent downgrade. Initial defensive limits are 1 MiB UTF-8, JSON depth 32, 512 contexts, 1,024 commands, 4,096 entries in a binding array, 128 parameters/presets per command/binding and four distinct modifiers. These parsing bounds protect the current validator, including deep context graphs; they are not new global-hotkey limits. Output uses indented JSON with deterministic context/command/binding/preset ordering, retaining parameter order and explicit typed values.

### Loading and reload

Startup first establishes the empty Root/catalog, reusable palette and existing tray/hotkey infrastructure, then performs a bounded asynchronous load. Tray **Reload Configuration** uses the same service/store through the application dispatcher. No watcher, modal dialog, toast or automatic editor is introduced. The tray status line reports success/revision or failure; logs include diagnostic categories, locations and valid structural IDs, never complete documents, preset values or raw exception messages. Unreachable contexts remain nonfatal warnings. Ordinary JSON and file-access errors leave the tray usable and the file untouched.

Only a completely valid routing/catalog pair publishes. Reload does not reset an already visible palette or its selected invocation; the next hidden-to-visible activation uses the latest publication. Drafts are neither persisted in configuration nor deleted/remapped by reload. Compatible draft restoration retains original meaning; incompatible drafts remain stored behind their existing non-destructive checks. Configuration never contains invocations, attempts, operations, Activity, journals or recovery records, and never starts/resumes work by itself.

### Programmatic Save boundary

`ConfigurationService.SaveAsync(document, expectedRevision)` is for later editor integration and tests; there is no graphical Save control yet. Use `Revision` from the owning service. Service-owned load/save operations serialize through an asynchronous gate, without a state lock held across I/O. Domain validation uses the existing palette-aware combined validator plus any target-store rules. `CommandConfigurationStore.Validate` exposes the existing validation without changing publication. The service freezes validated proposals before serialization, writes only valid representable v1 documents and publishes only after known successful saving.

Save checks the expected service revision and the retained publication. A SHA-256 digest detects unexpected file-content changes, including whitespace changes; timestamps alone are not trusted. Saving over an existing file that this service has never successfully read conflicts. Reload acknowledges an observed missing file without clearing active configuration; invalid-file reload does not acknowledge overwriting the invalid content.

Persistence stages a unique temporary file in the destination directory, flushes it (including an OS disk-flush request), rechecks the expected digest immediately before replacement, then uses `File.Replace` for existing files or a no-overwrite same-directory move for first creation. It never deliberately truncates the destination or performs delete-then-move. Temporary files are cleaned up where possible. Failed writes do not publish an unsaved proposal. Replacement exceptions conservatively report an uncertain replacement outcome, retaining the old publication; inspect the file and Reload before retrying. A detected out-of-service publication during saving is preserved and reported as saved-but-unpublished when disk replacement already occurred.

This is best-effort external conflict detection, **not** globally transactional cross-process compare-and-swap. Another process can still edit between the final digest check and replacement. All application file-backed writers must use the shared service. Replacement/flush behavior depends on Windows and the underlying filesystem; perfect crash durability is not promised. Unsupported replacement facilities fail without an unsafe delete fallback. Configuration persistence is unrelated to the future durable operation journal. The API keeps no discarded proposal: callers retain their proposed document on failure.

### Manual Windows checks — not performed in the implementation environment

- [ ] Start without config: tray, global activation and empty Root work; no example file/commands appear automatically.
- [ ] Create `%APPDATA%\Disorderly\config.json` from the tested example, adjust the executable path and select tray Reload Configuration. Verify the status and configured N shortcut.
- [ ] Select the command; verify name/parameters and that neither startup nor Reload launched anything. Explicit Run retains its established launch/completion behavior.
- [ ] Change a display name and Reload: the next activation sees it. Reload during a nested visible interaction; it keeps its old context/invocation until dismissal/reopening.
- [ ] Introduce malformed JSON; Reload retains the previous working bindings, reports the failure without focus stealing and leaves the file unchanged. Correct and Reload successfully.
- [ ] Test duplicate local gestures, missing command/context references, invalid execution parameters, unknown fields and unsupported versions. Verify useful diagnostic categories/IDs and no partial publication.
- [ ] Restart with valid config: automatic startup loading restores the authored configuration. Check empty/missing and access-denied cases remain usable.
- [ ] Create meaningful unfinished edits, then Reload compatible and incompatible changes. Verify Root-first activation and draft Resume retain original interpretation or explain blocked restoration without deleting input.
- [ ] Confirm existing explicit Run, argument editing, focus/dismissal, mixed-DPI placement and tray Exit still work, with exactly one global hotkey and no additional hooks.

Graphical command/context/binding/parameter editors, migrations, file watching, profiles, cloud synchronization, invocation-preset override policy, command search, durable drafts, additional execution kinds, production operations/Activity UI, SQLite journals, filesystem recovery and Undo remain deferred. Automated codec/service/fake-failure tests and isolated-directory writes do not establish native tray, keyboard, foreground-focus or real executable behavior.
