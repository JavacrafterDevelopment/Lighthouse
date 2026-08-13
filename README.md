# Lighthouse

Instant file search for Windows. Type a few letters, see every matching file and
folder on every drive, with the real icons Explorer would show.

Lighthouse reads the NTFS master file table directly instead of crawling folders,
so a first index of a few million files takes seconds, and searching afterwards
takes single-digit milliseconds.

---

## Running it

Copy the `dist\Lighthouse` folder anywhere — a USB stick, another PC, a network
share — and run `Lighthouse.exe`. Nothing is installed, nothing is written outside
the folder, and there is no runtime to download: the .NET runtime is published
inside it.

On launch Lighthouse asks for administrator rights. It needs them to open a raw
volume handle, which is the only way to read the file table. **If you decline, it
still works** — it falls back to walking folders instead, which is slower to index
and only covers what your account can read. The status bar says which mode you are in.

| | |
|---|---|
| **Ctrl+Shift+Space** | Show or hide Lighthouse from anywhere |
| **Enter** | Open the selected item |
| **Ctrl+Enter** | Show it in Explorer |
| **Menu key** / **Shift+F10** | Open the context menu for the selection |
| **F5** | Re-index every drive from scratch |
| **Ctrl+C** | Copy the full path |
| **Ctrl+L** / **Ctrl+F** | Jump back to the search box |
| **Esc** | Clear the search, then hide to tray |

Closing the window leaves Lighthouse in the tray so the index survives; **Exit**
from the tray menu quits for real.

### Command line

```
Lighthouse.exe --query minecraft     Open with a search already typed
Lighthouse.exe --filter apps         Start with a filter on (apps|files|folders)
Lighthouse.exe --tidy                Start with the clutter filter on
Lighthouse.exe --no-elevate          Skip the admin prompt, use the folder walk
```

Diagnostics, for when something looks wrong:

```
Lighthouse.exe --mft-selftest              Scan every NTFS volume and report
Lighthouse.exe --icon-selftest             Dump icon lookups to %TEMP%\lighthouse-icons
Lighthouse.exe --menu-selftest "<path>"    Print the shell context menu for a file
Lighthouse.exe --open-menu                 Open the menu on the first result at startup
```

---

## Searching

Typing `min` shows everything whose name starts with `min`, then names where `min`
starts a word (`My-Minecraft-Backup`), then names containing it anywhere. Adding
letters narrows what is already on screen. Turn on **Starts with** to keep only
true prefix matches.

- Extensions are part of the name, so `minecraft.exe` and `.zip` both work as queries.
- A backslash searches paths too: `games\mine` matches items named `mine…` sitting
  under a folder path containing `games`.
- **Apps**, **Folders** and **Files** restrict the result type. Apps means anything
  you launch — `.exe`, `.lnk`, `.msi`, `.bat`, `.cmd`, `.com`, `.url`, `.appref-ms`,
  `.msc`, `.cpl`, `.scr`.
- **Tidy** hides the machinery. One switch, no configuration: anything inside
  `node_modules`, `.git`, `site-packages`, `WinSxS`, `$Recycle.Bin` and their
  kind, plus build and platform artefacts (`.dll`, `.pdb`, `.obj`, `.pyc`, `.sys`,
  `.manifest` …), NTFS metafiles and system-flagged files. On a typical dev
  machine it removes roughly half the results — searching `min` here drops from
  18,547 hits to 9,761, and the ones left are things a person was actually
  looking for.

  It works by marking each entry once per index, walking up the parent chain and
  memoising the verdict on every ancestor it passes, so the check costs nothing
  per keystroke.

## Right-clicking

The context menu is Explorer's own, read live from the shell for that exact file,
then drawn in Lighthouse's styling. Whatever is installed shows up: 7-Zip's
cascading submenu, "Edit with Notepad++", Open with, Send to, Give access to,
Properties. Icons that extensions supply come through too.

Hold **Shift** while right-clicking for the extended verbs Explorer hides behind
the same shortcut. Lighthouse's own **Copy full path**, **Copy name** and **Open
containing folder** sit at the bottom, below whatever Windows offered.

---

## How it works

```
  NTFS volume ──FSCTL_ENUM_USN_DATA──► VolumeScanner ──► FileIndex ──► SearchEngine
       │                                                     ▲              │
       └────────FSCTL_READ_USN_JOURNAL──► UsnMonitor ────────┘              │
                                                                           ▼
  Explorer's icon cache ◄──SHGetFileInfo──── IconProvider ────► WebView2 front-end
```

**Reading the drive.** `VolumeScanner` walks the master file table with
`FSCTL_ENUM_USN_DATA`, which returns every record in one sequential pass — file
reference number, parent reference number, attributes and name. Parents are then
resolved into array indices, so a full path is a walk up a chain of ints.

