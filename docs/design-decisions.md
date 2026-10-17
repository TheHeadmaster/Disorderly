# Disorderly Architecture and Design Decisions

This is the authoritative record of Disorderly's settled architectural and behavioral decisions. Later implementation checkpoints must preserve these contracts. A design decision being settled does not mean that its functionality exists today.

## Status Conventions

- **Settled:** A decision approved for the intended architecture.
- **Implemented:** Functionality already present in the repository.
- **Deferred:** An approved design deliberately postponed until a later checkpoint.
- **Unsettled:** A question that still requires design work.

Unless explicitly marked Implemented below, the subsystem requirements in this document are Settled design contracts for future implementation. Approved enhancements marked Deferred retain their long-term intent while respecting their initial implementation boundaries.

## Purpose and Current Implementation

Disorderly combines a user-configurable contextual command/hotkey launcher with incremental, interruption-friendly file organization. Users should be able to start, interrupt and return to useful work without giving Disorderly ownership of ordinary keyboard input in other applications.

**Implemented — Foundation I:** A tray-resident .NET 10 WPF application with explicit shutdown, a compact root palette, one provisional Ctrl+Shift+D activation registration, tray-based activation, root-first presentation, foreground/focus requests, and dismissal through Escape at Root, repeated activation or external focus loss. Registration conflicts are logged while the tray remains usable. PerMonitorV2 awareness and physical-pixel positioning support monitor work areas and negative desktop coordinates. Hidden placement prepares monitor DPI before Show; visible placement applies final bounds directly. Custom windows use monitor-relative WM_GETMINMAXINFO without artificial maximize/restore transitions.

This inventory describes code present in the repository, not a claim that all interactive Windows scenarios have been manually verified. Build instructions and runtime checks remain in the [README](../README.md).

**Implemented — Context Routing I:** The UI-independent `Disorderly.Routing` subsystem provides stable context/binding identities, typed context/opaque-command destinations, canonical logical gestures, context-local resolution, a reusable acyclic context graph, path-based navigation, structured validation errors/warnings, immutable snapshots, atomic publication and typed routing outcomes. Active sessions retain their original snapshot; a newly created session uses the latest successful publication. Available bindings are enumerated deterministically in canonical-gesture then binding-ID ordinal order.

Initial domain conventions: identifiers are case-sensitive ordinal ASCII tokens beginning with a letter/digit and continuing with letters/digits or `-`, `_`, `.`, `:`. Logical key identities are case-normalized invariantly: one non-whitespace, non-control Unicode scalar, or a named key (Escape, Tab, Backspace, Enter, Space, Delete, Insert, Home, End, PageUp, PageDown, arrows, F1–F24). Modifiers are Control, Shift, Alt and Meta flags; order does not matter, unknown bits are invalid. The engine does not infer modifiers from character output or map physical keys to symbols. Extending supported key names does not change routing semantics. Only unmodified Escape is reserved by default; additional exact reservations may be supplied to validation for future interaction modes.

Proposals remain caller-owned and must not be mutated during validation. Published snapshots copy/freeze their collections. Publication exchanges a complete snapshot atomically; routing sessions themselves are owned by one interaction thread. The standalone routing validator continues to accept opaque, syntax-validated command references. Command Model I validates them against the associated catalog at the combined publication boundary. A command request carries command, binding and invoking-context IDs so later command infrastructure can resolve presets and completion policy without duplicating definitions. No argument schema is introduced.

**Implemented — Command Model I:** The UI-independent `Disorderly.Commands` subsystem provides reusable definitions and an immutable catalog, typed parameter/argument contracts, command-binding presets, completion-policy resolution, icon metadata, invocation/attempt identity and execution-ready preparation. `CommandConfigurationStore` validates and atomically publishes a compatible routing/catalog pair; `CommandInteraction` retains that pair and its routing session. Invalid replacements preserve the previous pair. Missing command references and invalid presets fail combined publication; standalone `RoutingConfigurationStore` remains independently usable with opaque references.

Concrete domain conventions:

