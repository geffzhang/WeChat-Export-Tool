# Phase 2 — Realign the data layer to WeChat 4.x's real schema

Brief: `.superpowers/sdd/2026-09-11-dotnet-avalonia-migration/schema-realign-brief.md`
Pre-change revision: `ad16107` (`fix: fix wave 3 - wxid conversation filter, keyed-connection leak, 3.x candidate`) — that is `HEAD`; the phase is an uncommitted working-tree change.
Harness: `%TEMP%\phase2\` (outside the repo, sources linked not copied).

## The ceiling — read this before the rest

**There is no real WeChat database in this environment.** Every schema claim below
was tested against a fixture that I built from this repo's own research dumps. A
fixture built to the documented schema proves **the code matches the
documentation**. It does **not** prove the documentation matches a real WeChat
4.x install. All schema assumptions are therefore marked **UNVERIFIED** below,
each with the research reference it came from. What *is* verified is narrower and
should be read that narrowly: the mapping logic, the fallbacks, the export path
and the absence of regressions behave as documented against a database laid out
the way the research says WeChat lays one out.

There is also no CI here. The two harness projects and their raw outputs live in
`%TEMP%` and are pasted verbatim in this report — they are not committed to the
repo.

---

## 1. What changed

| File | Change | Lines |
|---|---|---|
| `WeChatExport/Services/DatabaseService.cs` | Rewritten data layer: `Msg_*` discovery, `SessionTable` conversation list, `real_sender_id` resolution, `contact.db` name lookup, `Msg_`-table fallback listing, no `ChatInfo` | 1554 lines total (949 insertions / 306 deletions vs `ad16107`) |
| `WeChatExport/Services/WeChatPathService.cs` | WeChat 4.x path layout (`db_storage\message\message_*.db`), account-folder descent | +100 / −? (`git diff --stat` below) |

```
 WeChatExport/Services/DatabaseService.cs   | 1155 +++++++++++++++++++++-------
 WeChatExport/Services/WeChatPathService.cs |  100 ++-
 2 files changed, 949 insertions(+), 306 deletions(-)
