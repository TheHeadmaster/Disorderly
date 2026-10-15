# Disorderly

A tray-resident WPF utility on .NET 10. Foundation I adds a root command palette, opened with **Ctrl+Shift+D** or **Open Command Palette** in the tray menu. No commands, search, shortcuts beyond activation/Escape, or saved drafts are implemented yet.

## Build and automated validation

On Windows with the .NET 10 SDK:

```powershell
dotnet restore Disorderly.slnx
dotnet build Disorderly.slnx --configuration Release --no-restore
dotnet test Disorderly.slnx --configuration Release --no-build --no-restore
```

The existing Windows GitHub Actions workflow runs restore, Release build and NUnit tests on push. Tests cover registration ownership/failure, palette lifecycle with a fake view, physical placement at multiple scales/origins, and the existing MainWindow construction test. Fake registrations never reserve a real global hotkey. Interactive behavior must be verified separately.

## Implementation notes

- `ReactiveUI.Primitives.ObservableEvents` is pinned to **9.0.0**, preserving `PrivateAssets="all"`. NuGet's exact-package flat-container index lists both 8.4.0 and 9.0.0; 9.0.0 is the latest stable listed version at implementation time (published October 3, 2026). No unrelated dependency versions changed.
- `app.manifest` establishes PerMonitorV2 awareness before WPF initialization, with PerMonitor fallback. WPF owns DPI scaling and WM_DPICHANGED processing.
- Desktop monitor/work rectangles and native window placement use physical pixels, including negative virtual coordinates. Only DIP dimensions, pointer anchors and margins are scaled. Palette positioning first moves its hidden HWND to the selected monitor, reads that window's DPI, measures WPF content, clamps dimensions, then anchors to the work area's upper-right corner. Each activation reselects the foreground application's monitor; cursor and primary-monitor fallbacks never depend on the palette's previous location. Visible palettes re-anchor on DPI/display/work-area changes.
- Custom desktop windows use monitor-relative native maximize bounds, supported visual DPI, and physical drag positioning. No reflected SystemParameters DPI or hardcoded DPI padding tables remain.
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