- Command, context, binding and parameter IDs use the same shared ordinal ASCII identifier validator. Commands have their own identity domain; parameter IDs are scoped to their command. Duplicate display names remain permitted, and selection is by stable ID rather than by an ambiguous name.
- Parameters retain declaration order. Catalog definitions enumerate by ordinal command ID. Argument assignments enumerate by ordinal parameter ID and preserve duplicates/malformed entries for structured diagnostics rather than silently overwriting them.
- Values are explicit Text, Boolean, signed 64-bit Integer and Path records. Text/path equality compares retained text without filesystem canonicalization. Path values are opaque text, structurally nonblank and NUL-free; no path resolution, existence check, association lookup or image loading occurs. Empty text, false and zero are present values. Missing required values produce Incomplete; malformed, unknown, duplicate or incompatible assignments produce Invalid. Both block execution-ready preparation.
- Presets and optional completion overrides belong only to typed command destinations. Presets can omit required parameters. No preset locking/editability or automatic argument-merge policy is established. `WithArguments` is immutable state replacement retaining invocation identity; a future editor is responsible for any editing authorization.
- Completion resolves Binding override → Command default → Dismiss. A direct selection has an explicit Direct origin without a fabricated binding or context; contextual selection captures its binding/context IDs from the associated snapshot. Preparing a request does not navigate, dismiss or change focus.
- Icon metadata uses Automatic by default, CustomFile(path) or BuiltIn(identifier). Definition validation checks metadata structure only. Missing resources are not checked and cannot block domain readiness; eventual presentation fallback remains separate from execution behavior.
- Invocation and attempt identities are independent nonempty GUIDs. Constructing or revising an invocation creates no attempt. An explicit `PrepareAttempt` call creates a new identified request only for a valid invocation; requests retain the immutable invocation version and catalog interpretation. Preparing an attempt does not execute anything or automatically retry. Command Execution I supplies single acceptance and repeated-confirmation protection at submission.
- Invocation preparation distinguishes failure to resolve a command/origin from a successfully constructed but Incomplete/Invalid invocation. Definition snapshots and argument sets copy/freeze caller collections; callers must not mutate proposals while they are being validated. New interactions use the latest successful pair; existing interactions and invocations never retrieve a newer catalog or binding midway through preparation. Execution-ready means the declared domain contract is satisfied, not that future execution-time resource checks or authorization have occurred.

**Implemented — Palette Interaction I:** The existing visibility controller now owns one transient `CommandInteraction` per hidden-to-visible activation, with separate Hidden/Opening/Visible/Dismissing visibility states. Navigation and command interpretation remain in the domain models. A new activation begins at Root using the latest compatible snapshot; dismissal releases the interaction without creating a draft. The WPF view renders actual breadcrumbs and active-only binding labels with bounded scrolling, and command selections show immutable invocation readiness without creating attempts or executing/completing commands. Preview selection does not add a navigation layer. Empty Root/empty catalog is the production configuration.

Technical conventions: `PaletteConfiguration.CreateStore` supplies exact Ctrl+Shift+D reservation through `RoutingValidator`, in addition to unmodified Escape. The controller/view contract carries a per-activation GUID on gestures and dismissal requests, and deferred geometry/focus work checks activation provenance. WPF key-down is the sole routing source; text/composition events never dispatch. The adapter uses current-layout non-mutating Windows translation, retains effective modifiers and ignores repeat, IME/dead-key composition, AltGr and ambiguous output. Text controls retain their own input ownership. Real keyboard-layout, foreground-focus and multi-monitor behavior still requires the README's manual Windows checks.

**Implemented — Command Execution I:** Command definitions optionally carry a typed `LaunchExecutable(ExecutableParameterId)` specification. Catalog/combined publication validates its reference to an existing required Path parameter; no target filesystem access occurs during publication. Commands without a specification remain valid and retain the original argument-only `IsExecutionReady` contract, but cannot submit to an executor. The fixed handler registry and asynchronous submission coordinator are independent of WPF/routing. The initial handler starts only an explicit fully qualified Windows `.exe` with `UseShellExecute=false`, no arguments, shell, association lookup or elevation; no process-exit monitoring or file mutation is introduced.

Explicit Run creates and retains an existing `ExecutionReadyRequest`. The coordinator atomically accepts an attempt ID at most once; the controller additionally rejects repeated Run events and requires the previous failed attempt ID on explicit Retry. Activation, invocation and attempt provenance guard async presentation and completion. Selection/render/navigation/focus changes create no attempt. Accepted launch work continues after navigation/dismissal/disposal; completion never reopens or restores an obsolete selection. The retained invocation policy applies only after success. Stay Open retains its path if the relevant palette is still present; external focus loss wins.