```

Nothing else was touched. `Message`/`Contact`/`Conversation`, `CryptoUtils`,
`ExportService`, `MainWindowViewModel` and `MainWindow.axaml` are byte-identical
to `ad16107` — the brief said to extend the models rather than redesign them, and
on inspection they already fit the real schema without modification
(`Contact.Identifier` is a string, so it holds a wxid; `Message.SenderIdentifier`
is a string; `UnreadCount`/`LastMessageTime`/`LastMessagePreview` already exist).
The ViewModel already calls `GetMessages(contact.Identifier, 1000, contact.DisplayName, senderNames)`,
which is exactly the shape the real schema needs, so it needed no edit.

### The decryption path (R7) — unchanged

Deliberately left byte-identical: the `KeyCandidate` list, `RawKeyCandidates`
derivation, `Connect`'s key normalisation, `OpenKeyedConnection`,
`OpenRawKeyConnection`, `ReadSalt`, `Pooling = false`, the `SQLITE_NOTADB` (26)
split, and the honest outcome reporting. The only edits inside `Connect` are
*after* the decryption probe succeeds (table discovery + owner wxid) and in the
success/summary strings. The harness re-checks the round trip and it still
passes — see section 4.

---

## 2. How this was verified

One shared `Program.cs` (`%TEMP%\phase2\Program.cs`) is compiled twice:

- `post/post.csproj` — `AssemblyName phase2post`, links the **live** production
  sources by absolute path (`<Compile Include="C:\workshop\...\DatabaseService.cs">`).
- `pre/pre.csproj` — `AssemblyName phase2pre`, links frozen copies in
  `pre-src/` produced with
  `git show ad16107:WeChatExport/Services/DatabaseService.cs` and
  `.../WeChatPathService.cs`.

```
13ee72a30de5a5dce35267d50602d460 *pre-src/DatabaseService.cs
f8a3785f4882dd462c4559e301fcae28 *pre-src/WeChatPathService.cs
```

Both projects link the same unchanged models, `CryptoUtils.cs` and
`ExportService.cs`, so the only variable between the two runs is the two files
this phase touched. Every check is wrapped in a `Section(...)` so a throwing
section records exactly one failed check instead of aborting the run — this is
what keeps the check count identical (41) across both revisions.

### Fixtures (`%TEMP%\phase2-run\`)

Built once, at the top of the run, by the shared harness itself:

- **rootA** — the WeChat 4.x tree
  `rootA\xwechat_files\wxid_caccoealsdbj12_e8c8\db_storage\message\message_0.db`,
  containing a 19-column `SessionTable` (the real dump from
  `research/experiments/m89/all_schema.sql:822`) with 3 rows, plus two
  `Msg_<hash>` tables whose hashes are the **literal** MD5s of the usernames.
  Rowids: `1=wxid_bob`, `2=12345@chatroom`, `3=wxid_caccoealsdbj12` (the owner).
- **rootB** — rootA plus `db_storage\contact\contact.db` with
  `user_info(user_name, nickname, remark, sex)`. The column names are deliberately
  the *non-obvious* variants so candidate discovery is actually exercised.
- **rootC** — a `Msg_` table with **no** `SessionTable` (the R9 degraded case).
- **rootD** — a plain SQLite file carrying `ChatInfo` + `DeleteInfo` + `TimeStamp`
  and no `Msg_` table (the "old invented schema" case that must now be rejected).
- **empty/empty.db** — only a `nothing_here` table.

The message content includes CJK, an embedded newline and an embedded double
quote: `你好，这是中文消息 "带引号"\n第二行`.

Encrypted fixtures are keyed through **`raw.sqlite3_key(handle, 32 raw bytes)`**
(WeChat's own keying, distinct from this app's PBKDF2 derivation), which is what
makes the decryption round trip non-circular: the fixture is sealed with one
mechanism and opened with the other.

---

## 3. Raw harness output — the counts the brief asked for

**Pre-change (`phase2pre.exe`, sources from `ad16107`), verbatim tail:**

```
=================================================
checks: 41, failures: 38
RESULT: FAILURES PRESENT
=================================================
```
exit code 1. Full raw output: `%TEMP%\phase2\pre-run.txt` (163 lines).

**Post-change (`phase2post.exe`, live sources), verbatim tail:**

```
=================================================
checks: 41, failures: 0
RESULT: ALL CHECKS PASSED
=================================================
```
exit code 0. Full raw output: `%TEMP%\phase2\post-run.txt` (182 lines).

41 checks, **38 fail before** and **0 fail after**. Both numbers were produced by
the commands in section 8 in this session, and both full outputs are on disk at
the paths above.

### The three checks that pass in both revisions

`grep -c "^  PASS" pre-run.txt` → `3`, and all three are labelled in the output
itself:

```
  PASS  [passes both] a database with no readable tables reports the empty table list
  PASS  [passes both - regression guard] anti-circularity: the fixture is unreadable without the derived key
  PASS  [passes both] a wrong key is reported as KeyRejected, not as a schema problem
