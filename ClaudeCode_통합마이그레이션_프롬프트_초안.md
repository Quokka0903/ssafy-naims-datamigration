You are working on an existing C# WPF MVVM project named DBImplant_N1toN2.

Your job is to transform the current codebase into a single integrated WPF migration workbench.

# Final product goal

Build ONE WPF application that supports TWO source modes:

1. Legacy MSSQL + legacy file tree -> Target MSSQL
2. Legacy Oracle + legacy file tree -> Target MSSQL

This is not two separate apps. It is one integrated WPF workbench with two source modes.

# User-facing required features

1) Legacy structure explorer
- The user must browse legacy data as FK-based hierarchical lists.
- When the user selects a row in a top-level list, the child list that references that row by FK must appear on the right.
- When the user selects a child row, the next child list must appear on the right again.
- File-tree-backed entities must also participate where relevant.

2) Migration runner
- The user must run migration jobs from legacy sources into target MSSQL.
- Show progress, success count, failure count, failed items, and failure reasons.

3) Bidirectional mapping inspector
- When the user selects a legacy DB row or legacy file, the corresponding migrated target MSSQL row must also be found and selected.
- Reverse lookup must also work from target back to legacy.

4) Wav/Grm compare viewer
- This is only for visual/manual validation.
- No advanced DSP validation is required.
- When wav is selected, show a simple time-series or gram-style side-by-side comparison.
- When grm is selected, parse and render the migrated-side payload for visual inspection.

5) Session/config/logging
- Source and target connection info and root paths must be configurable and stored in config files.
- In addition to in-memory state, the app must save file-based session logs for:
  - legacy item -> target item mapping results
  - failed items and failure reasons
  - current and historical selection state during the session

# Important current codebase facts you must respect

The current codebase already has:
- a 4-tab WPF structure
  - RawTabViewModel
  - NarrowBandTabViewModel
  - ShortPeriodTabViewModel
  - FolderTabViewModel