Execution results carry command/invocation/attempt identities, a structured category/code and non-sensitive messages. Rejected-before-execution, succeeded, known non-start failure, uncertain and already-accepted outcomes are distinct. Unexpected handler exceptions or ambiguous process results are uncertain, without blind Retry. Known non-start failures retain arguments/invocation and allow explicit retry with a new attempt. Log identities/outcome codes rather than argument values/stack traces in ordinary feedback.

Concrete limits: drive/UNC `.exe` syntax is checked at execution time; relative/device/argument-bearing/unresolved-dot paths are rejected. No preflight existence check promises filesystem identity or atomic launch. Success means process-start acceptance, not child exit or harmless child behavior. The app-owned acceptance ledger stores only compact IDs, bounded to 16,384 by default; when full it rejects new work rather than evicting deduplication history. It never retains completed request/catalog graphs. This process-lifetime resource limit is injectable for tests and resets on restart; durable idempotency/recovery remains deferred. Launch work is observed/logged independently of the UI; shutdown logging is best effort without waiting for children or arbitrarily blocking exit.

The WPF preview exposes explicit Run/Retry, pending and inline outcomes. A focused Run button owns its standard Enter/Space activation and focus traversal, including repeat suppression; Enter is not reserved globally or in contextual configuration. Unassigned Tab on the contextual surface can perform standard focus navigation; assigned context bindings retain their meaning. No production sample bindings or command-management UI are introduced.

**Implemented — Argument Editing I:** `Commands.Editing.ArgumentEditorSession` holds raw parameter input separately from typed immutable invocation revisions, retaining command/catalog, origin and completion policy. Changes identify activation, invocation, editing session, expected revision and parameter; stale/unknown/out-of-scope edits are rejected. Revisions replace only the affected parameter ID through `WithArguments`; domain structure/type/requiredness remains authoritative in `ArgumentValidator`. Supplied binding presets are non-editable in this initial interface and remain unchanged; this deliberately limited boundary does not settle future preset override/editability policies.

Text preserves Unicode, whitespace and explicit empty presence. Boolean distinguishes absent/false/true. Integer uses invariant ASCII digits with an optional sign and checked Int64 parsing; malformed/overflow raw text is retained and blocks Run. Path remains opaque text, validated through the existing domain boundary without filesystem access. Invalid supplied input never reuses an older typed value or becomes an implicitly omitted optional value for submission. Run requires valid typed arguments, no pending input errors, an implementation and the existing attempt/retry eligibility. Editing itself creates no attempt. Accepted versions freeze until a known non-start failure permits correction and explicit identified Retry; uncertain/successful attempts cannot be casually edited or resubmitted.

The WPF field controls are stable for an editing session; validation updates values/diagnostics without rebuilding focused controls, caret/selection or the parameter list. Descendant/interactive-control ownership keeps text, IME, named editing keys and Tab local. Escape first permits native composition/dropdown cancellation or leaves the editor; it cannot also navigate. Context-surface navigation, focused Run semantics and the single global hotkey remain distinct. Run capture now invalidates on activation/invocation/attempt/editing-revision/availability changes and clears on hide/disposal; obsolete physical input does not fall back to a current submission. Native capture/focus is canceled on hide so fresh physical and automation input can proceed.

`IInvocationDraftStore`/`InvocationDraftStore` retain multiple independent meaningful unfinished editor snapshots in memory without expiry, persistence or raw-value logging. Stable draft/invocation IDs, command/catalog interpretation, origin, typed assignments, raw invalid buffers, last parameter/caret/selection position, preset flags and prior non-start attempt metadata are retained. Preview/selection/navigation alone does not qualify. Pending accepted work is not an unfinished draft; a later known non-start failure can retain meaningful input independently without altering current UI. Success/uncertainty does not create a resumable draft. Each activation remains Root-first. Draft Management I integrates reconstruction with explicit user-facing restoration and a fresh editing-event epoch.

**Implemented — Draft Management I:** Suspensions receive a lock-protected monotonic generation, ordered newest-first independently of time, names or dictionary order. Available drafts and active restoration leases share stable draft/invocation identities; atomic identified Take removes an item from the available list, and re-suspension returns it with new recency. Explicit Discard affects only the named suspended generation. No count limit, automatic expiration or persistence is introduced.

Root stays the initial view. Its real newest-draft suggestion exposes command name and non-sensitive input status, never raw argument values. Unmodified Tab is conditional on contextual-surface focus, a suggestion and no explicit local Root Tab binding; other modifiers and controls retain ownership. Browse drafts is a palette mode, not a synthetic routing context. Escape returns that mode to Root; available drafts have explicit keyboard-accessible Resume/Discard actions.

