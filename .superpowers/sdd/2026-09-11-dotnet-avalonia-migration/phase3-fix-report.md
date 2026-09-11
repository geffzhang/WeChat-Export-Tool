# Phase 3 fix wave — report

Repo `C:\workshop\github\WeChat-Export-Tool`, project `WeChatExport/`, branch `main`,
fix wave applied on top of `12986bf` and committed as the one commit
`fix: phase 3 fix wave - shard leak, silent shard failure, zstd bound, Excel
surrogate cut` (three files: `Services/DatabaseService.cs`,
`Services/ExportService.cs`, this report; `research/README.md` and the untracked
`TestAvalonia/` were left alone).

**All four items fixed: F1, F2, F3, F4.** No behaviour on the measured happy path
changed (`GetMessages`/`GetContacts`/`Connect` results on the real install are
byte-for-byte what they were: the full harness is 0 failures, including every check
that existed before this wave).

`cd WeChatExport && dotnet build` → **0 errors, 0 warnings** (Debug and Release — see
Build below).

Bugs the fix wave was NOT to touch (`Message.ContentUnsupported` is set but read by
nobody; `Pooling = false` only on the keyed paths; the `Disconnect`-during-load UI
race) were **not** touched — see Backlog. I also did not touch the review's *other*
finding numbered F4 (the account-folder owner route at `DatabaseService.cs:898-915`)
— it is not in the fix brief's list, and the brief's F4 is the Excel surrogate one.

---

## F1 — fixed — `SchemaMismatch` leaked every sibling shard connection

**What changed** (`WeChatExport/Services/DatabaseService.cs`)

1. `DatabaseService.cs:646-654` — the `SchemaMismatch` cleanup now disposes **before**
   it clears, matching the two sibling catch paths (`:684-685`, `:700-701`):

   ```csharp
   // Dispose first, THEN clear: DisconnectInternal() replaces _shards with an
   // empty list WITHOUT disposing anything, so the reverse order ... made
   // DisposeConnections()'s loop iterate an empty list and leak every sibling
   // shard connection - handle, key material and all - ...
   DisposeConnections();
   DisconnectInternal();
   ```

2. `DatabaseService.cs:2487-2506` (`DisposeConnections`) — the `ReferenceEquals`
   guard was dead because `_connection` was nulled on the line above it, so the
   primary was disposed twice. The primary reference is now snapshotted before the
   field is cleared, so the guard means what it says:

   ```csharp
   var primary = _connection;
   _connection = null;
   primary?.Dispose();
   foreach (var shard in _shards)
       if (!ReferenceEquals(shard.Connection, primary))
           shard.Connection.Dispose();
   ```

Effect on the happy path: none. `DisposeConnections` is only reached from
`Disconnect()`, the `Connect` failure paths and the `SchemaMismatch` branch, and
every one of them clears `_shards` immediately afterwards; the only difference is
that the primary is disposed once instead of twice (the review verified the second
`Dispose()` was harmless).

**How verified** — new harness check (section 10 of `%TEMP%\realdb\Program.cs`). A
scratch install **outside the repo** (`%TEMP%\realdb\work\fixwave\leak`): three
SQLCipher databases keyed with a *passphrase* (the one key form whose connections are
always unpooled) holding a single `NotAMessageTable`, so `Connect(primary)` ends in
`SchemaMismatch`. The service is disposed, then each file is probed with an exclusive
open (`FileShare.None`), which fails exactly while a SQLite handle is held.

```
post-fix (working tree):
  [PASS] [new] F1: a SchemaMismatch connect disposes every shard connection it opened  -- outcome=SchemaMismatch (SchemaMismatch proves the scratch key worked), message_0.db=free, message_1.db=free, message_2.db=free

pre-fix (12986bf, -p:SrcRoot=%TEMP%\pre12986\WeChatExport):
  [FAIL] [new] F1: a SchemaMismatch connect disposes every shard connection it opened  -- outcome=SchemaMismatch (SchemaMismatch proves the scratch key worked), message_0.db=free, message_1.db=HELD, message_2.db=HELD
```

The pre-fix signature is exactly the review's: the primary is free (it went through
`DisposeConnections`), the siblings are held (the loop never ran) and stay held after
`Dispose()`, i.e. for the life of the process. On `2841dd1` this check **passes** —
not a false green: that revision has no sibling-shard code at all, so it opens one
connection and leaks nothing; its red proof is the `12986bf` run above.

Scratch note: the harness creates its own scratch databases with `Pooling = false`.
Without that flag the driver's own pool held all three files and the first version of
this check read `HELD` on all three **post-fix** — a harness artifact, not a
production leak. Fixed in the harness, not in the product.

---

## F2 — fixed — an unopenable shard produced `Success` + a short list

**What changed** (`WeChatExport/Services/DatabaseService.cs`)

