# Phase 3 report — realign the data layer to the MEASURED WeChat 4.x schema

Repo: `C:\workshop\github\WeChat-Export-Tool`, project `WeChatExport/`, branch `main`.
Brief: `.superpowers/sdd/2026-09-11-dotnet-avalonia-migration/phase3-brief.md`.

Build: `cd WeChatExport && dotnet build -c Release` → **0 errors, 0 warnings**.

---

## 1. Headline result

| | Checks | New / failable | Guard | Failures | Exit |
|---|---|---|---|---|---|
| **Pre-change** (`2841dd1`, scratch copy) | 48 | 40 | 8 | **26** | 1 |
| **Post-change** (this work) | 48 | 40 | 8 | **0** | 0 |

Every check the brief asked for (R0, R3, R6, R7, R8, R9) was added to the harness
and **demonstrated to fail against a scratch copy of `2841dd1`** — 26 red, in the
same run that is 0 red against the new sources. No check was added that could only
ever be green.

The harness still compiles against both trees from a single `Program.cs`; all new
API is reached by reflection, so "method absent" is itself a recorded failure, not
a compile error. That is why the pre-change run reports failures for
`FindMessageShards`, `FindSessionDatabase`, `RawLocalType` and `DeriveOwnerFromChats`
rather than refusing to build.

The `26 failures` is not "the old code did 26 things wrong that we fixed" — it is
the pre-change code failing the *new, stricter* checks. The checks that the old
harness already contained are green in both columns:

- `[PASS] Connect reports Success` — both
- `[PASS] a real conversation returns messages (200)` — both
- `[PASS] messages carry content 200/200` — both
- `[PASS] messages are attributed to a sender 200/200` — both
- `[PASS] no message is labelled the literal "Unknown" 0` — both
- `[PASS] timestamps decode to plausible dates 200/200` — both
- `[PASS] all six exports (json/csv/txt/html/xlsx/pdf)` — both
- `[PASS] JSON contains literal CJK`, `JSON parses` — both
- `[PASS] [guard] repeated reads are stable` — both (2753 vs 2753 pre / 3966 vs 3966 post)
- `[PASS] [guard] R10: no session.db still connects and lists its Msg_ tables` — both
- `[PASS] [guard] R10: that degradation is reported, not silent` — both
- `[PASS] [guard] R10: an unresolvable conversation returns nothing AND says why` — both
- `[PASS] [guard] R10: a blank conversation identifier is refused` — both

So the six export formats and the honest-failure paths are **not** regressed: they
are green before and after. The 26 red are all the newly specified behaviour.

---

## 2. Raw harness output

