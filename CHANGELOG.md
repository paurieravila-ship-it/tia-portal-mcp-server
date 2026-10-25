# Changelog

## Unreleased

### Added

- `tia_hmi_set_widget_property`: collection elements in the property path. `TrendAreas[0]` takes an element by
  position (index = current count appends one when the collection has a parameterless `Create()`, e.g. a second
  trend); `TrendAreas[area1].LeftValueAxes[ax]` takes it by name and creates it when missing.
- Multilingual texts can be written for one language only with `@culture`: `Text@en-US`,
  `TrendAreas[0].Trends[1].DisplayName@en-US`. Without it every language still gets the same text.
- `tia_hmi_get_software_object` and `tia_hmi_set_software_property`: read and change objects of the Unified HMI
  software that are not screen items (alarm classes and their state colours, runtime settings, tags, connections),
  with the same path rules.

### Fixed

- `tia_connect` with `projectPath` kept the current instance when already attached. With several TIA Portal
  instances open it now detaches and attaches to the one holding the requested project.

## 1.0.1

### Fixed

- Devices in TIA Portal's *Ungrouped devices* folder (distributed I/O, GSD devices added in the network view) were
  missing from `tia_list_devices`, `tia_list_modules`, `tia_list_ip_addresses` and every lookup by device name.
- `tia_hmi_set_widget_property`, `tia_hmi_set_widget_color` and `tia_hmi_set_button_text` reported `set: true`
  even when TIA Portal silently ignored the assignment. The property is now read back: `set` is true only if it
  holds the requested value, and `value` shows what it actually reads. `tia_hmi_create_items` lists such
  properties per item under `propertiesNotApplied`.

## 1.0.0

First public release.

### Safety

- Every tool has a safety class (read-only, write, destructive), shown at the start of its description and sent as
  MCP annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`).
- Destructive calls are refused without `confirm: true`. Tools with an `overwrite` argument are destructive only
  in that form.
- Access levels: `--access readonly | write | full`. Tools outside the level are neither listed nor callable.
- `--no-save`: only the user saves the project.
- Edits run inside a TIA transaction: rollback on failure, one undo unit per edit.
- Automatic backup of a block, data type or tag table before it is replaced or deleted.
- File arguments can be confined with `--allowed-dir`; live S7 reads are limited to addresses configured in the
  open project.
- Fail-safe (F) blocks cannot be deleted or overwritten.
- Optional JSON-lines audit log.

### PLC

- `tia_read_block`: block content inline (source text, or XML for graphical blocks).
- `tia_get_block_interface`: interface members with types, start values and comments.
- `tia_import_scl`: accepts inline source, generates into a folder, detects what it would replace, compiles the
  result and returns the compiler messages.
- `tia_create_lad_block`: ladder blocks from a structured rung description, with a dry-run mode.
- `tia_create_instance_db`, `tia_create_block_group`, `tia_delete_block_group`.
- PLC data types: `tia_list_types`, `tia_delete_type` (create and read through the block tools).
- Tags: `tia_create_tag_table`, `tia_create_plc_tags`, `tia_create_plc_constants`, `tia_update_plc_tag`,
  `tia_delete_plc_tag`, `tia_delete_tag_table`, `tia_export_tag_table`, `tia_import_tag_table`.
- `tia_get_cross_references`, `tia_list_watchtables`, `tia_delete_watchtable`, `tia_online_state`.
- `tia_compile`: single block, PLC software or hardware, or an HMI; compiler messages flattened to the ones that
  say something, each with its location.

### HMI (WinCC Unified)

- Screens are found inside screen groups; `tia_list_hmi_screens` works for Unified panels.
- `tia_hmi_get_screen`, `tia_hmi_get_item`: read a screen or one item in full.
- `tia_hmi_create_items`: build several items with properties, bindings and scripts in one atomic call.
- `tia_hmi_set_event_script` / `tia_hmi_remove_event_script`: scripts on any item type and on the screen.
- `tia_hmi_set_widget_property`: dotted paths (`Font.Size`) and multilingual texts; returns the previous value.
- Colours accept names, `#RRGGBB` and `r,g,b`; unknown names are rejected instead of silently becoming black.
- `tia_hmi_set_item_bounds` works on shapes as well as widgets.
- HMI tags: `tia_hmi_list_tags`, `tia_hmi_create_tags`, `tia_hmi_create_tag_table`, `tia_hmi_delete_tag`.
- Alarms: `tia_hmi_list_alarms`, `tia_hmi_create_alarm`, `tia_hmi_delete_alarm`.
- `tia_hmi_find_tag_usage`, `tia_hmi_replace_tag_refs` (dry run by default),
  `tia_hmi_update_faceplate_interface`, `tia_hmi_validate`, `tia_hmi_delete_screen`.

### Project, hardware, live values

- `tia_project_info`, `tia_archive_project`, `tia_doctor` (also `--doctor` on the command line).
- `tia_list_modules`: modules with their I/O address ranges.
- `tia_list_devices` reports PLC, Unified HMI and classic HMI software.
- Live S7: `tia_s7_read_values` (typed values by address), `tia_s7_read_tags` (PLC tags by name),
  `tia_s7_cpu_info`. The PLC address is taken from the project when not given.

### Server

- Tool groups (`--groups`) to shorten the tool list.
- Forgiving argument parsing: numbers, booleans and arrays sent as strings are accepted.
- MCP protocol version negotiation (2024-11-05, 2025-03-26, 2025-06-18) and server instructions.
- Build against another TIA Portal version with `-p:TiaVersion=NN`.
- Source split by area; tool reference generated from the code (`--dump-tools`).
- Offline and live test scripts.

### Removed

- `tia_debug_set_button_text` (a one-off experiment; `tia_hmi_set_button_text` does the job).

### Changed behaviour

- `tia_import_scl` no longer replaces an existing block silently: it needs `confirm: true` and makes a backup.
- Create tools with `overwrite: true`, and all delete tools, need `confirm: true`.
- `tia_compile` defaults to the PLC *software* (`scope: software`); use `scope: hardware` or `all` for the rest.
- Tool results omit null fields.
