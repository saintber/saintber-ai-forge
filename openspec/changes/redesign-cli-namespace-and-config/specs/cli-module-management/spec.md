## ADDED Requirements

### Requirement: Module subcommands replace flag-based filtering

The system SHALL provide `saifg module add <selector...>`, `saifg module remove <selector...>`, `saifg module update [<selector...>]`, and `saifg module list` as subcommands, replacing the previous `--module`/`--modules` flag-based filtering on `init`/`update`/`remove`.

#### Scenario: Adding modules by selector

- **WHEN** a user runs `saifg module add org.kb,prj.code --target ./my-project`
- **THEN** the system SHALL install only assets whose parsed scope/module match `org.kb` or `prj.code` into `./my-project/.github`, and SHALL update `./my-project/.copilot-library/state.json` to track the newly installed files

#### Scenario: Removing modules by selector

- **WHEN** a user runs `saifg module remove usr.docs --target ./my-project`
- **THEN** the system SHALL remove only the tracked files matching `usr.docs` from `./my-project/.github`, and SHALL update the state file to no longer track those files

#### Scenario: Updating all installed modules without a selector

- **WHEN** a user runs `saifg module update --target ./my-project` with no selector arguments
- **THEN** the system SHALL refresh every module currently tracked in the state file

#### Scenario: No matching files for a selector

- **WHEN** a user runs `saifg module add nonexistent-scope --target ./my-project` and no template asset matches the selector
- **THEN** the system SHALL print `Error: no files match selector(s): nonexistent-scope` and SHALL exit with a non-zero exit code

### Requirement: `saifg update` is a shorthand for updating all modules

The system SHALL treat `saifg update [--target <dir>]` (no subcommand) as equivalent to `saifg module update` with no selector arguments, refreshing every module currently tracked in the target's state file.

#### Scenario: Top-level update refreshes everything

- **WHEN** a user runs `saifg update --target ./my-project`
- **THEN** the system SHALL produce the same file changes and state update as running `saifg module update --target ./my-project`

### Requirement: `saifg module list` groups output by scope

The system SHALL list available module selectors and, for a target with an existing state file, list installed module selectors grouped by scope (`org`, `prj`, `usr`, and shared) when scope information is present in the tracked filenames.

#### Scenario: Listing installed modules with scope grouping

- **WHEN** a user runs `saifg module list --target ./my-project` and the target has assets installed under `org.kb`, `prj.code`, and `kb` (shared)
- **THEN** the output SHALL group the installed selectors under their respective scope headings, with shared assets listed separately from any scope