The shard set is connection state, so the failure is recorded at connect time and
reported by every reader:

| where | line | what |
| --- | --- | --- |
| new field | `:416` | `private List<string> _unopenedShards = new();` |
| new public property | `:471-486` | `public IReadOnlyList<string> UnopenedShards => _unopenedShards;` (+ R10 remarks) |
| new helper | `:501-509` | `UnopenedShardWarning()` → `"N message shard(s) could not be opened, so messages stored in them are missing from this result: <names>."`, or `null` when every shard opened |
| `OpenShards` catch | `:818` | `_unopenedShards.Add(shardPath);` — the warning log line stays, but the log is not the result |
| `DescribeSchema` | `:860-864` | the `Success` message now ends with `WARNING: 1 message shard(s) could not be opened, ...` |
| `GetContacts` | `:1239` | `LastError ??= UnopenedShardWarning();` |
| `GetMessages` | `:1597` | `LastError ??= UnopenedShardWarning();` |
| `DisconnectInternal` | `:2518` | `_unopenedShards = new List<string>();` — the list belongs to the connection |

I used the **existing honest-degradation pattern** (set `LastError` on a read that
succeeded but is knowingly incomplete — exactly what the degraded `GetContacts`
branch at `:1215-1223` already does) rather than inventing a new mechanism, and I
**did not overload `LastResultTruncated`**. Reasons, stated in the code:

* `LastResultTruncated` means "the newest `limit` messages were returned and older
  ones exist" — a property of one query, fixable by asking for more.
* an unopenable shard is a property of the **connection**, is not fixable by
  re-querying, and shrinks the count for the opposite reason (the *newest* messages
  can be the missing ones). Folding the two into one flag would make the UI line
  wrong in one of the two cases.

`LastError` is what the UI already surfaces (`MainWindowViewModel.cs:320`, `:398-412`
prefer it over the success/truncation text), so the user sees
"1 message shard(s) could not be opened, so messages stored in them are missing from
this result: message_1.db." instead of "Loaded 14497 messages". `Connect`'s status
line names it too. The `Connect` outcome stays `Success`, deliberately: R10 says a
degraded path continues, and the messages that *did* open are still exportable — they
just may not claim completeness.

**How verified** — new harness check (section 11). It builds
`%TEMP%\realdb\work\fixwave\shardfail\db_storage\` from **hard links** to the real
`message\message_0.db` and `session\session.db` (`CreateHardLinkW`; no copies, nothing
written under `db_storage`), and replaces `message_1.db` with a 4 KB file that cannot
be opened with the key. The same chat is read twice: once from the real install (all
shards open) as the control, once from the scratch tree.

```
post-fix (working tree):
  [PASS] [new] F2: an unopenable shard is surfaced, never a silently short result  -- control (real install, both shards): 18915 of 18915 msgs, LastError=<null>; scratch layout ...\work\fixwave\shardfail (message_1.db unopenable): outcome=Success, 14497 msgs (m0 alone=14497, union=18915, m1=4418), LastError=1 message shard(s) could not be opened, so messages stored in them are missing from this r…(+20), UnopenedShards=[message_1.db], truncated=False, contacts=2240

pre-fix (12986bf):
  [FAIL] [new] F2: an unopenable shard is surfaced, never a silently short result  -- control (real install, both shards): 18915 of 18915 msgs, LastError=<null>; scratch layout ...\work\fixwave\shardfail (message_1.db unopenable): outcome=Success, 14497 msgs (m0 alone=14497, union=18915, m1=4418), LastError=<null>, UnopenedShards=[], truncated=False, contacts=2240
