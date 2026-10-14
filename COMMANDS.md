# Kugutsu MCP commands

English | [日本語](COMMANDS.ja.md)

The Japanese reference is [COMMANDS.ja.md](COMMANDS.ja.md). Kugutsu exposes MCP tools for request submission, request status and cancellation, window and control operations, browser and DOM operations, Aloe batches, locks, and signals.

## Request lifecycle

Use `execute_operation` for one operation and `submit_batch` for a JSON operation list or an Aloe program. Accepted work enters a FIFO queue. A queued response means that work was accepted, not completed. Use `get_request` to read the final state and result; `cancel_request` requests cooperative cancellation and does not roll back side effects already sent to an application.

## Tool groups

| Group | Examples | Reference |
| --- | --- | --- |
| Connection and discovery | `get_version`, `list_windows`, `ui.list_drivers` | [SPECIFICATION.md](SPECIFICATION.md) |
| Request processing | `execute_operation`, `submit_batch`, `get_request`, `cancel_request` | [docs/USAGE.md](docs/USAGE.md) |
| Windows, controls, and screens | window, control, grid, `screen.capture` operations | [SPECIFICATION.md](SPECIFICATION.md) |
| Browser and DOM | `browser.*`, `dom.*` operations | [SPECIFICATION.md](SPECIFICATION.md) |
| Coordination | `lock_mcp`, `is_mcp_locked`, `renew_mcp_lock`, `unlock_mcp`, signal tools | [SPECIFICATION.md](SPECIFICATION.md) |

The complete operation names, input fields, validation, result shapes, state transitions, and error codes are defined in [SPECIFICATION.md, sections 5 and 8](SPECIFICATION.md#5-mcp-通信とコマンド). Keep that contract as the single source of truth. Batch syntax is described in [docs/ALOE_BATCH.md](docs/ALOE_BATCH.md).
