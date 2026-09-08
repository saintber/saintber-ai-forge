## ADDED Requirements

### Requirement: Dual-layer configuration file

The system SHALL support a global configuration file at `~/.saifg/config.yaml` and a project-level configuration file at `<target>/.saifg/config.yaml`, both using the `storage.{org,project,user}.{memory,keys,kb}` YAML schema.

#### Scenario: Reading merged configuration

- **WHEN** the global file sets `storage.org.memory: /global/org-memory` and the project file sets `storage.org.memory: /project/org-memory`
- **THEN** `saifg config get storage.org.memory --target ./my-project` SHALL return `/project/org-memory`

#### Scenario: Falling back to global when project value is unset

- **WHEN** the global file sets `storage.user.kb: /global/user-kb` and the project file does not set `storage.user.kb`
- **THEN** `saifg config get storage.user.kb --target ./my-project` SHALL return `/global/user-kb`

##### Example: merge precedence

| Key | Global value | Project value | Resolved value |
| --- | --- | --- | --- |
| `storage.org.memory` | `/global/org-memory` | `/project/org-memory` | `/project/org-memory` |
| `storage.user.kb` | `/global/user-kb` | (unset) | `/global/user-kb` |
| `storage.project.keys` | (unset) | `/project/keys` | `/project/keys` |
| `storage.org.keys` | (unset) | (unset) | (empty/unset) |

### Requirement: `-g/--global` flag targets the global layer

The system SHALL default `config get`, `config set`, `config list`, and `config path` to operate on the project-level configuration file, and SHALL operate on the global configuration file only when the `-g`/`--global` flag is supplied.

#### Scenario: Setting a value in the project layer by default

- **WHEN** a user runs `saifg config set storage.project.memory /data/memory --target ./my-project` without `--global`
- **THEN** the system SHALL write the value into `./my-project/.saifg/config.yaml`, creating the file and its parent directory if they do not exist

#### Scenario: Setting a value in the global layer

- **WHEN** a user runs `saifg config set storage.user.keys /secure/keys -g`
- **THEN** the system SHALL write the value into `~/.saifg/config.yaml`, creating the file and its parent directory if they do not exist

### Requirement: `config path` reports the configuration file location

The system SHALL print the absolute path of the requested configuration layer's file when `saifg config path` is run, regardless of whether the file currently exists.

#### Scenario: Printing the project config path

- **WHEN** a user runs `saifg config path --target ./my-project`
- **THEN** the system SHALL print the absolute path `./my-project/.saifg/config.yaml` (resolved), even if the file has not yet been created

### Requirement: Unset configuration keys resolve to empty

The system SHALL return an empty result (not an error) and exit with code 0 when `saifg config get` is called with a key that is not set in either configuration layer.

#### Scenario: Getting an unset key

- **WHEN** a user runs `saifg config get storage.org.keys --target ./my-project` and neither the global nor the project file sets `storage.org.keys`
- **THEN** the system SHALL print an empty string and SHALL exit with code 0
