# Excel plug-in

A signed plug-in that lets agents read, query, write, format, and **recalculate**
Excel workbooks (`.xlsx`) entirely in-process using
[ClosedXML](https://github.com/ClosedXML/ClosedXML) — no Microsoft Office install
required.

## Tools

| Tool | Description |
|---|---|
| `excel.list_sheets` | List sheets and dimensions. |
| `excel.read_range` | Return a 2-D array of cells from `A1:D50`-style range. |
| `excel.read_table` | Return rows of a named or auto-detected table. |
| `excel.find` | Case-insensitive substring search across cells. |
| `excel.describe` | Per-column stats (count, nulls, distinct, min/max/mean/median, top-K). |
| `excel.pivot` | Group-by aggregation: `sum`, `avg`, `count`, `min`, `max`. |
| `excel.write_cells` | Write a 2-D array. Strings starting with `=` are written as formulas. |
| `excel.append_row` | Append one row at the next free row. |
| `excel.create_workbook` | Create a new `.xlsx`. |
| `excel.set_format` | Apply number format / bold / fill color to a range. |
| `excel.set_formula` | Set a formula and return its computed value. |
| `excel.recalculate` | Force recompute every formula in the workbook and save. |
| `excel.evaluate` | Evaluate a one-off formula against an optional workbook context. |

## Configuration

The owner sets the plug-in's `ConfigJson` from the **Skills** tab. Example:

```json
{
  "allowedRoots": [
    "C:\\Users\\admin\\Documents\\Spreadsheets",
    "D:\\Reports"
  ],
  "maxRowsPerCall": 5000,
  "maxCellBytes": 25000,
  "allowWrites": false
}
```

| Key | Default | Purpose |
|---|---|---|
| `allowedRoots` | `[]` | Folders the plug-in is allowed to open files from. Paths outside these roots return `path_not_allowed`. |
| `maxRowsPerCall` | `5000` | Cap rows returned by `read_range` / `read_table`. |
| `maxCellBytes` | `25000` | Cap JSON size returned per call. |
| `allowWrites` | `false` | When `false`, every write tool returns `writes_disabled`. Owner must opt-in. |

In addition to `allowedRoots`, the per-conversation work directory (passed by the
host on every invoke) is always allowed, so an agent can write its own scratch
files without admin configuration.

The `MLA_EXCEL_ALLOWED_ROOTS` environment variable (semicolon-separated) is also
merged into `allowedRoots`, useful for testing.

## Build

```powershell
pwsh ./plugins/excel/build.ps1                            # dev build
pwsh ./plugins/excel/build.ps1 -InstallTo "$env:LOCALAPPDATA\MyLocalAssistant\state"
```

The script publishes, hashes, signs and packs into
`plugins/excel/publish/<keyId>-excel.mlaplugin`. With `-InstallTo`, the plug-in
is unpacked into `<state>/plugins/excel/` and the matching public key is copied
to `<state>/config/trusted-keys/`.

## Notes

- ClosedXML's formula engine supports ~300 Excel functions
  ([compatibility list](https://github.com/ClosedXML/ClosedXML/wiki/Functions)).
  Anything not implemented (e.g. `LET`, `LAMBDA`, dynamic arrays) returns
  `formula_error` rather than producing wrong values silently.
- Range strings use Excel A1 notation (`B2:D10`, `Sheet2!A1:Z100`).
- Dates are returned as ISO-8601 strings so the model can parse them
  unambiguously.