**Holding it in memory.** `FileIndex` is struct-of-arrays with every name packed
into one UTF-8 pool. A few million entries cost around 100 MB rather than the
~1 GB the same data would take as objects. It is append-only and arrays are
replaced wholesale when they grow, so searches read it without locking.

**Searching it.** `SearchEngine` matches over the raw byte pool with a vectorised
first-byte skip. Because UTF-8 continuation bytes are always ≥ 0x80, an ASCII
query can be matched against the bytes directly and still be correct for names in
any language. Extending a query rescans only the previous result set, so each
keystroke gets faster rather than slower. Results are ranked exact → prefix → word
start → anywhere, and only the rows actually on screen get sorted precisely.

**Keeping it current.** `UsnMonitor` tails the NTFS change journal, so files
created, renamed or deleted after startup are reflected without another scan.

**Re-indexing.** The button at the bottom left (or **F5**) throws the index away
and scans everything again — useful after moving a lot of files around, or on a
non-NTFS volume where there is no journal to follow. It scans into a brand new
index and swaps it in only when finished, so searching keeps working against the
old one throughout, selection and scroll position intact.

**Icons.** `IconProvider` asks the shell the same question Explorer asks. Types
whose icon comes from the file association (`.zip`, `.txt`) are resolved from a
synthetic path with `SHGFI_USEFILEATTRIBUTES` and cached per extension — so if you
point `.zip` at 7-Zip, Lighthouse shows the 7-Zip icon, because it is reading your
association rather than guessing. Types whose icon lives inside the file (`.exe`,
`.lnk`, `.ico`) are read per path. Icons are served to the UI over an internal
origin that the browser caches.

**The context menu.** `ShellContextMenu` asks the shell for the file's real
`IContextMenu`, populates an off-screen `HMENU` with it, then walks that menu with
`GetMenuItemInfo` — labels, states, submenus and the bitmaps extensions attach —
and hands the tree to the UI as plain data. Choosing an entry calls back into
`IContextMenu::InvokeCommand`, so the command runs exactly as it would from a
folder window.

**Launching things.** Lighthouse runs elevated, and a child process would inherit
that. Opening an item therefore goes through `explorer.exe`, which hands off to
the desktop shell already running at your normal integrity level — so a program
opened from search results does *not* silently run as administrator. The context
menu's plain **Open** is routed the same way, detected by its canonical verb via
`GetCommandString`, so it behaves identically to a double-click.

---

## Building from source

Requires the .NET 9 SDK.

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output lands in `dist\Lighthouse`. Add `-Zip` to also produce a distributable
archive.

```
src/Lighthouse/
  Program.cs              entry point, elevation handling
  MainForm.cs             window shell, WebView2 bridge, icon endpoint
  Indexing/
    FileIndex.cs          packed store of every file and folder
    VolumeScanner.cs      master file table enumeration
    SearchEngine.cs       ranking, incremental narrowing, paging
    UsnMonitor.cs         live change-journal updates
    IndexService.cs       orchestration + non-elevated fallback walk
  Shell/
    IconProvider.cs       shell icons, cached per extension or path
    ShellContextMenu.cs   reads Explorer's real IContextMenu as data
    GdiPng.cs             HICON / HBITMAP → PNG, alpha intact
    FileTypeNames.cs      Explorer's Type column text
    ShellLauncher.cs      de-elevated open / reveal
  wwwroot/                the interface (HTML, CSS, JS)
tools/make-icon.ps1       draws lighthouse.ico
```

### Notes

- **WebView2 runtime.** Ships with Windows 11 and current Windows 10. If it is
  missing, Lighthouse says so and links to the installer. To make the folder
  truly self-sufficient on old machines, drop a fixed-version WebView2 runtime
  into the folder and point `CoreWebView2Environment.CreateAsync` at it via its
  `browserExecutableFolder` argument in `MainForm.OnLoad`.
- **Non-NTFS volumes** (exFAT, FAT32) have no master file table to read, so they
  are indexed with the folder walk instead. This is automatic and per volume.
- **File sizes and dates** are not stored in the index; they are fetched for the
  rows on screen only, which keeps memory flat regardless of drive size.
- **Context menu commands run elevated.** Other than plain Open, verbs invoked
  from the menu execute inside Lighthouse, which is running as administrator —
  so "Extract Here" produces admin-owned files, for instance. This is inherent to
  hosting `IContextMenu` in an elevated process; Explorer has the same behaviour
  when it is itself elevated. Third-party shell extension DLLs are likewise
  loaded into the elevated process, exactly as Explorer loads them.
