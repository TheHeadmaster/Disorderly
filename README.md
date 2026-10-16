# Disorderly

Disorderly is a Windows utility intended to combine two complementary capabilities:

- A user-configurable contextual command/hotkey launcher for navigating shortcuts, selecting commands and entering arguments.
- Incremental, interruption-friendly file organization that can pause for decisions and resume safely.

## Architecture and Design

The authoritative specification is [Architecture and Design Decisions](docs/design-decisions.md). It records the settled contracts, implementation boundaries, glossary and an illustrative checkpoint roadmap.

The key invariants are:

- Exactly one global activation hotkey; all contextual bindings operate inside Disorderly's focused interface.
- Explicit context transitions with strict local bindings, stable identities and a navigation stack recording the actual path.
- Every hidden activation opens at Root. Planned Resume suggestions restore drafts only on explicit selection, without executing them.
- Invocation drafts and accepted operations have different lifecycles; dismissing UI does not inherently cancel unfinished work.
- Initially, only one file-mutating step executes globally. Manual Resume receives bounded priority with fairness; future independent-resource parallelism must preserve the same safety contracts.
- Awaiting Input stops the entire operation. Cancellation preserves completed changes; Undo is a separate operation.
- Revalidate filesystem identity and state at the point of use and across waits; use appropriate atomic/handle-based safeguards and verify actual outcomes.
- Durable operation intent belongs in a SQLite journal, distinct from diagnostics. Uncertain outcomes require reconciliation; mutating work requires explicit Resume after restart.

**Implemented today — Foundation I:** A tray-resident .NET 10 WPF application with a compact root palette, provisional **Ctrl+Shift+D** activation, **Open Command Palette** in the tray menu, focus/dismissal handling and per-monitor DPI-aware positioning.

**Implemented — Context Routing I:** A UI-independent engine in `Disorderly/Routing` with routing domain models, context-local bindings, a reusable acyclic graph, navigation stack, structured validation, immutable configuration snapshots and typed routing outcomes. It can be exercised directly in NUnit without windows or native hotkeys.

**Implemented — Command Model I:** A UI-independent command catalog with reusable definitions, typed required/optional parameters and arguments, binding presets, completion policies, icon metadata, immutable invocation/attempt contracts and compatible routing/catalog snapshots. Validation can prepare an execution-ready request, with executable behavior supplied separately by Command Execution I.

**Implemented — Palette Interaction I:** The reusable WPF palette now owns a fresh compatible routing/command interaction on each activation. It displays the active context, actual breadcrumb and local bindings, navigates with contextual gestures and Escape, and previews command definitions/argument readiness without executing them. Visible interactions retain their original snapshot. Production startup deliberately contains only an empty Root and an empty catalog; there is no binding editor or persistence yet.

**Implemented — Command Execution I:** Commands may opt into `LaunchExecutable` through a declared required Path parameter. An explicit Run/Retry control submits an existing immutable invocation through an identified attempt and a deduplicating coordinator. The executable launches directly, with no shell or extra arguments. Outcomes distinguish success, pre-execution rejection, known non-start failure and uncertainty. Successful completion applies the retained invocation policy; errors preserve the selection, and uncertainty offers no blind retry. Production startup still contains only an empty Root and empty catalog; command creation/configuration is not available through the UI.

**Planned, not implemented:** Document association launching, structured argument vectors, scripts/shell commands, argument editing, typed search, configuration UI/persistence, suspended drafts/Resume, Activity/operations, background scheduling, SQLite journaling/recovery, file mutation, Undo, icon extraction/rendering and durable execution deduplication across crashes.

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
- One controller owns one reusable lightweight palette window. Every hidden-to-open transition resets root presentation; repeated activation toggles closed. Escape and external focus loss dismiss without exiting. Owned windows/popups are allowed to take focus. Visibility is separate from future command/draft state; no synthetic Resume item is shown.
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