Each retained editor carries its immutable combined snapshot and actual stable-ID navigation path captured at selection. Restoration validates Root and every context edge, binding/command origin, parameters and presets. Relevant current command contracts and saved navigation edges/origin binding must remain compatible; equivalent republication and display-name-only command/context changes do not replace retained meaning. Incompatible/missing contracts block restoration with a non-destructive explanation, preserving the draft; no configuration migration or global rollback is performed. Original invocation/arguments/presets/completion policy and safe failed-attempt provenance survive. Raw invalid text and parameter/caret/selection state return under a fresh editing session; deferred focus verifies activation/editor provenance and updates do not rebuild controls. Restoration itself prepares no attempt and has no execution effects.

Meaningful restored work remains eligible even if immediately interrupted without further edits. Known non-start failures retain identified explicit Retry; pending/uncertain accepted work is not offered as an ordinary draft. Success removes the completed invocation's record, including late completion after dismissal, without touching unrelated drafts or restoring obsolete UI. Resume/Discard actions carry activation/draft/generation identities; physical button presses retain their original target. In-memory state disappears at process exit. Filesystem text is not a resource guarantee: execution-time revalidation remains separate, and no filesystem lookup or mutation is added to restoration.

**Still Deferred:** Preset editing/override policy, inline pills, guided prompting beyond this field editor, autocomplete/path completion, typed search, persistent drafts/configuration, configuration management, additional execution kinds/argument vectors, operations/scheduling/pause/cancel, SQLite journaling/recovery, filesystem mutation/file organization/Undo/Activity, icon extraction/customization and durable deduplication.

## Glossary

| Concept | Meaning |
| --- | --- |
| Context | A stably identified set of locally available bindings. Root is the entry context. |
| Binding | A normalized gesture in one context, with exactly one destination: a command invocation or another context. It may supply preset command arguments. |
| Command definition | Reusable metadata and an execution contract describing a command, independent of a particular invocation. |
| Command invocation | One stably identified use of a command with its arguments and invoking context. Execution attempts have their own identities. |
| Invocation draft | An unfinished invocation or meaningful partial command/search input, suspended before work is accepted for execution. |
| Navigation stack | The actual sequence of context entries followed in the current interaction; it determines backward navigation. |
| Operation | Work accepted for execution, even if its first step has not started; it may continue independently of palette visibility. |
| Operation step | A unit of operation work with relevant validation, effects, verification and recording. A file-mutating step owns the mutation slot through necessary safe finalization/cleanup. |
| Safe checkpoint | A boundary at which an operation can safely pause, honor cancellation or yield scheduling ownership without abandoning an unsafe mutation. |
| Input request | An identified, operation-linked outstanding request for a user decision; responses are accepted only while it remains valid. |
| Operation journal | Durable intent, verified effects, checkpoints and recovery information, stored separately from diagnostic logs. |
| Activity record | The unified user-facing record of running, blocked, interrupted or recently completed operation work. |
| Undo | A separate, explicit compensating operation based on recorded actual effects and current filesystem validation. It is not cancellation or guaranteed rollback. |

## Global Activation and Contextual Keyboard Routing

### Single global hotkey

Disorderly registers exactly one system-wide hotkey, used to activate the command palette.

The current implementation uses `Ctrl+Shift+D` as its provisional default.

All other keyboard bindings and chord navigation belong to Disorderly's focused interface.

Do not use system-wide keyboard hooks to implement ordinary contextual bindings.

### Context-based routing

Each binding in a context has exactly one routing destination:

1. Invoke a command.
2. Enter another context.

A binding cannot simultaneously execute a command and act as the prefix of a longer chord.

Chords are represented through explicit context transitions, not timing-based ambiguity resolution.

### Strict context isolation

Only bindings declared in the active context are available.

Parent bindings are not inherited.

The same gesture may have different meanings in different contexts.

Reserved palette navigation gestures remain available independently of ordinary user bindings.

### Reusable contexts

Contexts have stable identities and may be referenced from multiple entry points.

The context-transition graph is a directed acyclic graph.

Reject self-references and circular context-transition references.

Context names are presentation metadata, not stable identities.

### Navigation stack

Navigation maintains the actual path followed by the user.

`Escape` returns to the previous context in that path.

`Escape` at Root dismisses the palette.

Do not assume each context has a single parent.

Backward navigation is reserved for Escape, not Backspace.

