# SqlAssist for SSMS 22

**Supercharge the SSMS 22 T-SQL editor with schema-aware autocomplete, statement expansion, and instant object previews.**

[English](README.md) · [繁體中文](README.zh-TW.md)

[![Release](https://img.shields.io/github/v/release/a73013110/SqlAssist.Ssms22?sort=semver)](https://github.com/a73013110/SqlAssist.Ssms22/releases)
[![License](https://img.shields.io/github/license/a73013110/SqlAssist.Ssms22)](LICENSE)
![SSMS 22.9.x](https://img.shields.io/badge/SSMS-22.9.x-5c2d91)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078d4)

<p align="center">
  <img src="docs/images/hero.png" width="900"
       alt="SqlAssist autocomplete card in SSMS 22 editor with matched characters highlighted">
</p>

SqlAssist is a native VSIX extension for **SQL Server Management Studio 22**. It is neither a separate SQL editor nor a modified build of SSMS—you stay right within your familiar query window.

All suggestions and schema metadata are computed **locally on your machine**. Queries are made solely to the **SQL Server instance you are already connected to**. **Zero cloud dependencies, no external telemetry, and no AI models involved**—ensuring strict compliance with enterprise security and privacy standards.

[Getting Started](docs/getting-started.md) ·
[Download VSIX](https://github.com/a73013110/SqlAssist.Ssms22/releases) ·
[Report Issue](https://github.com/a73013110/SqlAssist.Ssms22/issues) ·
[Documentation](docs/index.md)

---

## Core Experience: Real-Time Autocomplete

As you type, suggestions dynamically adapt to your current syntactic context. Selecting any candidate immediately reveals its columns, data types, and primary key flags in the preview panel below—no need to switch windows or query system catalogs manually.

<p align="center">
  <img src="docs/images/completion.png" width="820"
       alt="Typing SELECT * FROM Li pops up suggestions with Libraries and LibraryBranches; the bottom panel displays column types and flags for dbo.Libraries">
</p>

- **Abbreviation & Fuzzy Matching**: Powered by CamelHump and word-boundary matching. When you recall an object's purpose but not its exact name, typing a few initials or letters finds it instantly (e.g., `cb` finds `Cat_BookCopy`, `libr` finds `Lib_Reader`).
- **Live Column & Type Panel**: Highlighting any table or view instantly lists its columns, data types, and `PK` / `NOT NULL` constraints in the panel directly below.
- **Context-Aware Filtering**: Prioritizes tables and views after `FROM` or `JOIN`; prioritizes columns after `SELECT`, `WHERE`, and `ORDER BY`; prioritizes stored procedures after `EXEC`.
- **Alias Resolution**: Typing `lr.` resolves aliases in real time and lists columns from the aliased source table.
- **Live Script Variable Parsing**: In-flight `@variables`, `#temp` tables, and `@table` variables declared in your current query are parsed locally on the fly—available immediately without waiting for server metadata refreshes.
- **Auto-Uppercase Keywords**: Automatically normalizes T-SQL keywords to uppercase as you type, maintaining consistent formatting effortlessly.

---

## Wildcard & Statement Expansion

Eliminate repetitive boilerplate typing across your everyday T-SQL workflows.

### 1. Expand `SELECT *` into Explicit Columns

Place the cursor after `*` and press `Tab` to expand it into an indented, multi-line list of explicit columns. For multi-table queries, column names are automatically qualified with table aliases, avoiding ambiguity and query performance pitfalls.

<p align="center">
  <img src="docs/images/expand-star.png" width="820"
       alt="SELECT * FROM dbo.Books with cursor after asterisk; pressing Tab expands it into an indented list of explicit column names">
</p>

### 2. Statement Commit Expansion

Selecting an object in specific syntax positions expands complete, ready-to-run statement scaffolds:

- **`INSERT INTO`**: Selecting a table expands formatted column lists and inserts type-aware default value placeholders in the `VALUES` clause.
- **`EXEC`**: Selecting a stored procedure expands all named parameters, type annotations, and necessary `OUTPUT` variable declarations.
- **`ALTER PROCEDURE / FUNCTION`**: Typing `ap` or `af` and selecting an object inserts its complete, executable definition directly into the editor—ready for in-place modification and execution.
- **`MERGE INTO`**: Selecting a table generates a comprehensive `USING ... ON ... WHEN MATCHED` template.

---

## Schema Preview & Go to Definition

Inspect object schemas and underlying definitions without digging through deep Object Explorer trees.

<p align="center">
  <img src="docs/images/structure-preview.png" width="820"
       alt="Floating structure preview script tab showing CREATE TABLE and index DDL with T-SQL syntax highlighting">
</p>

- **Floating Structure Preview**: Hover over an object for a quick tooltip, or open the structure preview window to inspect columns, constraints, and indexes. Switch to the **Script** tab to scroll, select, and copy clean DDL scripts directly.
- **F12 Go to Definition**: Place the cursor on any table, view, or stored procedure name and press `F12`. SqlAssist opens the complete, executable definition in a new query window, automatically inheriting your current connection and database context.

---

## Result Grid Utilities

After running a query, right-click any selected cells in the result grid to transform data directly in memory, without issuing extra server round-trips:

- **Script as #temp Table**: Generates an executable script containing `CREATE TABLE #SqlAssistRows`, batched `INSERT` statements, and a final `SELECT` in a new query window for instant debugging.
- **Copy as IN Clause**: Formats selected values into a clean `IN ('val1', 'val2')` predicate ready to paste straight into a `WHERE` clause.
- **Copy as Markdown Table**: Copies the selection as an aligned Markdown table, perfect for GitHub PRs, issues, or team chat.
- **Copy as JSON**: Exports rows as a structured JSON array of objects.
- **Column Profiling**: Essential for wide tables—instantly analyzes null counts, empty strings, distinct value counts, and data/length ranges across every column in the selection.
- **View Full Cell Content**: Overcomes the default SSMS 65,535 character grid display truncation to inspect, scroll, and copy massive XML, JSON, or text content in a dedicated window.

---

## Snippets & Auto-Pairing

- **45 Built-in T-SQL Snippets**: Comprehensive templates for DDL, DML, control flow, and maintenance. Use `Tab` / `Shift+Tab` to seamlessly jump between replacement fields.
- **Smart Character Pairing**: Typing `(`, `'`, or `[` automatically inserts the closing character, with built-in type-over and coordinated backspace removal.

---

## What It Does for You

| Everyday SQL Task | How SqlAssist Helps |
|---|---|
| Know the purpose, but not the exact object name | Type `libr` to find `Lib_Reader` via word-boundary fuzzy matching |
| Want to inspect columns without leaving the editor | Type `lr.` to immediately list columns, data types, and primary keys |
| Need to replace `SELECT *` with explicit columns | Place cursor after `*` and press Tab to expand into an aligned column list |
| Tired of typing `INSERT` columns or `EXEC` parameters | Automatically expands full column lists, default values, or named parameters |
| Need to edit a stored procedure without browsing Object Explorer | Type `ap` and pick the proc; the full `ALTER` definition opens in your editor |
| Encounter an unfamiliar object and want to see its source code | Press F12 on the name to open its executable definition using the same connection |
| Want to verify table structure, keys, or indexes beforehand | Hover for summary or open floating preview to copy clean DDL scripts |
| Frequently write repetitive T-SQL boilerplate | Use 45 built-in snippets with Tab / Shift+Tab field navigation |
| Frequently miss closing parentheses or quotes | Type `(` or `'` to auto-close; type-over to skip, Backspace to delete both |
| Want to reproduce a bug locally using a few query result rows | Right-click the grid selection to script as a `#temp` table or `IN` predicate |
| Faced with a 100+ column result set and don't know where to start | Column profiling displays null rates, distinct counts, and ranges in one glance |
| A cell contains large XML/JSON truncated by the grid | View full content in a dedicated viewer with scroll, select, and copy support |
| Need to paste query results into a GitHub PR or ticket | One-click copy as an aligned Markdown table |

---

## Installation

Requirements: **Windows x64** and **SSMS 22.9.x**. No need to clone this repository or install the .NET SDK.

1. Go to [Releases](https://github.com/a73013110/SqlAssist.Ssms22/releases) and download `SqlAssist.Ssms22.vsix` from the latest release **Assets**.
2. Save your open queries and close all SSMS windows.
3. Run the `.vsix` installer and ensure the target is **SQL Server Management Studio 22**.
4. Restart SSMS. Look for **Tools → SqlAssist** in the top menu to verify the installation.

For troubleshooting, first-run tips, or uninstallation instructions, see [Getting Started](docs/getting-started.md).

> [!IMPORTANT]
> Keep SSMS native T-SQL IntelliSense enabled. SqlAssist by default only suppresses conflicting completion lists; red error squiggles, outlining, and parameter info are still provided by SSMS.

> [!WARNING]
> [SSMS does not currently provide an official third-party extension marketplace](https://learn.microsoft.com/en-us/ssms/faq#are-extensions-supported-in-ssms). This project has been validated on SSMS 22.9.x. Future major SSMS releases may require updated compatibility verification.

---

## Documentation

| Topic | Document |
|---|---|
| Installation, verification, and first steps | [Getting Started](docs/getting-started.md) |
| Autocomplete, fuzzy matching, and keyword casing | [Completion](docs/completion.md) |
| INSERT, EXEC, and ALTER statement commit expansion | [Commit Expansion](docs/completion-commit-expansion.md) |
| `SELECT *` wildcard expansion formatting rules | [Wildcard Expansion](docs/wildcard-expansion.md) |
| Built-in & custom snippets and Tab navigation | [Code Snippets](docs/snippets.md) |
| Automatic bracket and quote pairing | [Auto Pairing](docs/auto-pairing.md) |
| Tooltips and floating schema/DDL preview | [Structure Preview](docs/structure-preview.md) |
| F12 Go to Definition in a new query window | [Go to Definition](docs/go-to-definition.md) |
| Result grid utilities: #temp, IN clause, and profiling | [Result Grid](docs/result-grid.md) |
| Feature toggles, UI options, and diagnostics | [Settings](docs/settings.md) |

---

## Developer Guide

If you would like to inspect the source code, build the VSIX locally, or contribute:

- [Documentation Index](docs/index.md): Map tasks to documentation and code paths.
- [Build, Test, and Release](docs/development.md): Development environment setup and automation scripts.
- [Architecture](docs/architecture.md): Division of responsibilities across Core, Metadata, and SSMS integration layers.
- [Development Guidelines](CLAUDE.md): Design constraints and lessons learned.

---

## License

This project is licensed under the [MIT License](LICENSE).