```

That is the review's reproduction, reproduced by the harness on a chat of the same
shape: **14,497 of 18,915 messages** (4,418 missing, 23%) with `LastError == null`,
`LastResultTruncated == false`, and nothing in the API or the UI to say so. The check
also fails if the warning is *invented* for a complete install (the control read must
stay silent) and if `LastResultTruncated` starts meaning this (`!brokenTruncated`).

Note the same chat's numbers differ from the review's (2753/3966 for
`34785466910@chatroom`): the check picks the dual-shard conversation with the largest
`message_1.db` share dynamically instead of hard-coding one, so it stays valid if the
user's data moves on.

---

## F3 — fixed — zstd decompression was unbounded from the call site

**What changed** (`WeChatExport/Services/DatabaseService.cs`)

* `:161-177` — new `private const int MaxDecompressedContentBytes = 16 * 1024 * 1024;`
  with the measurements that justify it (8,211 bytes → 256 MB; 4 corrupt header bytes
  → a 1,000,000,024-byte allocation; real content peaks at 189,764 chars over 283,458
  compressed rows, ~88x below the ceiling).
* `:1752-1755` — `decompressor.Unwrap(bytes)` → `decompressor.Unwrap(bytes, MaxDecompressedContentBytes)`.

Exceeding the bound lands in the **existing** honest path: the `catch (Exception)` at
`:1762` replaces the content with `[unsupported compressed content]` and sets
`ContentUnsupported`, per message, so no export aborts and no raw bytes reach a file.

**How verified** — new harness check (section 12) that calls the production private
`ReadContent` (via reflection) with a 659-byte zstd frame declaring 20 MB, inserted
into a plain SQLite table with `WCDB_CT_message_content = 4`:

```
post-fix (working tree):
[WRN] Could not decompress the content of t local_id=1 (659 bytes)
ZstdSharp.ZstdException: Decompressed content size 20971520 is greater than maxDecompressedSize 16777216
   at ZstdSharp.Decompressor.Unwrap(ReadOnlySpan`1 src, Int32 maxDecompressedSize)
   at WeChatExport.Services.DatabaseService.ReadContent(...) in ...\DatabaseService.cs:line 1754
  [PASS] [new] F3: a zstd frame declaring more than the ceiling degrades to the unsupported marker  -- frame declares 20 MB in 659 bytes -> content=len=32, unsupported=True

pre-fix (12986bf):
  [FAIL] [new] F3: a zstd frame declaring more than the ceiling degrades to the unsupported marker  -- frame declares 20 MB in 659 bytes -> content=len=20971520, unsupported=False
```

The pre-fix line is the defect: the frame's own header decided a 20 MB allocation
(and, in the review's 256 MB/1 GB cases, far more). 20 MB was chosen so the A/B run
allocates ~40 MB rather than ~1 GB.

Real data is unaffected: the harness's R8 checks (all `WCDB_CT_message_content = 4`
rows of a real conversation decompressed here and compared string-for-string against
the production read) still pass, as does "no binary mojibake reached the JSON export".

---

## F4 — fixed — the Excel cut could split a UTF-16 surrogate pair

**What changed** (`WeChatExport/Services/ExportService.cs:278-302`)

```csharp
var take = ExcelMaxCellLength - marker.Length;
// Do not split a surrogate pair: a lone surrogate is not a character.
if (char.IsHighSurrogate(text[take - 1]))
    take--;

return text[..take] + marker;
```

with a remark explaining why stepping back on a **high** surrogate is sufficient (a
low surrogate can only be reached through its high surrogate, so it can never end up
alone this way).

**How verified** — new harness check (section 13), calling `ExcelCell` via reflection
with a 32,806-unit string whose high surrogate sits exactly on the old cut point
(index 32,754), plus the exact-limit and one-under passthrough cases:

```
post-fix (working tree):
  input 32806 units (high surrogate kept at index 32754 before the fix)
  cell  32766 units, marker=True, loneSurrogate=False
  [PASS] [new] F4: the 32,767-unit Excel cut does not split a surrogate pair  -- len=32766, loneSurrogate=False, passthrough=True

pre-fix (12986bf):
  [FAIL] [new] F4: the 32,767-unit Excel cut does not split a surrogate pair  -- len=32767, loneSurrogate=True, passthrough=True (last units: uncated])
```

`passthrough=True` in both runs confirms the change did not start truncating short
values (32,766 and 32,767 units pass through unchanged); the fix is one unit of
shorter output only when the cut would have split a pair.

---

## Harness counts

Harness `%TEMP%\realdb` (`dotnet run -c Release`, `-p:SrcRoot=<revision>`). Four
checks were added (F1-F4, all `[new]`/failable); **no existing check was weakened,
deleted or skipped**, and the guard set is unchanged at 8.

| run | command | result |
| --- | --- | --- |
| post-fix (working tree) | `dotnet run -c Release` | **`52 checks (44 new/failable, 8 guard), 0 failures`** |
| pre-fix `12986bf` (the revision under repair, `git archive HEAD`) | `-p:SrcRoot=%TEMP%\pre12986\WeChatExport` | `52 checks (44 new/failable, 8 guard), 4 failures` — exactly the four new checks |
| pre-change `2841dd1` | `-p:SrcRoot=%TEMP%\pre2841\WeChatExport` | `52 checks (44 new/failable, 8 guard), 29 failures` (was 26 before the wave; **not below 26**) |

Per-check, pre-fix `12986bf` (new checks only; the 48 pre-existing checks were
unchanged and all passed, which is why the pre-fix failure count is 4 and not 30):

```
  [FAIL] [new] F1: a SchemaMismatch connect disposes every shard connection it opened  -- ... message_1.db=HELD, message_2.db=HELD
  [FAIL] [new] F2: an unopenable shard is surfaced, never a silently short result  -- ... 14497 msgs (m0 alone=14497, union=18915, m1=4418), LastError=<null>, UnopenedShards=[], ...
  [FAIL] [new] F3: a zstd frame declaring more than the ceiling degrades to the unsupported marker  -- ... content=len=20971520, unsupported=False
  [FAIL] [new] F4: the 32,767-unit Excel cut does not split a surrogate pair  -- len=32767, loneSurrogate=True, passthrough=True
=== RESULT: 52 checks (44 new/failable, 8 guard), 4 failures ===
```

On `2841dd1` the four new checks are F1 PASS / F2 FAIL / F3 FAIL / F4 FAIL
(`F2`'s control read cannot even see the second shard: `14497 of 18915 msgs`;
`F3`/`F4` report `ReadContent absent` / `ExcelCell absent`, the harness's documented
"needs new API" behaviour). F1 passes there because `2841dd1` has no sibling-shard
code and therefore nothing to leak — its red proof is the `12986bf` run.

Source identity was confirmed, not assumed: the two A/B runs log different source
line numbers for the same stack frames (`OpenShards` at `719` pre-fix vs `799`
post-fix; `OpenRawKeyConnection` at `2331` vs `2433`), and the pre-fix run's
`LastResultTruncated` checks compile through reflection only.

Raw output files: `%TEMP%\realdb\run-post-fixwave.txt`,
`run-pre12986-fixwave.txt`, `run-pre2841-fixwave.txt`.

### Six export formats, re-run from real records

From the post-fix harness run (section 4, real chat records off the user's install):

```
  [PASS] [new] JSON  export (json)  -- 103304 bytes
  [PASS] [new] CSV   export (csv)  -- 55181 bytes
  [PASS] [new] TXT   export (txt)  -- 52106 bytes
  [PASS] [new] HTML  export (html)  -- 86005 bytes
  [PASS] [new] Excel export (xlsx)  -- 22458 bytes
  [PASS] [new] PDF   export (pdf)  -- 505800 bytes
  [PASS] [new] JSON contains literal CJK (not \uXXXX escapes)
  [PASS] [new] JSON parses
  [PASS] [new] R8: no binary mojibake reached the JSON export
```

Each is checked for a well-formed signature (and the JSON for parseability, literal
CJK and no replacement characters).

### Build

```
$ cd WeChatExport && dotnet build
  WeChatExport -> ...\bin\Debug\net10.0-windows\win-x64\WeChatExport.dll
已成功生成。 0 个警告 0 个错误

$ cd WeChatExport && dotnet build -c Release
已成功生成。 0 个警告 0 个错误
```

---

## Not fixed — backlog (as instructed)

* `Message.ContentUnsupported` is set (`DatabaseService.cs:1699`) and read by nobody,
  so no view or export can render the marker as "not a message". Cosmetic (0 of
  283,458 real flagged rows fail to decompress), untouched.
* `Pooling = false` is set only on the keyed paths (`OpenKeyedConnection`,
  `OpenRawKeyConnection`). An unencrypted file opened with no key still pools, so a
  disposed connection keeps its handle until the pool trims. Only reachable for the
  wrong-key/no-key attempt on an unencrypted file; every real WeChat database takes
  the keyed path. Untouched.
* `Disconnect` during an in-flight load: `DisconnectCommand` is gated only by
  `IsConnected`, so it stays enabled while `LoadMessages` runs on a worker thread and
  the continuation can repopulate `Messages` after `Disconnect` cleared them. Plausible
  from the code (threading conclusions here are still code-level), not run. Untouched.
* The review's *own* F4 (`GetOwnerAliasCandidates` never yields the account folder for
  the 4.x layout, so `DeriveOwnerFromChats` runs on every connect) is Low, is not in
  the fix brief's item list, and was not touched. Reporting it here only so the two
  things called "F4" are not confused.

## UNVERIFIED / ceiling

* The **GUI was not run**. The claim "the user can tell a partial result from a
  complete one" (F2) is verified at the API level — `LastError`, the `Connect`
  message and `UnopenedShards` all carry it, and `MainWindowViewModel.cs:320`/`:398-412`
  displays `LastError` verbatim — but not by clicking through the window.
* The unopenable shard in the F2 check is a **stand-in** (4 KB of zeros, i.e. "not a
  database"), not a genuinely locked/truncated/mid-rotation real shard. The review
  took the same shortcut; no real locked shard was available while WeChat runs.
* F3's bound was exercised with a synthetic 20 MB frame. The "real content peaks at
  189,764 chars over 283,458 compressed rows" figure is the **review's measurement**,
  not re-measured in this wave; what this wave did re-run is the harness's R8 path,
  which decompresses every flagged row of a real conversation and requires an exact
  string match with the production read (it passes).
* F4's fix was verified on `ExcelCell`'s output (the function that writes the cell).
  The workbook was not re-opened to read the cell back; the review established that
  ClosedXML tolerates a lone surrogate by writing a replacement character, which is
  why the defect was silent rather than fatal.
* One account, one machine — as in every wave of this migration.