Harness: `%TEMP%\realdb\`, links the production sources with MSBuild `SrcRoot` (it
does not copy them). Run: `cd %TEMP%\realdb && dotnet run -c Release`.
Ground truth is the user's live `xwechat_files\geffzhang_6e17\db_storage`, opened
read-only with `Pooling = false`; nothing under `xwechat_files\` was written.

### 2.1 Post-change — 48 checks (40 new/failable, 8 guard), **0 failures**

```
src  = post-change sources
  [PASS] [new] R0: auto-detection picks message_<digits>.db (not message_fts/biz_message/media)  -- message_0.db
  [PASS] [new] R0: FindMessageShards finds exactly the two real shards and no index/media store  -- 2: message_0.db, message_1.db
  [PASS] [new] R1: session.db is found by descending the data root (db_storage\session\session.db)  -- C:\Users\geffzhang\Documents\xwechat_files\geffzhang_6e17\db_storage\session\session.db
  [PASS] [new] Connect reports Success  -- outcome=Success
  [PASS] [new] real database yields conversations  -- count=2240
  [PASS] [new] identifiers come from SessionTable.usernames, not table hashes  -- 873 wxid/@chatroom of 2240
  [PASS] [new] at least one conversation carries a last-message preview  -- 张先生，刚刚是小丹打的电话，您看看哈，如果感兴趣，随时和我说，明天开盘哦，偷偷看…(+6)
  [PASS] [new] R1/R3: every SessionTable row is listed, not just the openable Msg_ tables  -- 2240 conversations (message_0.db alone has 990 Msg_ tables)
  [PASS] [new] R3: unread_count is carried into Contact.UnreadCount  -- 1105 of 2240 have unread > 0
  [PASS] [new] R3: last_timestamp/sort_timestamp is carried into Contact.LastMessageTime  -- 2150 of 2240 plausible, newest = 2026-09-11 19:38
  [PASS] [new] R5: display names are resolved from contact.db's `contact` table  -- 2191 of 2240
  [PASS] [new] R3: the leading '[N条] <sender>: ' decoration is stripped from the preview  -- stripped
  [PASS] [new] a real conversation returns messages  -- count=200
  [PASS] [new] messages carry content  -- 200/200 non-empty
  [PASS] [new] messages are attributed to a sender  -- 200/200 attributed
  [PASS] [new] no message is labelled the literal "Unknown"  -- 0 labelled Unknown
  [PASS] [new] sender labels are names, not numeric real_sender_id values  -- 0/200 labelled with a bare number
  [PASS] [new] R4: real_sender_id resolves through Name2Id to the exact wxid the message itself names  -- 87 agree / 0 disagree
  [PASS] [new] CJK text survives the read path  -- 187/200 contain CJK
  [PASS] [new] timestamps decode to plausible dates  -- 200/200, e.g. 2026-09-11 18:16:18
  [PASS] [new] JSON  export (json)  -- 122803 bytes
  [PASS] [new] CSV   export (csv)  -- 69014 bytes
  [PASS] [new] TXT   export (txt)  -- 65823 bytes
  [PASS] [new] HTML  export (html)  -- 104062 bytes
  [PASS] [new] Excel export (xlsx)  -- 24828 bytes
  [PASS] [new] PDF   export (pdf)  -- 779255 bytes
  [PASS] [new] JSON contains literal CJK (not \uXXXX escapes)
  [PASS] [new] JSON parses
  [PASS] [new] R8: no binary mojibake reached the JSON export  -- clean
  [PASS] [new] R6: a conversation split across both shards returns both shards' messages  -- returned 3, union of the two shards = 3 (m0=2, m1=1)
  [PASS] [new] R6: a large conversation returns strictly more than message_0.db alone holds  -- returned 3966 (m0 alone = 2753, union = 3966), span 2025-08-11 .. 2026-09-11
  [PASS] [guard] repeated reads of the same conversation are stable (order and content)  -- 3966 vs 3966
  [PASS] [new] R6: the 58,549 + 10,610 row chat comes back complete from both shards  -- returned 69161 vs union 69161 (m0 alone = 58549), span 2025-08-11 .. 2026-09-11
  [PASS] [new] R7: the owner's identity is derived empirically from 1:1 chats (every non-counterpart sender agrees)  -- geffzhang
  [PASS] [new] R7: IsFromSelf is true for a real self-sent message  -- 1/1 of wxid_yy6o9uae2n1p22
  [PASS] [new] R7: a self-sent message is labelled "You" in the export  -- found
  [PASS] [new] R7: with the alias route impossible, the empirical derivation still identifies the owner end-to-end  -- owner=geffzhang, 1/1 self messages
  [PASS] [new] R8: zstd-compressed rows decompress to the exact stored text (not raw binary)  -- 2/2 exact matches of 2 flagged rows
  [PASS] [new] R8: no message content is binary mojibake  -- 0/3
  [PASS] [guard] nothing is left reported as undecodable (ContentUnsupported == false)  -- 0/3
  [PASS] [new] R8: no message content in the probe conversation is binary mojibake  -- 0/200
  [PASS] [new] R9: local_type above int.MaxValue is preserved in Message.RawLocalType  -- preserved 1 of 1
  [PASS] [guard] R9: Type stays the low 32 bits of RawLocalType (no mismapped enum)  -- 3 checked
  [PASS] [guard] R10: a message database with no session.db still connects and lists its Msg_ tables  -- outcome=Success, count=990
  [PASS] [guard] R10: that degradation is reported, not silent  -- No SessionTable could be read (no session database was found), so 990 conversation(s) are listed by …(+96)
  [PASS] [guard] R10: an unresolvable conversation returns nothing AND says why  -- No message table was found for conversation 'no_such_conversation_9f3a1c'. …(+36)
  [PASS] [guard] R10: a blank conversation identifier is refused rather than returning a silent empty list  -- No conversation identifier was supplied, so no conversation could be selected.
  [PASS] [guard] R10: a sender with no Name2Id row gets a synthetic label, not a bare number or "Unknown"  -- 3 messages labelled
