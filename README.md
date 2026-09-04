# SqlAssist for SSMS 22

**Database-aware completion, SQL expansion, and previews inside the SSMS 22 T-SQL editor.**

[English](README.md) · [繁體中文](README.zh-TW.md)

[![Release](https://img.shields.io/github/v/release/a73013110/SqlAssist.Ssms22?sort=semver)](https://github.com/a73013110/SqlAssist.Ssms22/releases)
[![License](https://img.shields.io/github/license/a73013110/SqlAssist.Ssms22)](LICENSE)
![SSMS 22.9.x](https://img.shields.io/badge/SSMS-22.9.x-5c2d91)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078d4)

<p align="center"><img src="docs/images/hero.png" width="900" alt="SqlAssist completion and schema details in the SSMS 22 query editor"></p>

SqlAssist is an **SSMS 22** VSIX, not a separate editor. Suggestions run locally; metadata comes only
from your connected SQL Server, with no cloud service or AI model involved.

[Download](https://github.com/a73013110/SqlAssist.Ssms22/releases) ·
[Get started](docs/getting-started.md) · [Docs](docs/index.md) ·
[Report an Issue](https://github.com/a73013110/SqlAssist.Ssms22/issues)

## Feature tour

### Complete SQL with database context

Suggestions adapt to each SQL clause, with acronym/CamelHump matching, aliases, script variables,
temporary tables, live column details, and keyword casing.

<p align="center"><img src="docs/images/completion.png" width="820" alt="Context-aware object completion and live column metadata in SSMS 22"></p>

### Expand boilerplate with Tab

Tab expands `*` into explicit columns. Committing an `INSERT`, `EXEC`, `MERGE`, or `ALTER` target
generates metadata-aware SQL ready to edit.

<p align="center"><img src="docs/images/expand-star.png" width="820" alt="Tab expands SELECT star into a formatted explicit column list"></p>

| `INSERT` | `EXEC` |
|:---:|:---:|
| <img src="docs/images/expand-insert-into.png" width="400" alt="INSERT completion generates columns and typed VALUES placeholders"> | <img src="docs/images/expand-exec.png" width="400" alt="EXEC completion generates named parameters"> |
| **`MERGE`** | **`ALTER PROCEDURE / FUNCTION`** |
| <img src="docs/images/expand-merge-into.png" width="400" alt="MERGE completion generates an editable statement skeleton"> | <img src="docs/images/expand-def-procedure.png" width="400" alt="ALTER completion loads the object definition"> |

### Inspect objects without leaving the query

Preview columns, keys, indexes, parameters, and DDL in place. F12 opens the complete definition in a new
query window using the current connection.

<p align="center"><img src="docs/images/structure-preview.png" width="820" alt="Object preview with columns, indexes, keys, and copyable DDL"></p>

### Turn result rows into useful output

Create a `#temp` script or `IN` list, copy Markdown or JSON, profile columns, and inspect cells truncated
by the SSMS grid.

<p align="center"><img src="docs/images/result-grid-utility.png" width="820" alt="Result-grid menu for export, profiling, and full cell content"></p>

Also included: T-SQL snippets with Tab Stops, automatic bracket/quote pairing, and feature toggles.

## Install

Requires **Windows x64** and **SSMS 22.9.x**.

1. Download `SqlAssist.Ssms22.vsix` from the latest [release](https://github.com/a73013110/SqlAssist.Ssms22/releases).
2. Close SSMS, run the VSIX installer, and restart SSMS.
3. **Tools → SqlAssist** confirms that it loaded.

> [!IMPORTANT]
> Keep SSMS T-SQL IntelliSense enabled; only its conflicting automatic list is suppressed.

> [!WARNING]
> [SSMS does not officially support third-party extensions](https://learn.microsoft.com/en-us/ssms/faq#are-extensions-supported-in-ssms).
> This project is validated on SSMS 22.9.x.

## Learn more

[Getting Started](docs/getting-started.md) covers setup and updates; [documentation](docs/index.md#主題)
covers features, settings, and development. Contributors begin with [CLAUDE.md](CLAUDE.md).
[MIT License](LICENSE).
