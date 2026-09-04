# SqlAssist for SSMS 22

**Database-aware autocomplete, SQL expansion, and object previews inside the native SSMS 22 T-SQL editor.**

[English](README.md) · [繁體中文](README.zh-TW.md)

[![Release](https://img.shields.io/github/v/release/a73013110/SqlAssist.Ssms22?sort=semver)](https://github.com/a73013110/SqlAssist.Ssms22/releases)
[![License](https://img.shields.io/github/license/a73013110/SqlAssist.Ssms22)](LICENSE)
![SSMS 22.9.x](https://img.shields.io/badge/SSMS-22.9.x-5c2d91)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078d4)

<p align="center">
  <img src="docs/images/hero.png" width="900"
       alt="SqlAssist completion list and object details inside the SSMS editor">
</p>

SqlAssist is a VSIX extension for **SQL Server Management Studio 22**, not a separate editor.
Suggestions are computed locally. Schema metadata is queried only from the SQL Server you are already
connected to; it is not sent to a cloud service and no AI model is involved.

[Getting Started](docs/getting-started.md) ·
[Download VSIX](https://github.com/a73013110/SqlAssist.Ssms22/releases) ·
[Report an Issue](https://github.com/a73013110/SqlAssist.Ssms22/issues) ·
[Documentation](docs/index.md)

## Highlights

<p align="center">
  <img src="docs/images/completion.png" width="820"
       alt="A context-aware completion list showing database objects and columns">
</p>

- **Context-aware completion:** narrows suggestions after `SELECT`, `FROM`, `JOIN`, `EXEC`, and other
  clauses. Includes acronym/CamelHump matching, alias columns, script variables, temporary tables, and
  automatic keyword casing.
- **SQL expansion:** press Tab after `*` to insert explicit columns. Committing an `INSERT`, `MERGE`,
  `EXEC`, or `ALTER` target can generate columns, parameters, a safe statement skeleton, or the full
  object definition.
- **Snippets and pairing:** built-in T-SQL snippets with Tab Stop navigation, plus automatic pairing for
  parentheses, quotes, and square brackets.
- **Schema preview and F12:** inspect columns, indexes, foreign keys, parameters, and DDL in a floating
  preview. F12 opens the definition in a new query window using the current connection.
- **Result-grid utilities:** turn selected rows into `#temp`, `IN`, Markdown, or JSON output; profile
  columns and inspect the complete contents of cells truncated by the SSMS grid.

## Install

Requires **Windows x64** and **SSMS 22.9.x**.

1. Download `SqlAssist.Ssms22.vsix` from the latest [GitHub Release](https://github.com/a73013110/SqlAssist.Ssms22/releases).
2. Save your queries and close every SSMS window.
3. Open the VSIX and confirm the target is **SQL Server Management Studio 22**.
4. Restart SSMS. The extension is loaded when **Tools → SqlAssist** appears.

> [!IMPORTANT]
> Keep SSMS T-SQL IntelliSense enabled. SqlAssist suppresses only the conflicting automatic member list;
> error squiggles, outlining, and parameter help continue to come from SSMS.

> [!WARNING]
> [SSMS does not currently provide official support for third-party extensions](https://learn.microsoft.com/en-us/ssms/faq#are-extensions-supported-in-ssms).
> This project is validated on SSMS 22.9.x; later SSMS updates may require compatibility verification.

## Documentation

| Need | Start here |
|---|---|
| Install, verify, update, or uninstall | [Getting Started](docs/getting-started.md) |
| Completion, expansion, snippets, previews, result grid, or settings | [Documentation Router](docs/index.md#主題) |
| Build and test | [Development](docs/development.md) |
| Version, release, and VSIX installation | [Release](docs/release.md) |
| Layers and platform boundaries | [Architecture](docs/architecture.md) |

Contributors should begin with [CLAUDE.md](CLAUDE.md), which routes each change to only the required
guardrails. Licensed under the [MIT License](LICENSE).