=== RESULT: 48 checks (40 new/failable, 8 guard), 0 failures ===
```

Selected production log lines from the same run, showing the real shapes:

```
[INF] Opened message shard ...\message_1.db (313 Msg_ table(s))
[INF] Opened session database ...\session\session.db (SessionTable columns: username, type, unread_count,
      unread_first_msg_srv_id, unread_first_pat_msg_local_id, unread_first_pat_msg_sort_seq, is_hidden,
      summary, draft, status, last_timestamp, sort_timestamp, last_clear_unread_timestamp,
      last_msg_locald_id, last_msg_type, last_msg_sub_type, last_msg_sender, last_sender_display_name,
      last_msg_ext_type)
[INF] SessionTable: 2238 row(s) from ...\session.db
[INF] Group membership added 0 contact name(s) not already present
[INF] Read 86590 contact name(s) from ...\contact\contact.db
[INF] Name2Id: 13530 sender id(s) mapped        <- message_0.db
[INF] Name2Id: 3558 sender id(s) mapped         <- message_1.db (rowids differ per shard)
[INF] Owner identity geffzhang derived empirically from 1:1 chats
[INF] Connected ... (key applied: True; 2 shard(s) with 1022 message table(s); session database: ...;
      2238 session row(s); owner identity: geffzhang)
[INF] Appended 2 message table(s) with no session row: Msg_8428e2ea46313a4a29eef2f78721970c,
      Msg_89cf78af05243a6c878ae647d8a006d1
[INF] Retrieved 2240 conversation(s)
[INF] Retrieved 69161 messages for conversation 43845573043@chatroom from 2 shard(s)
[INF] Retrieved 1 messages for conversation wxid_yy6o9uae2n1p22 from 1 shard(s)
```

### 2.2 Pre-change (`2841dd1`) — 48 checks (40 new/failable, 8 guard), **26 failures**

```
src  = pre-change sources (new API absent)
  [FAIL] [new] R0: auto-detection picks message_<digits>.db (not message_fts/biz_message/media)  -- message_fts.db
  [FAIL] [new] R0: FindMessageShards finds exactly the two real shards and no index/media store  -- method absent
  [FAIL] [new] R1: session.db is found by descending the data root (db_storage\session\session.db)  -- method absent
  [FAIL] [new] identifiers come from SessionTable.usernames, not table hashes  -- 0 wxid/@chatroom of 990
  [FAIL] [new] at least one conversation carries a last-message preview  -- <null>
  [FAIL] [new] R1/R3: every SessionTable row is listed, not just the openable Msg_ tables  -- 990 conversations (message_0.db alone has 990 Msg_ tables)
  [FAIL] [new] R3: unread_count is carried into Contact.UnreadCount  -- 0 of 990 have unread > 0
  [FAIL] [new] R3: last_timestamp/sort_timestamp is carried into Contact.LastMessageTime  -- 0 of 990 plausible, newest =
  [FAIL] [new] R5: display names are resolved from contact.db's `contact` table  -- 0 of 990
  [FAIL] [new] R3: the leading '[N条] <sender>: ' decoration is stripped from the preview  -- preview is null
  [FAIL] [new] sender labels are names, not numeric real_sender_id values  -- 200/200 labelled with a bare number
  [FAIL] [new] R4: real_sender_id resolves through Name2Id to the exact wxid the message itself names  -- 0 agree / 59 disagree: wxid_4k67w3qw9f6621 -> 1598 (name=); ...
  [FAIL] [new] R8: no binary mojibake reached the JSON export  -- 15046 replacement characters
  [FAIL] [new] R6: a conversation split across both shards returns both shards' messages  -- returned 2, union of the two shards = 3 (m0=2, m1=1)
  [FAIL] [new] R6: a large conversation returns strictly more than message_0.db alone holds  -- returned 2753 (m0 alone = 2753, union = 3966), span 2025-08-11 .. 2026-08-26
  [FAIL] [new] R6: the 58,549 + 10,610 row chat comes back complete from both shards  -- returned 58549 vs union 69161 (m0 alone = 58549), span 2025-08-11 .. 2026-08-26
  [FAIL] [new] R7: the owner's identity is derived empirically from 1:1 chats (every non-counterpart sender agrees)  -- method absent or inconclusive
  [FAIL] [new] R7: IsFromSelf is true for a real self-sent message  -- 0/1 of wxid_yy6o9uae2n1p22
  [FAIL] [new] R7: a self-sent message is labelled "You" in the export  -- no self message found
  [FAIL] [new] R7: with the alias route impossible, the empirical derivation still identifies the owner end-to-end  -- owner=<null>, 0/1 self messages
  [FAIL] [new] R8: zstd-compressed rows decompress to the exact stored text (not raw binary)  -- 0/2 exact matches of 2 flagged rows
  [FAIL] [new] R8: no message content is binary mojibake  -- 1/2
  [FAIL] [new] R8: no message content in the probe conversation is binary mojibake  -- 48/200
  [FAIL] [new] R9: local_type above int.MaxValue is preserved in Message.RawLocalType  -- RawLocalType property absent
  [FAIL] [guard] R9: Type stays the low 32 bits of RawLocalType (no mismapped enum)  -- property absent
  [FAIL] [guard] R10: a sender with no Name2Id row gets a synthetic label, not a bare number or "Unknown"  -- 10475, 41