### Keyboard interpretation

Use logical, keyboard-layout-aware gestures.

Bindings are normalized using the effective key and modifier combination.

Do not use physical keyboard positions as the default binding identity.

Unassigned shortcuts are silently ignored.

Display the available shortcuts for the current context by default.

---

## Binding Configuration and Validation

### Context-local uniqueness

A gesture must be unique within its context.

The same gesture may appear in different contexts.

A gesture cannot be simultaneously assigned to a context transition and a command invocation within the same context.

### Reserved gestures

Reserved palette navigation gestures cannot be reassigned in ways that conflict with their required behavior.

Reservation is based on the relevant exact gesture and interaction mode rather than banning unrelated modified combinations.

### Configuration changes

Validate changes before publication.

Reject ambiguous or structurally invalid configurations atomically.

Do not silently overwrite an existing binding.

Configuration changes become effective on the next root activation, not in the middle of an active palette interaction.

An active session uses a stable routing configuration snapshot.

### Stable identities

Commands, contexts, and bindings have stable identifiers distinct from their user-facing names.

Renaming a context or command must not invalidate its references.

### References and deletion

Block deletion of a command or context while it is referenced by bindings.

References must be resolved explicitly before deletion.

Unreachable contexts are permitted, but the management interface should warn about them.

### Binding-specific arguments

A binding may provide preset arguments to its target command.

The same command can therefore be invoked through multiple bindings with different preset values.

Do not duplicate the command definition to support this.

### Context reuse

Distinguish between:

- Referencing an existing context, which shares its bindings.
- Duplicating a context, which creates an independent identity and configuration.

The distinction must be clear in the eventual management interface.

---

## Command Discovery and Argument Editing

Disorderly is hotkey-first.

The Root context must not automatically interpret ordinary typing as global free-text search.

Instead, users may configure bindings that deliberately enter specialized input modes.

Examples include:

- A Find binding for global search.
- A command-search binding, such as `Shift+Period` producing `>` on a US keyboard.

The exact gestures are user-configurable.

These input modes use ordinary text-entry semantics rather than contextual hotkey dispatch.

### Unified command registry

Typed command selection and contextual bindings resolve through the same command definitions and execution infrastructure.

Do not create separate hardcoded command systems for search versus keyboard bindings.

### Typed parameters

Commands may declare required and optional parameters.

Supported editing concepts include:

- Contextual parameter completion.
- Inline structured parameter pills.
- Guided parameter prompting.
- Validation before execution.
- Keyboard navigation between parameters.

Inline editing and guided prompting must use the same underlying argument model.

### Command-name resolution

Entering arguments after a typed command name requires the command to be uniquely resolved.

Do not interpret a space as argument entry while the entered command name remains ambiguous.

Explicit selection or completion, such as Tab, can commit a command.

### Keyboard ownership

While a text field or argument editor is active, ordinary character keys edit text.

They must not trigger unrelated contextual shortcuts.

---

## Palette Visibility, Dismissal, and Resumption

### Root-first activation

Every activation from a hidden state opens the palette at Root.

Never automatically reopen an interrupted nested command or text editor.

This preserves predictable root-level shortcuts.

### Dismissal

The palette dismisses when it loses focus to an external application.

It also dismisses when:

- Escape is pressed at Root.
- The global activation hotkey toggles an already-active palette closed.

Dismissal does not terminate Disorderly.

Dismissal does not automatically cancel or discard unfinished work.

Owned application UI should not cause incorrect external-focus dismissal.

### Interrupted invocation drafts

An unfinished command becomes resumable after meaningful user input or argument modification.

Meaningful unfinished text in command or search input may also qualify.

Merely entering and leaving a shortcut context does not create a draft.

### Multiple drafts

Support multiple independent suspended invocation drafts without an initial fixed count limit.

Separate invocations of the same command produce separate drafts.

Executing another command does not erase unrelated suspended drafts.

### Root presentation

Show the most recently interrupted draft as a Resume suggestion at Root.

The established initial resume gesture is Tab when the suggestion is applicable.

Provide a separate command or context for browsing older drafts.

Resuming restores the relevant command, argument, partial input, and editing position.

Revalidate stale resources and regenerate volatile autocomplete results when restoring.

Restoration never executes the command automatically.

### Retention

Drafts do not expire automatically.

Disorderly may quietly suggest reviewing old drafts.

Future behavior should support persistence across application restarts.

**Initial implementation boundary:** Use in-memory draft state first. Durable draft persistence is an approved future enhancement.

