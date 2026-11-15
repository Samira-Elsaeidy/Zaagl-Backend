# E2.3 Instant clipboard transformers (JSON/CSV to table link)

PRD: "Convert copied data while sharing, e.g. turn copied JSON or CSV into a clean, readable table link."

## Model
A table is a **content link of kind `table` (9)** (viewer `app.zaa.gl/t/<code>`) whose row in `collab.tables` holds the normalised
table as `jsonb`: `{"columns":[{"name","type"}],"rows":[[cell,…],…]}`. Cells are JSON scalars only (string, number, boolean, null);
a nested JSON object/array becomes compact JSON **text** (column type `json`). The stored bytes count toward the owner's quota.

`collab.tables`: `resource_id` PK, `owner_id` (null for a guest), `source_format` (`json|csv`), `column_count` (1-500), `row_count`
(0-200,000), `document_bytes`, `document` (jsonb), `created_at`. Checks mirror the ceilings.

Why not the snippet model: a snippet is a text object served by ticket from `usercontent.zaa.gl`; a table is structured data that the
viewer pages through. We reuse everything else of the content model (resource, link, key, password, expiry, takedown, library, purge).

## Parsing (strict, bounded, nothing evaluated) - `TableParser`
- Input: `{text, format: auto|json|csv, delimiter?: , ; tab |, header?: true}`. `auto` = JSON when the text starts with `[` or `{`.
  A UTF-8 BOM is stripped; NUL is refused anywhere.
- Size cap first: UTF-8 bytes of `text` ≤ plan `TableMaxInputBytes` → `413 input-too-large`.
- **JSON**: `JsonDocument` with depth ≤ 16, no comments, no trailing commas. Accepted shapes: array of objects (columns = union of keys
  in first-seen order), array of arrays (`column 1…n`), array of values (one `value` column), an object (two columns `key`, `value`).
  Numbers longer than 64 characters are refused.
- **CSV**: RFC 4180 (quoted cells, doubled quotes, CRLF or LF, line breaks inside quotes); a quote inside an unquoted cell is literal
  (`=HYPERLINK("x")` pasted from a sheet); text after a closing quote is an error; blank lines skipped; delimiter detected from the
  first line when not given. With `header` (default) the first row names the columns; a row wider than the header is an error, a
  shorter one is padded with nulls.
- Limits (plan): rows `TableMaxRows`, columns `TableMaxColumns` (→ `413 input-too-large`); cell ≤ 10,000 characters and column name
  ≤ 200 characters (→ `400`). Column names: control characters removed, trimmed, empty → `column N`, duplicates → `name (2)`.
- **Type inference** per column over non-empty cells: `integer`, `number` (invariant culture, mixed integer/number → number),
  `boolean` (`true`/`false`), `date` (`yyyy-MM-dd`), `datetime` (ISO 8601), `json`, else `string`; all empty → `empty`. CSV cells are
  stored as the original text (no lossy number conversion; large integers stay exact); JSON scalars keep their JSON type.
- **No formula is ever executed or interpreted**: cells are data. The viewer must render them as text.

## Export (download as CSV or JSON)
`GET …/export?format=csv|json` returns `{fileName, mime, content}` **as JSON data**; the browser saves it (Blob). No table bytes are
served from the API origin as a document, so the "bytes only from usercontent by ticket" rule is not bent.
- **CSV injection guard** (`TableExport.Field`): a cell starting with `=`, `+`, `-`, `@`, tab or carriage return is prefixed with a
  single quote `'`; **exception**: a cell that is a plain number (`-12.5`, `+3`) is left as is (it cannot be a formula). Cells with the
  delimiter, a quote or a line break are quoted (quotes doubled). The header row gets the same treatment. Line ends are CRLF.
- JSON export: an array of objects keyed by column name, values unchanged (JSON is not a formula surface).
- File name: the title reduced to ASCII letters, digits and `-` (default `table`).

## Endpoints
Code: `src/Zaagl.Api/Endpoints/TableEndpoints.cs`, `src/Modules/Collaboration/Application/TableService.cs`, `TableParser.cs`.
Tests: `tests/Zaagl.Api.Tests/Phase2/ClipboardTableTests.cs` (`TableParserTests` unit, `ClipboardTableTests` API).