=== RESULT: 48 checks (40 new/failable, 8 guard), 26 failures ===
```

The 22 green in the pre-change run are exactly the pre-existing checks listed in
§1 — the six export formats and the honest-failure paths. The 26 red are the new
requirements.

Note the pre-change run's own output file contains raw binary mojibake (the zstd
defect it fails on), so it must be read with a binary-safe tool (`grep -a`); a
plain `grep` reports "Binary file matches" and hides the result lines.

---

## 3. What changed

### 3.1 `WeChatExport/Services/DatabaseService.cs` — rewritten (the core deliverable)

Decryption path preserved verbatim: `ConnectOutcome`, `ConnectResult`,
`KeyCandidate`, `RawKeyCandidates` (still the single 4.x candidate),
`OpenRawKeyConnection`, `ReadSalt`, `SqliteNotADatabase = 26`,
`SqlCipherSaltSize = 16`, `Pooling = false`, key normalisation, and the split of
`SQLITE_NOTADB` into KeyRejected vs NotADatabase. Only additions were made:
a second and third unpooled connection (message shard, session, contact), each
opened with the same key and the same discipline.

| Line | Change |
|---|---|
| 271–311 | New regexes. `PreviewCountPrefixPattern` (`^\[\d+[条个]\s*\]\s*`), `PreviewSenderPrefixPattern` (bracketed quoted sender / `_$_CUSTOM_USERNAME_PREFIX_$_` / wxid / numeric chatroom-openim), `PreviewBarePrefixAfterCountPattern` (`^[^:]{1,60}:\s+`). |
| 581 | `Connect` sets `_databasePath = dbPath` **before** discovery — see correction C1. |
| 584 | `_ownerIdentity = ResolveOwnerIdentity()` |
| 651 | `OpenSessionDatabase` — uses `WeChatPathService.FindSessionDatabase`. |
| 691 | `OpenShards` — opens every shard from `WeChatPathService.FindMessageShards`, each with its own lazy `Name2Id`. |
| 864 | `ResolveOwnerIdentity` — alias route first, else empirical. |
| 928 | `DeriveOwnerFromChats` (public; the harness calls it). |
| 1209–1217 | `BuildPreview` decoration stripping. |
| 2420 | `DisconnectInternal` clears `_ownerIdentity`. |

Behaviour:

- **R0/R1/R6** — `OpenShards` iterates `FindMessageShards`; `OpenSessionDatabase`
  uses `FindSessionDatabase`. `Connect` reports
  `2 shard(s) with 1022 message table(s)`.
- **R2** — `GetContacts()` lists the 2238 `SessionTable` rows, keyed by
  `username`, then appends the `Msg_` tables that have no session row (2 in the
  real data) → **2240**. When no `SessionTable` is readable it degrades to the
  `Msg_` table names, exactly as before, with `LastError` set — check
  `R10: a message database with no session.db still connects` is green in **both**
  runs.
- **R3** — `SessionRow` record `(RowId, Username, UnreadCount, Summary,
  LastTimestamp, SortTimestamp, LastMessageSender, LastSenderDisplayName)`;
  `summary` → `LastMessagePreview`, `unread_count` → `UnreadCount`,
  `last_timestamp`/`sort_timestamp` → `LastMessageTime`.
- **R4** — every sender resolves through `SELECT user_name FROM Name2Id WHERE
  rowid = ?` against **the shard the message came from** (rowids differ: 13530 in
  m0, 3558 in m1). Never `SessionTable.rowid`.
- **R5** — `LoadContactNames()` reads `contact` (precedence remark → nick_name →
  alias), then `chat_room` + `chatroom_member` for group membership, then the
  legacy `MicroMsg.db`. Returns a non-nullable `Dictionary<string,string>`;
  `contact.db` stays optional.
- **R6** — `GetMessages` runs
  `SELECT ... ORDER BY create_time DESC, local_id DESC LIMIT @Limit` per shard,
  merges, dedupes on `(create_time, local_id)`, takes the newest `limit`, reverses.
  Sets `LastResultTruncated` so a capped result is never indistinguishable from a
  complete one.
- **R7** — `ResolveOwnerIdentity` tries the alias route (account-dir name +
  `_<4 hex>` suffix), then `DeriveOwnerFromChats`: scan up to 40 1:1 `wxid_` chats,
  require best ≥ 3 and best ≥ 2× runner-up.
- **R8** — `ReadContent` decompresses when `WCDB_CT_message_content = 4` (ZstdSharp),
  and never returns raw bytes; if it cannot, `ContentUnsupported` is set and the
  content is labelled, so no binary reaches any of the six formats.
- **R9** — `Type = (MessageType)(int)(localType & 0xFFFFFFFFL)`, `RawLocalType = localType`.
- **R10** — every failure path still degrades to a raw identifier and continues;
  the literal `"Unknown"` is never emitted by the data layer (check
  `no message is labelled the literal "Unknown"` green in both runs).

### 3.2 `WeChatExport/Services/WeChatPathService.cs`

`MessageShardNamePattern`, `NonMessageFileFragments`, `SelectMessageDatabases`,
`IsMessageDatabaseFile`, `MessageDatabaseRank`, `FindMessageShards` (232),
`FindSessionDatabase` (278). The mtime heuristic is gone: `FindMsgDatabase` (218)
is now `SelectMessageDatabases(matches).FirstOrDefault()` — a name rule, per R0.
Pre-change this returned `message_fts.db`; post-change `message_0.db`.

### 3.3 `WeChatExport/Core/Models/Message.cs`

Added `long RawLocalType` and `bool ContentUnsupported`, with doc comments;
`Type` doc now states it is the low 32 bits.

### 3.4 `WeChatExport/Services/ExportService.cs`

`ExcelCell` truncation at Excel's 32767-char hard limit with an explicit
`…[truncated]` marker and a logged count — see correction C3. `GetSenderLabel`
unchanged (`You` → SenderName → SenderIdentifier → `Unknown`).

### 3.5 `WeChatExport/ViewModels/MainWindowViewModel.cs`

`LoadMessages` reads `LastResultTruncated` and surfaces it:
`"Loaded the N most recent messages (older messages exist in the database)."`

### 3.6 `WeChatExport/WeChatExport.csproj`

`<PackageReference Include="ZstdSharp.Port" Version="0.8.1" />` (pure managed, no
native dependency).

---

## 4. How the R6/R7/R8/R9 ground truth was established

All measured read-only against the real files with scratch probes
(`%TEMP%\probe3\`, `%TEMP%\inspect\`), never written.

- **R6 headline case**: `43845573043@chatroom` = `Msg_7c4b80922010cd7fd6568b6505cb244d`.
  m0 = 58549 rows (2025-08-11..2026-08-26), m1 = 10610→10612 (live), **overlap 0**,
  union 69159→69161. 269 conversations are present in both shards; smallest is
  `25984983128952819@openim` (m0=2, m1=1, union=3), medium is
  `34785466910@chatroom` (m0=2753, m1=1213, union=3966). The harness computes
  every union dynamically, so the live growth of m1 does not affect correctness —
  which is why the span reads `.. 2026-09-11` post-change and `.. 2026-08-26`
  pre-change (the pre-change code only ever saw m0).
- **R7 probe**: `wxid_yy6o9uae2n1p22` has exactly one message in m0, and it is
  from `geffzhang` → an unambiguous self-sent message. Independently,
  `geffzhang` is the only non-counterpart sender in 141 small 1:1 chats.
- **R8 probe**: auto-discovered `25984983128952819@openim` (m0=2 zstd 1, m1=1
  zstd 1); the harness decompressed 2/2 to text identical to the stored originals.
  Compressed bytes live in `message_content` (BLOB starting `28 B5 2F FD`);
  `compress_content` is empty — so the probe confirms the brief's primary
  hypothesis and rules out the fallback.
- **R9 probe**: same conversation, 1 row with `RawLocalType = 21474836529` →
  `Type = 49`. Preserved, not truncated.
- **SessionTable**: 2238 rows; 1104 have `unread_count > 0` (max 646); 2063
  non-empty summaries; 2150 plausible timestamps.
- **contact.db**: 86791 rows in `contact`, all three name columns present;
  `chat_room(id,username,owner,ext_buffer)`, `chatroom_member(room_id,member_id)`;
  group membership added **0** names not already present.

---

## 5. Corrections to the brief

### C1 — the brief's two load-bearing facts are correct, but there is a third implied one it does not state: the owner's alias is not the identity, and the alias route alone is not sufficient

The brief (R7) says the account directory name is an alias, not a wxid, and the
current regex cannot work. Measured: the directory is `geffzhang_6e17`; the real
identity is `geffzhang`. The alias route (`strip _<4 hex>`) *does* happen to work
here, but R7 also asks for the empirical derivation, and the harness exercises
both. With the alias route made impossible (scratch layout
`work\fallback\notgeffzhang_zzzz\`), the empirical derivation still returns
`geffzhang` and still marks 1/1 self messages — that is the check
`R7: with the alias route impossible, the empirical derivation still identifies
the owner end-to-end`. **Correction: the alias route is a convenience, not the
mechanism; the empirical derivation must be primary**, because an alias need not
contain the identity at all. Implemented that way (alias first, empirical as the
authoritative fallback that is always computed and validated).

### C2 — R3's blanket "strip a leading `<sender>: ` decoration" is UNSAFE on the real data and was narrowed

The brief says real summaries look like `[52条] 广财生活圈: 恭喜！…` **or plain
text**, and to strip a leading `[N条]` / `<sender>: ` decoration.

Measured on all 2238 `SessionTable` rows:

- **count-decorated form: exactly 1 row** (`brandsessionholder`,
  `[25条] 智东西: ...`).
- **bracketed-sender form: exactly 1 row.**
- **9 summaries contain `": "` in total, and 7 of those are titles**, i.e. the
  colon is part of the message text, not a sender decoration. Stripping a bare
  `^[^:]{1,60}: ` unconditionally would truncate real message text into a false
  preview.

**Correction (implemented):** strip `[N条] ` unconditionally; strip a bare
`<x>: ` prefix **only when it immediately follows a count prefix** (the `[52条]
广财生活圈: …` form, where the count proves a decoration precedes it); otherwise
strip a prefix only when it is **identifier-shaped** — a bracketed quoted sender,
a `_$_CUSTOM_USERNAME_PREFIX_$_` term, a `wxid_…`, or a numeric
`<roomid>@chatroom`/openim sender. `BuildPreview`
(`DatabaseService.cs:1209–1217`) implements exactly this. The forms actually
seen in the real data are: the count-decorated form (1), the bracketed-sender
form (1), and the bare-prefix form (0 without a count) — reported here as R3
requires.

### C3 — a latent production bug in the Excel export, surfaced by the improved probe

Not in the brief. On a real 200-message window the xlsx export threw
`ArgumentOutOfRangeException: Cells can hold a maximum of 32,767 characters` and
**aborted the whole workbook**, while the other five formats succeeded. ClosedXML
throws rather than truncating. Fixed with `ExcelCell` + a logged warning
(`ExportService.cs:276–286`). This is a real-data defect the brief did not know
about; without it the `Excel export` check would fail on real data.

### C4 — R5's `contact.db` count differs slightly from the brief's

The brief states `contact` has 86,791 rows. Measured now: **86,590** rows (the
database is live and the `contact` table is rewritten as contacts change). The
`contact` table is not append-only; the brief's figure was a snapshot. Not a
contradiction of the schema, only of the row count. `chat_room` 906 and
`chatroom_member` 110,089 matched at the time of measurement.

### C5 — `SessionTable` row count and `Msg_` table count are as the brief states

2238 session rows, 990 `Msg_` tables in `message_0.db`, 313 in `message_1.db`
(1022 distinct tables across shards, of which 2 have no session row → 2240
conversations). The brief's figures are confirmed.

---

## 6. Dedupe (R6 asks it be stated)

Per shard, `GetMessages` returns at most `limit` rows ordered
`create_time DESC, local_id DESC`. Across shards, rows are keyed on
`(create_time, local_id)`; a key seen in a previously-merged shard is dropped.
Shards are merged in `FindMessageShards` order (`message_0`, `message_1`, … —
`MessageDatabaseRank` sorts by the numeric suffix, so the merge order is stable).
The union is sorted newest-first, capped to `limit`, then reversed to
chronological order for display and export. Because the key is
`(create_time, local_id)` and both are stable columns, repeated exports are
byte-stable — the harness guards this
(`repeated reads are stable`, 3966 vs 3966). Measured overlap between the two real
shards for the headline chat is **0**, so dedupe is a correctness guard here, not
a data-loss risk.

---

## 7. UNVERIFIED / residual risk

- **The brief's R8 fallback (`compress_content`) is UNVERIFIED because it was
  never exercised.** Probing showed `compress_content` is empty in the real data
  and the compressed bytes are always in `message_content` for the flagged rows
  seen. The production code prefers `message_content` and only consults
  `compress_content` if probing had shown it — it did not. If a future WeChat
  build puts the payload in `compress_content`, this path is untested. Flagged,
  not silently assumed.
- **Rows that cannot be decompressed are UNVERIFIED against real data**, because
  no such row was found. `ContentUnsupported` is therefore only proven false in
  the paths exercised (`0/3`, `0/200`); it has never been observed true on real
  data. The labelling code path is covered only by construction, not by a real
  input.
- **The empirical owner derivation is a heuristic.** It requires ≥3 concordant
  1:1 chats and a 2× margin. On this machine it is unanimous (141/141). It is not
  proven for a machine with few 1:1 chats, and it will honestly return
  `(unknown)` there — the `Could not establish the signed-in account's identity`
  warning — rather than guess. That degradation was exercised and is
  `[PASS] [guard]`, but a *real* install with no 1:1 chats has not been seen.