```

Justification for keeping each (the brief allows "label it as such and justify
it, or remove it"):

1. **Empty table list** — `SchemaMismatch` naming what it found is the honest
   fallback the brief explicitly says to *keep* (R7/R9). It has to keep passing;
   the thing that changed is *which* databases hit it, and that is covered by the
   two failing-before, passing-after `ChatInfo`-fixture checks instead.
2. **Anti-circularity** — `control A` (no key) and `control B` (the underived key
   supplied as a raw key) both must fail with `SQLITE_NOTADB`. This is the check
   that proves the fixture really is sealed by the derived key and not by
   something the harness leaked, per the wave-2 lesson in the brief. It is
   supposed to pass in both revisions — that is the point of a regression guard
   on untouched code.
3. **Wrong key → `KeyRejected`, not `SchemaMismatch`** — again a guard on the
   untouched declaration path: a key failure must never be reported as a schema
   failure. Required to pass in both.

No other check passes in both. In particular, I originally labelled the
"fixture keyed via `sqlite3_key(32 raw bytes)` opens through `Connect(hex)`"
check as `[passes both - regression guard]`; that label was **false** — it asserts
`Outcome == Success`, which pre-change fails because the old `Connect` rejects the
real schema before it can return `Success` (pre-run line 127 shows exactly that).
I removed the false label. That check now reads plainly as an R7 check and fails
pre-change as it should; the anti-circularity and wrong-key checks above carry the
"untouched code path" guarantee instead. The raw outputs above were regenerated
**after** that edit, so they match the harness on disk.

---

## 4. Raw output, post-change, by the brief's own verification list

The brief's "Verify" section lists six items; all six appear below verbatim from
`post-run.txt`.

**(1) Conversations listed from `SessionTable`** (names, unread counts, previews):

```
GetContacts() => 3 conversation(s), LastError=<null>
  conversation: Identifier=wxid_bob DisplayName=wxid_bob Unread=3 Preview=最后一条消息 "带引号"\n第二行
  conversation: Identifier=12345@chatroom DisplayName=12345@chatroom Unread=0 Preview=群聊摘要
  conversation: Identifier=wxid_caccoealsdbj12 DisplayName=wxid_caccoealsdbj12 Unread=0 Preview=自己
  PASS  R3: conversations come from SessionTable (3 rows)
  PASS  R3: the 1:1 conversation is listed with its unread count and preview
        -> Unread=3 Preview=最后一条消息 "带引号"\n第二行
  PASS  R3: last_timestamp is mapped onto LastMessageTime
        -> LastMessageTime=2023-11-15T06:21:40.0000000+08:00
  PASS  R3: sort_timestamp orders the list (most recent first)
        -> first=wxid_bob
```

**(2) Messages with content, timestamps, types:**

```
  message: Id=1 Type=Text(1) Time=2023-11-15T06:15:00.0000000+08:00 FromSelf=False SenderIdentifier=wxid_bob SenderName=wxid_bob Content=你好，这是中文消息 "带引号"\n第二行
  message: Id=2 Type=Image(3) Time=2023-11-15T06:16:40.0000000+08:00 FromSelf=True SenderIdentifier=wxid_caccoealsdbj12 SenderName=<null> Content=[图片]
  message: Id=3 Type=File(49) Time=2023-11-15T06:18:20.0000000+08:00 FromSelf=True SenderIdentifier=wxid_caccoealsdbj12 SenderName=<null> Content=<msg><appmsg><title>分享</title></appmsg></msg>
  PASS  R2: the 1:1 conversation returns its 3 messages
  PASS  R2: message_content maps to Content with CJK, an embedded newline and a double quote intact
  PASS  R2: create_time maps to CreateTime as a local time
  PASS  R2: local_id maps to MessageId, and messages come back oldest first
  PASS  R5: local_type 1 -> Text, 3 -> Image, 49 -> File/Share