Do not persist sensitive parameter values indiscriminately.

---

## Command Execution and Completion

### Invocation identity

Separate command definitions from their individual invocations.

Each invocation has a stable identity.

Each execution attempt has its own identity.

Prevent accidental duplicate execution from repeated confirmation input.

### Completion policy

The default successful-completion policy dismisses the palette.

Commands may specify a different default.

Individual bindings may override the command's policy.

Resolution order:

`Binding override → Command default → Dismiss`

An effective Stay Open policy returns the user to the invoking context after successful execution.

It preserves the context navigation stack.

External focus loss overrides Stay Open.

Completing background work must never unexpectedly reopen the palette or steal focus.

### Validation and failure

Invalid arguments prevent execution.

Failed executions preserve relevant entered values and diagnostic information.

Offer explicit retry where safe.

Do not blindly retry an invocation that may already have produced side effects.

### Command acceptance versus operation completion

Starting a background operation successfully is distinct from the operation completing successfully.

The palette's completion behavior may apply after successful submission while the operation continues independently.

A command that starts another application normally considers successful process launch its completion, rather than waiting for the application to exit.

---

## Interactive Workflows and Background Operations

Commands may initiate either:

1. Interactive workflows requiring user decisions.
2. Independent background operations that can progress without continued interaction.

They share the underlying operation lifecycle infrastructure.

Substantial interactive workflows should use a dedicated interface rather than forcing every interaction into the compact palette.

Support multiple independent resumable interactive organization sessions.

Closing or unfocusing an interactive window does not inherently cancel its operation.

An unfinished command invocation is a draft.

Work becomes an operation once accepted for execution, even if its first step has not started (Queued).

Do not conflate these lifecycles.

### Operation states

The operation model must conceptually support:

- Queued
- Running
- Pausing
- Paused
- Awaiting Input
- Cancellation Requested
- Succeeded
- Failed
- Canceled
- Needs Recovery

Also record uncertainty and partial side effects independently of the overall operation status where necessary.

Not every command or operation must support every state.

### Activity

Provide a unified Activity concept for running, blocked, interrupted, and recently completed operations.

Progress must be factual.

Do not invent progress percentages or estimated completion times when the operation cannot determine them.

Closing Activity must not terminate operations.

---

## Initial File Mutation Concurrency

**Settled decision: globally serialized file-mutating steps.**

Initially, only one file-mutating step may execute globally at a time.

Different operations and interactive sessions may coexist.

Non-mutating activities may run concurrently where safe.

A mutating step must retain ownership of its execution slot through the portion that must be treated as one safely coordinated operation, including necessary finalization or cleanup.

Do not release the slot halfway through an unsafe multi-stage mutation.

### Blocked operations

An operation waiting for user input or explicitly paused relinquishes its execution slot.

Other eligible operations may continue, subject to resource-conflict and filesystem-safety checks.

### Future concurrency

Design the scheduler and operation interfaces so the implementation can later permit concurrent mutations on independent resources.

Do not hardcode an architectural assumption that globally serialized mutation is a permanent requirement.

However, **do not implement parallel file mutation in the first execution-engine checkpoint**.

Any future promotion to parallelism must preserve the existing safety, journaling, cancellation, and recovery contracts.

---

## Scheduling Priority and Fairness

**Settled decision: bounded manual-resume priority with fairness.**

Normal eligible operations use approximately FIFO scheduling.

When a user explicitly resumes an operation, it receives priority consideration at the next safe checkpoint.

This priority:

- Must not interrupt an in-progress file mutation.
- Must not bypass resource-conflict checks.
- Must not bypass recovery or validation requirements.
- Must not allow indefinite starvation of other operations.
- Must not permanently elevate the resumed operation above the rest of the queue.

A blocked operation automatically becoming runnable after input resolution uses normal scheduling unless the user explicitly requests priority.

This baseline does not prescribe scheduling time slices or numeric quotas.

Those implementation details remain to be settled when the scheduler is built.

---

## Pause, Cancellation, and Input Requests

### Pause

Pause is cooperative.

The operation transitions to Paused only after reaching a safe checkpoint.

A pause request does not arbitrarily terminate an in-progress filesystem mutation.

Paused operations do not start further file modifications.

### Resume

Resume requires eligibility checks, resource-conflict checks, and filesystem revalidation before proceeding.

### Cancel

Cancellation stops remaining work at a safe checkpoint.