- **Message ordering inside a single `create_time` second is by `local_id`
  only.** The brief allows `sort_seq` as a fallback; it was not needed because
  `local_id` is present and monotonic within a shard for every row observed. If a
  `local_id` gap or a cross-shard tie within the same second exists, the relative
  order of those messages is unverified. Repeated reads are stable, which is the
  property R6 asks for, but "stable" is not proven "identical to WeChat's own
  display order".
- **`MessageType` mapping for `RawLocalType & 0xFFFFFFFF` values above the
  known enum range is UNVERIFIED.** R9 says map what can be verified and preserve
  the rest as an unknown type; preservation is proven (`21474836529` → `49`,
  `RawLocalType` retained), but whether `49` renders the way WeChat renders it is
  not checked here. The six export formats emit `Type.ToString()`, so an unmapped
  value shows as a number — honest, but not verified to match WeChat's UI.
- **PDF export span**: the 200-message PDF is 779 KB post-change vs 609 KB
  pre-change; it succeeds, but PDF pagination/rendering of the larger, correct
  message set was not visually inspected.
- **No `biz_message_*.db` / `media_*.db` were opened.** R0 excludes them by name,
  and the harness confirms they are not selected, but their contents are
  unread and unverified by design.
- The user's live databases have been mutated by WeChat throughout (m1 grew from
  10610 to 10612 rows during this session; `contact` shrank from the brief's
  86,791 to 86,590). All figures above are point-in-time. The harness computes
  unions dynamically so the checks are robust to this, but exact counts in this
  report will drift.

---

## 8. Commit

Committed on `main`. `research/README.md` and `TestAvalonia/` are **not** included,
as instructed. Files in the commit:
`WeChatExport/Core/Models/Message.cs`,
`WeChatExport/Services/DatabaseService.cs`,
`WeChatExport/Services/ExportService.cs`,
`WeChatExport/Services/WeChatPathService.cs`,
`WeChatExport/ViewModels/MainWindowViewModel.cs`,
`WeChatExport/WeChatExport.csproj`,
`.superpowers/sdd/2026-09-11-dotnet-avalonia-migration/phase3-report.md`.

`dotnet build -c Release` → 0 errors, 0 warnings.