| Method and route | Who | What |
|---|---|---|
| `POST /api/tables` | JWT, API key `tools:write`, or guest (panel `guest-table`) | `{text, format?, delimiter?, header?, title?, expiresAt?, password?, customSlug?, public?}` → `201 {resourceId, code, shortUrl, linkKey?, expiresAt}` |
| `GET /api/tables/{id}?offset=&limit=` | owner (`resources:read`) | State, columns, one page of rows (limit ≤ 5,000, default 1,000), stored bytes. |
| `GET /api/tables/{id}/export?format=` | owner (`resources:read`) | `{fileName, mime, content}`. |
| `DELETE /api/tables/{id}` | owner (`tools:write`) | Link deleted (410). |
| `GET /api/public/tables/{code}?offset=&limit=` | link holder (gate) | `{kind:"table", title, expiresAt, hasPassword, sourceFormat, rowCount, columns, offset, count, rows}` |
| `GET /api/public/tables/{code}/export?format=` | link holder (gate) | `{fileName, mime, content}`. Consumes nothing. |
Owner list: `GET /api/resources?kind=table`; renew, password, public via `PATCH /api/resources/{id}`.

## Plan gating (PROPOSED, `Plans:<Plan>:Tools:*`)
| | Guest | Free | Starter |
|---|---|---|---|
| Create (`TablesEnabled`) | yes (CAPTCHA `guest-table`, PD-14 counters) | yes | yes |
| Input (`TableMaxInputBytes`) | 64 KB | 256 KB | 2 MB |
| Rows (`TableMaxRows`) | 500 | 5,000 | 50,000 |
| Columns (`TableMaxColumns`) | 20 | 50 | 200 |
| Password | no | no | yes (PD-54) |
| Public | no | yes | yes |
Ceilings (`ToolCeilings`): 8 MB, 200,000 rows, 500 columns.

## Security and abuse
- Guests go through the admin panel (`guest-table`: pause, CAPTCHA toggle), the in-memory brakes and the PD-14 counters (shared
  budget with snippets/uploads), consumed after the free checks and refunded on failure. A failing credential is 401 (PD-36).
- Keyed by default (`#k=`); a wrong key is the same 404 as an unknown code. Owner routes 404 for others; `private, no-store` everywhere.
- Parsing is linear and bounded (size cap before parsing, row/column/cell/depth caps during), so a paste cannot exhaust CPU or memory.
- XSS: cells are JSON data; the UI must render text only. CSV injection: export guard above.
- Logs never contain table content.

## Retention
Content retention (guest 7 days, purged at expiry; Free 7, Starter 30 days, quarantine then purge). `TableKindHandler` deletes the row
when Resources reclaims the resource.

## What the UI needs
1. **Paste box**: a textarea (paste or drop a `.csv`/`.json` file read client-side), format selector (Auto/JSON/CSV), CSV options
   (delimiter, "first row is a header"), a client-side preview of the first rows (optional: the server is authoritative), title,
   expiry, password (Starter), public toggle; guests: Turnstile (action `guest-table`) when `GET /api/guest-status` says
   `table.captchaRequired`; show `413 input-too-large` with the plan limit and `400` messages (they name the row).
2. **Viewer `app.zaa.gl/t/<code>`**: sticky header with column names and a small type badge, right-aligned numbers, booleans as
   check marks, `json` cells in a monospace popover, null as an empty cell; virtualised rows with paging (`offset`/`limit`), client-side
   sort/filter on the loaded page (or fetch all for small tables), "Download CSV" / "Download JSON" buttons (call `/export`, save the
   `content` as a Blob named `fileName`), copy-as-TSV, password form, 404/410 states. Render every cell as **text**.
3. Owner card in the library: row/column counts, delete, renew.

## Open questions
1. Approve the plan values; should guests be allowed at all (proposed: yes, like snippets)?
2. Server-side sort/filter/search for large tables (today: paging only)?
3. Should the CSV export exempt plain numbers from the formula prefix (implemented) or prefix strictly every `-`/`+` cell?
4. Excel (`.xlsx`) export or import?
5. Allow editing (re-paste) to create a new version like snippets?
