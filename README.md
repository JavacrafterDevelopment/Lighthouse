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
| **Ctrl+C** | Copy the full path |
| **Ctrl+L** / **Ctrl+F** | Jump back to the search box |
| **Esc** | Clear the search, then hide to tray |

Closing the window leaves Lighthouse in the tray so the index survives; **Exit**
from the tray menu quits for real.

### Command line

```
Lighthouse.exe --query minecraft     Open with a search already typed
Lighthouse.exe --no-elevate          Skip the admin prompt, use the folder walk
Lighthouse.exe --icon-selftest       Dump shell icon lookups to %TEMP%\lighthouse-icons
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
- **Folders** and **Files** restrict the result type.

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

**Icons.** `IconProvider` asks the shell the same question Explorer asks. Types
whose icon comes from the file association (`.zip`, `.txt`) are resolved from a
synthetic path with `SHGFI_USEFILEATTRIBUTES` and cached per extension — so if you
point `.zip` at 7-Zip, Lighthouse shows the 7-Zip icon, because it is reading your
association rather than guessing. Types whose icon lives inside the file (`.exe`,
`.lnk`, `.ico`) are read per path. Icons are served to the UI over an internal
origin that the browser caches.

**Launching things.** Lighthouse runs elevated, and a child process would inherit
that. Opening an item therefore goes through `explorer.exe`, which hands off to
the desktop shell already running at your normal integrity level — so a program
opened from search results does *not* silently run as administrator.

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
    IconProvider.cs       HICON → PNG, cached per extension or path
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