```

**(3) Sender resolves through `real_sender_id` → `SessionTable.rowid` → username,
label is the username not `"Unknown"`:**

```
  PASS  R4: real_sender_id -> SessionTable.rowid -> username (the other party's wxid)
        -> SenderIdentifier=wxid_bob
  PASS  R4: the sender label of the other party's message is that username, never "Unknown"
        -> SenderName=wxid_bob, SenderIdentifier=wxid_bob
  PASS  R4: a message whose sender resolves to the owner's wxid is marked from self
        -> IsFromSelf=True, SenderIdentifier=wxid_caccoealsdbj12
```

Plus the fallback and group cases:

```
  PASS  R4: a resolved rowid yields the SessionTable username (rowid 2 -> the chatroom)
        -> identifiers=1:999,10002:12345@chatroom
  PASS  R4: an unresolvable real_sender_id falls back to the raw id, never "Unknown"
        -> identifiers=999,12345@chatroom
  PASS  R5: an undocumented local_type (10002) is preserved, not dropped
        -> types=1,10002
  PASS  R5: an undocumented type keeps its content too
        -> contents=群里的消息 | 未知类型消息
```

**(4) All six export formats** (JSON parses, PDF `%PDF-`, XLSX zip, CSV escaping):

```
  PASS  6 formats / JSON: parses, carries all 3 messages, and the CJK/newline/quote message round-trips
        -> messages=3, content intact=True, its SenderName=wxid_bob
  PASS  6 formats / CSV: the embedded double quote is doubled and the field stays quoted
        -> expected field: "你好，这是中文消息 ""带引号""\n第二行"
  PASS  6 formats / TXT: contains the messages and the sender label, with no "Unknown"
        -> has CJK=True, has 'wxid_bob:'=True, has 'You:'=True, has 'Unknown'=False
  PASS  6 formats / HTML: message text is HTML-escaped (the share XML is not markup)
        -> escaped share XML present=True, raw <msg> present=False
  PASS  6 formats / XLSX: a zip workbook whose content cell holds the CJK message
        -> zip=True, cell(2,3)=你好，这是中文消息 "带引号"\n第二行
  PASS  6 formats / PDF: starts with %PDF- and is larger than the same chat with no messages
        -> header="%PDF-1.4", bytes=48973 vs empty=12261
```

The JSON check parses the file with `JsonDocument` rather than string-matching,
because the serializer escapes the embedded newline to `\n` and a raw substring
grep would not have matched — that was a defect in an earlier draft of the
harness and is fixed here. It compares `Content` field-by-field and reads the
`SenderName` of the same element.

**(5) No `Msg_` tables → `SchemaMismatch` naming what it found:**

```
Connect(fixture D, no key) => SchemaMismatch : The database opened and decrypted, but it contains no per-session message tables (Msg_<hash>) to read. Tables actually found: ChatInfo, DeleteInfo, TimeStamp. This build reads WeChat 4.x message_*.db databases, which hold one Msg_<hash> table per SessionTable row - please report the table list above.
  PASS  R1/R9: a database with no Msg_ tables is a SchemaMismatch
        -> outcome=SchemaMismatch (a database carrying ChatInfo must no longer be accepted)
  PASS  R9: the mismatch names the tables that were actually found
```

Pre-change this same fixture returned `Success`, because carrying `ChatInfo` was
then the *success* condition (`post-run.txt` vs `pre-run.txt:107`). That inversion
is the single clearest demonstration that the harness discriminates.

**(6) Decryption round trip still passes:**

```
  PASS  R7: the fixture keyed via sqlite3_key(32 raw bytes) opens through Connect(hex)
        -> outcome=Success
  PASS  [passes both - regression guard] anti-circularity: the fixture is unreadable without the derived key
        -> no-key read => SqliteException: SQLite Error 26: 'file is not a database'. ; underived-key read => SqliteException: SQLite Error 26: 'file is not a database'.
Connect(fixture A, wrong key) => KeyRejected
  PASS  [passes both] a wrong key is reported as KeyRejected, not as a schema problem
        -> outcome=KeyRejected
```

And the R7 key-source report, confirming the winning candidate is still the 4.x one:

```
Connect(fixture A, hex) => Success : Connected successfully (key: WeChat 4.x (PBKDF2-HMAC-SHA512, 256000 iterations, page size 4096)) Found 2 message table(s).
  PASS  R7: the winning key candidate is still reported as the 4.x one
```

---

## 5. Per-requirement: what changed, where, how verified

### R1 — Discover the real tables by `Msg_` prefix

- `DatabaseService.cs:122` `MsgTablePrefix = "Msg_"`;
  `:502` `GetTableNames(SqliteConnection)` reads `sqlite_master`;
  `:534` `FindMessageTables` filters by prefix and excludes `_fts`.
- `Connect` (`:316`) discovers tables after the decryption probe
  (`:400` `MsgTablePrefix`) and returns `SchemaMismatch` via
  `BuildSchemaMismatchMessage` (`:493`) when none are found.
- `GetMessages` resolves the table with `ResolveMessageTable` (`:952`), which
  works in **both directions**: a plain username is MD5-hashed to `Msg_<hash>`,
  and a name that is *already* a `Msg_<hash>` table is matched literally
  (`:960-966`). That second direction is what makes the R9 hash-listing path
  round-trip back into a readable conversation.
- The invented `ChatInfo` schema is gone: `ExpectedMsgTables`, the old
  `GetContactsFromMsgDb` and the `ChatInfo` conversation path were deleted.

Verified: `R1` checks (lines 20-23 of `post-run.txt`), and the inverted fixture-D
result versus pre-change.

### R2 — Column mapping

- `GetMessages` (`:804`): builds the SELECT from
  `MessageTableIdentityColumns` (`:133` — `local_id, message_content, local_type,
  create_time`, required) intersected with the table's actual columns
  (`:849` missing-column guard, `:860`) plus any of
  `MessageTableOptionalColumns` (`:139` — `real_sender_id, server_id, sort_seq,
  source`) that exist. So a table missing optional columns still reads.
- Mapping: `local_id`→`MessageId`, `message_content`→`Content`, `create_time`→
  `CreateTime` (converted via `ToLocalTime`, which range-guards the Unix value),
  `local_type`→`(MessageType)localType` at `:912`.
- Message shapes were **not** redesigned. No change was made to any model.

Verified: the `R2` block (lines 47-58 of `post-run.txt`).

### R3 — Session list from `SessionTable`

- `SessionTableColumns` (`:151`) is a 9-name selectable list; `LoadSessionRows`
  (`:682`) selects `rowid, <existing columns>` and reads projected columns from
  **ordinal 1** (`:707`; ordinal 0 is the injected `rowid`). An earlier draft of
  mine passed `2` here — that was a bug I introduced and fixed; the projected
  columns start at 1.
- Selection is by intersection with `PRAGMA table_info` (`GetColumnNames`,
  `:1269`) so a trimmed `SessionTable` cannot break the read.
- `GetContacts` (`:571`) emits one `Contact` per session row: `Identifier` =
  `username`, `NickName` = contact name or the username, `UnreadCount`,
  `LastMessageTime` from `last_timestamp`, `LastMessagePreview` = `summary`.
- Ordering: `sort_timestamp ?? last_timestamp` descending, then rowid.

Verified: `R3` block (lines 28-41 of `post-run.txt`).

### R4 — Sender resolution

- `LoadSessionRows` also builds `RowId → Username` and `GetMessages` uses it at
  `:869` (`SessionRows?.ToDictionary(r => r.RowId, r => r.Username)`).
- "From self" (`:891-893`): the resolved username is compared, OrdinalIgnoreCase,
  against `_ownerWxid`. `_ownerWxid` comes from the account directory name via
  `TryGetOwnerWxid` (`:544`), regex `OwnerWxidPattern` (`:226`,
  `(?<wxid>wxid_[a-z0-9]+)`). **If the wxid cannot be extracted from the path,
  nothing is claimed to be from self** — the code does not guess.
- `SenderIdentifier = senderUsername ?? realSenderId?.ToString()`;
  `SenderId = isFromSelf ? 0 : realSenderId ?? 0` (`:914-915`).
- Fallbacks never produce `"Unknown"`: an unresolved `real_sender_id` becomes the
  raw numeric id (`999` in the fixture), and a degraded (no-`SessionTable`)
  conversation uses the raw id (`1`).
- `ResolveSenderName` (`:987`) was deliberately **narrowed**: it tries
  `senderNames` (the ViewModel map), then `contactNames`, then falls back to the
  conversation display name **only when the sender identifier equals the
  conversation identifier**. The previous unconditional fallback would have
  labelled every message in a group chat with the chatroom's own name. This is a
  behaviour change worth calling out.

Verified: the `R4` blocks (lines 62-68, 70-85, 87-101 of `post-run.txt`).

### R5 — `local_type` mapping

`(MessageType)localType` — an unchecked enum cast, deliberately, so an
undocumented value is **preserved numerically** rather than dropped or coerced.
The fixture's `10002` comes back as `Type=10002(10002)` with its content intact.
The documented readings used: `1=Text`, `3=Image`, `49=File`/share. Reference:
`research/experiments/m88/SCHEMA_SUMMARY.md` and
`research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:42-90`. **UNVERIFIED**
beyond those dumps — the enum values are the research's, not an observed WeChat
install's.

### R6 — `contact.db` optional

- `LoadContactNames` / `FindContactDatabasePath` / `ReadUserInfo` +
  `ReadMicroMsgContacts`: `contact.db` is located relative to the message
  database, opened with the same keyed connection settings, and read if present.
  **Every contact source is optional** — failure sets no fatal error and the
  conversation list still works from `message_0.db` alone.
- `ReadUserInfo` discovers its columns from candidate lists rather than hardcoding
  (`username/user_name/user_name_str/wxid/alias_name`,
  `nick_name/nickname/nick_name_str/alias`,
  `remark/remark_name/con_remark`), because `user_info` was not in the dumps I
  read. This is the **UNVERIFIED** part of R6 — see section 6.
- `ReadMicroMsgContacts` reads the 3.x `Contact`/`Contact_V2` tables as a last
  source; the `DisplayName` column was dropped from its SELECT as unused.
- Fixture rootB deliberately uses the *non-obvious* column names
  (`user_name`/`nickname`/`remark`) so the discovery path is exercised, not
  bypassed.

Verified: rootA (no `contact.db`) still lists 3 conversations with names
(`post-run.txt:40-41`) — the degraded case; rootB shows `DisplayName=BobRemark`
and `SenderName=BobRemark` from `contact.db` (`post-run.txt:95-101`).

`GetContacts`' degraded branch sets `LastError ??= ...` (`:613`) — `??=` and not
`=`, so a genuine `SessionTable` read failure is **not** overwritten by the
"no SessionTable" message. That was a defect in an earlier draft of mine.

### R7 — Decryption path untouched

See section 1. Left byte-identical. Round trip, anti-circularity and wrong-key
checks all pass (section 4.6). `SchemaMismatch` is now rare but retained as the
honest fallback, still naming the tables found (`BuildSchemaMismatchMessage`,
`:493`).

### R8 — `WeChatPathService` 4.x layout

File: `WeChatExport/Services/WeChatPathService.cs`.

- `MsgSubFolders` (`:48`) is now
  `{ "Msg", "db_storage", Path.Combine("db_storage","message"), "message" }` —
  note `db_storage` **alone was not enough**: the database is one level deeper,
  in `db_storage\message\`. The brief's own research
  (`research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:6`) shows the
  deeper path.
- `AccountParentFolders` (`:61`) = `{ "xwechat_files", "WeChat Files" }`, and
  `FindMsgDatabaseUnder` (`:127`) now descends: root → root's layout dirs → each
  child's layout dirs → and, if a child is an account-parent folder, each of *its*
  children's layout dirs. That is the
  `<root>\xwechat_files\<wxid>_<4hex>\db_storage\message\message_0.db` shape with
  an arbitrary user-chosen `<root>`.
- Dedupe via a `HashSet<string>` (`:152`) so overlapping search dirs do not
  duplicate matches. `SafeGetDirectories` (`:199`) swallows
  `UnauthorizedAccessException`/`IOException` so one unreadable folder does not
  abort discovery.
- The "most recently written wins" rule is **unchanged**, with an explicit
  comment (`:177-179`) that which shard of a multi-file `message_0.db .. message_N.db`
  install is authoritative is **not documented in the research**, so no
  preference is invented here. See section 6.

Verified: `R8` block (`post-run.txt:136-143`) — the exact path is found both from
the data root and when the user picks the account folder itself, and the harness
compares full paths for equality, not just "not null".

### R9 — Honest diagnostics / degraded mode

- No `Msg_` tables → `SchemaMismatch` naming the real table list (fixture D:
  `ChatInfo, DeleteInfo, TimeStamp`).
- `Msg_` tables but no `SessionTable` → still `Success`, with a message that says
  so plainly, and `GetContacts` lists conversations by `Msg_` table name with
  `LastError` explaining that names, previews and unread counts are unavailable.
  Previews/unread are not fabricated.
- No `Contact` is ever emitted with a blank display name: the name falls back
  `contact name → username`, and in degraded mode to the table name.

Verified: the `R9 degraded` block (`post-run.txt:104-123`) and the
`R1/R9 schema mismatch` block (`:125-134`).

---

## 6. UNVERIFIED assumptions

Each row is an assumption the code makes about a schema **I could not observe**.
The fixture proves the code implements the assumption; it cannot prove the
assumption is true of a real install.

| # | Assumption | Where in code | Research reference relied on |
|---|---|---|---|
| U1 | Message tables are named `Msg_<32-hex>` and the hex is the MD5 of the session username | `:952-966` | `research/experiments/m88/SCHEMA_SUMMARY.md`; `research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:42-90`; `research/experiments/m112/WECHAT_EXPORT_FULL_CONTEXT.md:38-62` |
| U2 | `Msg_<hash>.real_sender_id` equals `SessionTable.rowid` (implicit rowid, not a wxid) | `:869`, `:885-893` | `research/experiments/m90/SENDER_MAPPING.md`; the mapping diagram in `WECHAT_EXPORT_FULL_CONTEXT.md:38-62` |
| U3 | `local_type`: 1=text, 3=image, 49=share/file; anything else is unknown-but-preserved | `:912` | `research/experiments/m88/SCHEMA_SUMMARY.md`; `WECHAT_EXPORT_RESEARCH_HANDOVER.md:42-90` |
| U4 | `SessionTable` columns are `username, unread_count, summary, last_timestamp, sort_timestamp` (plus others). The brief's 9-column `SessionTable` is a **subset** of the 19-column dump | `:151` | `research/experiments/m89/all_schema.sql:822` (the full dump). I select only columns that exist, via `PRAGMA table_info` intersection, so a trim cannot break the read |
| U5 | `create_time` is a Unix seconds timestamp, and local-time conversion is correct for it | `ToLocalTime` helper | `research/experiments/m88/all_tables.sql` |
| U6 | The owner's wxid can be taken from the account **directory name**, and there is no in-database "from self" flag | `:226`, `:544` | `research/experiments/m112/` path examples, e.g. `wxid_caccoealsdbj12_e8c8`. **No documented in-DB flag exists in any dump I read.** If the wxid cannot be extracted, the code claims nothing is from self rather than guessing |
| U7 | `contact.db` lives at `db_storage\contact\contact.db` and has a `user_info` table whose identity/nick/remark column names are among the candidate lists in `ReadUserInfo` | `LoadContactNames`, `FindContactDatabasePath`, `ReadUserInfo` | `research/experiments/m90/SENDER_MAPPING.md:43` names `user_info`. **The individual column names are NOT in the dumps I read** — hence the candidate lists. This is the weakest assumption in the change; it degrades safely (no names, not a failure) but it may silently produce no names against a real `contact.db` |
| U8 | The authoritative message DB in a multi-shard `message_0.db .. message_N.db` install is the most recently written one | `WeChatPathService.cs:177-180` | **None.** Explicitly undocumented. The pre-existing rule is kept and the uncertainty is written into a comment rather than papered over |
| U9 | `summary` is a plain-text preview | `:586` | `research/experiments/m89/all_schema.sql`. If a real `summary` carries binary/XML for some message types, it is passed through as-is |
| U10 | `SessionTable` may legitimately be absent, and `Msg_`-prefixed tables are the reliable fallback enumeration | `:534`, degraded branch of `GetContacts` | Brief R9 (which is itself derived from the research, not from an install) |

The FTS tables named in the brief (`message_fts_v4_*`) are ignored by the
`_fts` exclusion in `FindMessageTables` (`:534`) — the brief says "ignore unless
needed" and nothing needed them.

---

## 7. Where the brief and the research disagree

- **`SessionTable` column count.** The brief gives a 9-column `SessionTable`;
  `research/experiments/m89/all_schema.sql:822` has 19. **The research wins.**
  The code does not hardcode either list as a requirement: it intersects its
  9-name candidate list with `PRAGMA table_info`, so it reads whichever of the
  two a real install actually has. Nothing in the brief was contradicted, only
  narrowed.
- **Message DB filename.** The brief's summary sentence says "not `MSG.db`" and
  targets `message_0.db`; the research path is `message_0.db` under
  `db_storage\message\`. These agree; I mention it only because the *brief's own*
  `MsgSubFolders` description ("`db_storage\message\`") and the R8 signature line
  (which shows `db_storage\message\message_0.db`) are consistent, whereas a naive
  reading of "look under `db_storage`" would have stopped one level too high. The
  research's deeper path won.
- No other conflict was found.

---

## 8. Reproducing this

```
cd C:/Users/geffzhang/AppData/Local/Temp/phase2
rm -rf phase2-run post-run.txt pre-run.txt

cd post && dotnet build -v q --nologo && dotnet run --no-build > ../post-run.txt 2>&1
cd ../pre  && dotnet build -v q --nologo && dotnet run --no-build > ../pre-run.txt  2>&1
```

Expected: `post` → `checks: 41, failures: 0`, `RESULT: ALL CHECKS PASSED`, exit 0;
`pre` → `checks: 41, failures: 38`, `RESULT: FAILURES PRESENT`, exit 1.

Main project build:

```
cd c:/workshop/github/WeChat-Export-Tool/WeChatExport && dotnet build
```

```
已成功生成。
    0 个警告
    0 个错误
```

0 errors, 0 warnings, same language-neutral output as the brief's "Build" line
requires.

---

## 9. What could NOT be verified

- **Anything about a real WeChat 4.x install.** Stated at the top; repeated here
  because it is the whole ceiling. The fixture is my own construction from the
  research dumps. If the dumps are wrong, the code faithfully implements the
  wrong schema and this report's green results still hold.
- **`contact.db` column names (U7).** The weakest link. The candidate-list
  approach degrades to "no names" rather than a crash, so a wrong guess here costs
  display names, not correctness — but it would not be detected by this harness,
  which supplies the names the code looks for.
- **Multi-shard `message_N.db` selection (U8).** Undocumented; the pre-existing
  "most recent wins" heuristic is retained unfixed and unverified.
- **The effect of the `ResolveSenderName` narrowing on real group chats.** The
  fixture exercises it (a group message whose sender is the chatroom resolves to
  the chatroom username), but the fixture is mine, so this shows the branch works,
  not that it matches WeChat's real behaviour.
- **Performance on a real database.** The fixture has 3 conversations and a
  handful of messages. `LoadSessionRows` reads the whole `SessionTable` eagerly
  on first access; on a real install with thousands of sessions this is untested
  here.
- **The FTS tables.** Ignored by design; never exercised.