**Completed changes remain intact.**

Cancel is not Undo.

Do not automatically roll back already completed file operations.

Record which steps completed and which were never started.

### Input requests

When an operation encounters a condition that requires user input, it enters Awaiting Input.

**The entire operation stops processing additional items.**

It does not continue with later files while waiting.

The input request remains available through Activity.

The request must be tied to its operation and have its own identity.

Responses must be accepted only while the request remains valid and outstanding.

Do not steal foreground focus or show unexpected modal dialogs.

### Automatically continuing after input

When the final outstanding input request is resolved, the operation automatically becomes eligible to continue.

Exception: If the user explicitly paused it, answering the request does not resume it.

Cancellation, recovery requirements, resource conflicts, and filesystem validation still take precedence.

### Unexpected file errors

Stop and request an appropriate decision rather than automatically skipping the affected file.

Support an explicit **Apply to remaining conflicts** option for suitable repeated conflict categories.

The selected conflict policy must remain subject to per-file validation and safety constraints.

No blanket authorization may bypass filesystem safety checks.

### Retry

Execution failures require explicit retry.

Do not automatically retry failed filesystem mutations merely because the error appears transient.

---

## Filesystem Identity, Race Conditions, and Revalidation

The following requirements form a normative filesystem-safety contract.

**Settled invariant: Previously observed filesystem state must never be assumed to remain valid at the point of use.**

Disorderly must account for files and directories being moved, renamed, deleted, replaced, or modified by other applications while Disorderly is awaiting input, paused, executing asynchronously, or recovering.

### Revalidation boundaries

Revalidate relevant filesystem assumptions:

- Before an operation step that depends on previously observed filesystem state.
- After waiting for user input.
- After a pause or asynchronous wait.
- Before resuming an operation.
- Before retrying a failed operation.
- During recovery and reconciliation.
- Before Undo or other compensating operations.
- After filesystem mutations, when verifying their outcomes.

### Identity versus path

A pathname alone is not always sufficient to identify the intended file.

Where supported, use stable filesystem identity information or appropriate open-file handles to distinguish the original object from another file that now occupies the same path.

Validate content or metadata changes when relevant to the requested action.

### TOCTOU protection

A simple `File.Exists` or equivalent check is not sufficient protection.

Another process may change the filesystem between the check and the operation.

Use appropriate atomic or handle-based operating-system mechanisms, file-sharing constraints, no-overwrite behavior, and related safeguards where available.

Do not claim that preliminary checks eliminate all filesystem races.

### Changed or missing resources

If the relevant resource cannot be verified or its state no longer satisfies the operation's assumptions:

- Do not proceed blindly.
- Do not silently substitute a different file at the same path.
- Do not overwrite unrelated data.
- Stop for user input or recovery as appropriate.

### Post-operation verification

A filesystem operation is not considered durably completed until its actual outcome has been verified and appropriately recorded.

The same revalidation principles apply to Undo.

---

## Operation Journaling and Recovery

### Storage

**Settled decision: SQLite is the intended durable operation journal storage mechanism.**

The journal is separate from Serilog diagnostic logging.

It records operation identities, steps, intended changes, verified results, errors, checkpoints, and recovery-relevant information.

### Write-ahead intent

Record recoverable intent durably before attempting a relevant filesystem side effect.

Do not advance durable progress markers until preceding results have been verified and recorded.

A SQLite transaction cannot make arbitrary filesystem changes atomic with database changes.

The recovery design must explicitly account for failures between filesystem mutation and journal update.

### Uncertain results

If Disorderly stops after an action was attempted but before its outcome was recorded, treat the outcome as uncertain.

Do not automatically repeat the mutation.

Reconcile the journal with the current filesystem state.

### Application restart

Restore and reconcile interrupted operation records after restart.

**Do not automatically resume mutating operations after restart.**

Explicit user Resume is required, even when recovery finds an operation eligible to continue.

### Recovery states

Distinguish:

- Work known to have completed.
- Work known not to have started.
- Work requiring reconciliation.
- Work requiring user attention.

Preserve uncertainty explicitly instead of falsely reporting success or failure.

### Cross-volume operations

Do not assume all file moves are single atomic operations.

The eventual filesystem engine must address multi-stage copy, verification, finalization, and source removal where applicable.

Recovery must account for temporary artifacts and intermediate states.

### Graceful shutdown

When exiting with active operations, allow them to reach safe checkpoints before shutdown completes.

Do not silently abandon partially executed mutations.