- runtime-selectable RootPath pattern using backing field + SelectRootPathCommand
- DataGrid stabilization where text columns are effectively read-only
- parsing pipelines for raw/nb/sd-related files
- partial/unfinished MSSQL insert logic
- existing parser/service code under ReadFileToDB/*
- existing Common, Model, ViewModel folder structure

Do NOT throw away this codebase.
Refactor incrementally.
Reuse existing parsers and existing ViewModels where practical.

# Critical constraints

- Keep WPF as the main application shell.
- Keep MVVM.
- Do not invent schema mapping details that are not provided.
- Do not hard-code connection strings, credentials, or machine-specific paths.
- Do not break already-stabilized behaviors unless there is a clear reason.
- Treat existing parser code as valuable assets to reuse.
- Since final legacy->target mapping rules are NOT fully provided yet, do not implement guessed final DB insert mappings.
- Instead, create contracts, services, state models, shell structure, selection synchronization, logging, and extension points first.

# What to build in this first phase

You are NOT asked to finish the entire migration logic in one pass.
You are asked to build the first safe and reusable foundation.

Implement Phase 1 with these goals:

A. Introduce an integrated workflow-oriented shell
- Keep the existing logic, but reorganize the application around these workflow areas:
  - Source Explorer
  - Migration Runner
  - Mapping Inspector
  - Wav/Grm Viewer
  - Settings / Session
- Existing 4-tab functionality may be reused as submodules or embedded views/services.

B. Add source mode abstraction
- Add a source mode concept:
  - LegacyMssql
  - LegacyOracle
- The UI and workflow should be aware of the chosen mode.
- Oracle real implementation may remain as stub/contract if details are missing.

C. Add session state and synchronization layer
Create in-memory structures for:
- MigrationSessionState
- MappingIndex
- SelectionSyncService

These must support:
- current source mode
- current source/target configs
- currently selected legacy item
- currently selected target item
- mapping lookup in both directions
- migration result tracking for the current session

D. Add file-based session logging
Design and implement file-based logs, using JSON and/or JSONL, for:
- session metadata
- mapping results
- failures
- selection history

Make the logging structure replaceable/extensible later.
Do not tightly couple it to final DB schema.

E. Add settings/config loading
Introduce config file models and loading/saving for:
- source mode
- legacy source connection settings
- target MSSQL connection settings
- root paths
- logging paths
- optional UI/session settings

F. Keep migration execution logic extensible
- Wrap current/partial insert logic behind interfaces/services.
- Do not guess final mapping rules.
- Create extension points for future legacy->target mapping specifications.

G. Add a minimal visual compare scaffold
- Add a minimal compare view/service abstraction for wav/grm.
- Only basic visual/manual comparison is required in Phase 1.
- Prefer simple rendering architecture that can evolve later.

# Existing code reuse guidance

Prefer reusing and adapting:
- MainWindowViewModel and current tab ViewModels
- existing parser modules in ReadFileToDB/*
- Common command infrastructure
- current RootPath selection pattern
- existing short-period / narrow-band / raw parsing flows

Do not rewrite parsers unless necessary.
Do not perform unrelated cleanup.

# Expected output behavior

Before making large changes:
1. inspect the current solution structure
2. explain what you found
3. explain the Phase 1 design
4. list exact files to add/modify
5. then implement the code in incremental, buildable steps

# Required response format

For your response and work, use this format:

1. Findings from current code
2. Phase 1 design decision
3. Files to add
4. Files to modify
5. Implementation steps
6. Code changes
7. Build verification checklist
8. Open points intentionally deferred until legacy->target mapping spec is provided

# Additional guardrails

- If a required detail is not known, explicitly mark it as deferred instead of inventing it.
- If Oracle-specific implementation details are missing, create interfaces/contracts and a clear stub.
- Keep all new code directly compatible with the current .NET 8 WPF project style.
- Preserve current stabilized behavior such as RootPath selection pattern and read-only display intent in DataGrids.

# Additional workbook-driven instructions

You MUST treat the attached workbook as the migration source of truth.

## Workbook usage
- 01_폴더파일_트리: legacy file-tree structure and source artifact inventory
- 02_NAIMS_DB_테이블: recognized legacy DB table inventory
- 03_마이그레이션_매핑: authoritative field-level migration mapping
- 04_테이블_관계도: target relationship reference
- 05_TableManager_분석: existing schema/program analysis notes
- 06_검토요약: summary of fixed vs deferred items
- 07_검토리스트: historical review decisions
- 08_제외목록: explicitly excluded mappings
- 09_결정규칙: authoritative policy decisions
- 10_최종확정필요목록: items intentionally deferred and still requiring future specification
- 11_INSERT_순서체크리스트: draft migration execution order and checkpoints

## Implementation rules from workbook
- Treat 03_마이그레이션_매핑 + 09_결정규칙 as implementation source of truth.
- Where a row is Fixed, implement it.
- Where a row is Exclude, do not migrate it.
- Where a row remains in 10_최종확정필요목록, do NOT invent final behavior. Create explicit TODO/extension points and validation/logging hooks only.

## Critical migration rules you must honor
- DELETED = 0
- IS_TRAINING = 0
- Legacy PKs do NOT map to target RECORD_ID; target RECORD_ID/IDENTITY is newly generated
- For .sd Signal_*.wav, the * suffix equals EventID
- REF_EXTRACT_DATA_STORE_ID must be filled only after:
  1) inserting the binary into EXTRACT_DATA_STORE
  2) retrieving the created RECORD_ID
  3) storing that RECORD_ID in the FK column
- TR Snippets (*.wav, *.grm) belong to PREC_SS_SEGMENT
- PREC_NB_ANALYSIS.AI_DIFFUSION_MASK is not populated
- PREC_NB_ANALYSIS.SELECTED_DEMON_BAND defaults to 1
- PREC_NB_CLASSIFICATION latitude/longitude must be split to degree/minute/second
- Distance fields noted in the workbook are currently assumed to be nm
- HarmonicList parsing rule:
  split by spaces; numeric part -> HARM_NO; suffix p/P -> PREDOMINANCE=1, else 0; FREQ=0
- ALG parsing:
  direct fields like Detection/DetSonar may be mapped now, but SUPPLEMENT_DATA binary serialization remains deferred
- NB snippet metadata FREQ_MIN/FREQ_MAX/FRAME_NUM/BIN_NUM/RESOLUTION remains deferred until grm conversion specification exists

## What to do with deferred items
For every item from 10_최종확정필요목록:
- keep implementation buildable
- add interface/contract/service placeholders if needed
- add logs and TODO markers
- never guess final DB mapping behavior

## Additional required deliverables in your response
Along with code changes, also provide:
- how sheet 11 (INSERT 순서 체크리스트) maps to the implemented services
- which rows from sheet 10 remain deferred after your implementation pass
- what exact sample files or schema details are still needed to close those deferred items