Abrupt termination and crashes must be handled by recovery, not treated as graceful exits.

---

## Undo

Undo is a separate, explicit operation.

Support independently undoable completed operations where safe, rather than limiting Undo to only the most recently completed operation.

Undo must use recorded actual effects, not merely the original requested command.

Before reversing a change, revalidate relevant filesystem identity and state.

If an item has been moved, modified, deleted, or replaced since the original operation, Undo must not blindly overwrite or destroy the current data.

Undo may require user input or report that automatic reversal is unsafe.

Do not promise universal or perfectly atomic rollback.

---

## Activity Retention and Notifications

Completed Activity records have configurable retention.

Unresolved recovery records must never be automatically deleted by routine history cleanup.

Operations awaiting decisions remain discoverable.

Notifications may be delivered through configurable, quiet Windows notifications for:

- Failures.
- Required input.
- Other genuinely actionable operation conditions.

Notifications must not steal keyboard focus.

Avoid repeatedly notifying the user about the same unresolved request.

Activity must remain usable without notifications being enabled.

---

## Command Icon Resolution

Every command may declare an optional icon source.

Three conceptual icon modes are approved:

- Automatic.
- CustomFile.
- BuiltIn.

### Automatic default

By default, Disorderly determines the icon from the application actually used by the command.

Examples:

- Launching Chrome uses the icon extracted from the installed Chrome executable.
- Executing a PowerShell script through PowerShell uses the interpreter's icon.
- Opening a `.txt` file through the current default application uses the effective associated application's icon.
- Explicitly opening a file with a selected application uses that selected application's icon.

Do not assume a script file's extension association is the same application as the interpreter used to execute it.

### User override

Users may select:

- A custom icon file by path.
- An icon from Disorderly's bundled generic icon collection.

### Bundled assets

Disorderly should not need to ship third-party application logos to support automatic application icons.

Bundled generic icons must use appropriately licensed assets.

### Safety and caching

Resolving an icon must never execute the command.

Missing or invalid icons must not prevent command execution.

Caching is permitted, but icon results must eventually reflect changed executables, file associations, and user-selected icon resources.

Icon metadata does not define the identity or behavior of a command.

---

## Deferred Features and Implementation Boundaries

The following remain **Settled** and **Deferred** after Draft Management I; they are not currently implemented:

- Execution kinds beyond direct executable launching, typed command search and the binding editor.
- Command discovery, parameter pills, autocomplete and guided prompting beyond the initial field editor.
- Durable draft persistence.
- Interactive organization sessions.
- Background operation scheduler.
- SQLite operation journal.
- Crash recovery and reconciliation.
- Undo infrastructure.
- Activity and notifications.
- Automatic application icon resolution.
- User-facing icon customization, extraction and rendering (icon metadata is implemented).
- Future parallel file mutation on independent resources.

Describing these features in this specification does not make them implemented.

Globally serialized mutation is the initial accepted implementation strategy. Future nonconflicting parallelism is an approved architectural extension, not an implemented capability.

## Initial Implementation Sequence (Nonbinding)

This illustrative, nonbinding roadmap identifies reasonable incremental checkpoints:

1. **Design Baseline I:** Record these decisions in documentation.
2. **Context Routing I:** Context definitions, bindings, routing, validation, navigation, and tests.
3. **Command Model I:** Command definitions, invocation identity, execution contracts, and initial argument model.
4. **Palette Interaction I:** Connect contextual routing to the WPF palette.
5. **Draft Management I:** Multiple in-memory drafts and root-first Resume behavior.
6. **Operation Infrastructure I:** Operation states, scheduling contracts, pause/cancel requests, and Activity model.
7. **Filesystem Execution and Recovery:** Journal-driven, race-aware file operations built in small, separately testable slices.
8. **Subsequent features:** Durable draft persistence, Undo enhancements, notifications, icon customization, and user-facing organization workflows.

This roadmap is illustrative. It must not override the settled architectural contracts or force premature implementation of later features.

## Unsettled Details and Future Design Work

Context Routing I and Command Model I implement their domain contracts without settling the remaining deferred subsystems. This baseline leaves concrete configuration/storage schemas, scheduling time slices or fairness quotas, and the eventual filesystem mechanisms to later design and implementation checkpoints under the contracts above. Durable draft persistence and independent-resource parallel mutation are approved enhancements with explicitly deferred implementation, not unresolved alternatives to the initial in-memory and serialized strategies.
