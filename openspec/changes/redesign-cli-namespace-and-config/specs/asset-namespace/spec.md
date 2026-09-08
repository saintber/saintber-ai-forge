## ADDED Requirements

### Requirement: Filename-based scope and module parsing

The system SHALL parse each template asset filename into an optional `scope` (`org`, `prj`, or `usr`), a `module` (one of the existing module identifiers: `code`, `copilot`, `docs`, `kb`, `migration`, `speckit`), a `name`, and a `type` (one of `instructions`, `agent`, `prompt`, `skill`). When the filename has no scope segment, the asset SHALL be treated as shared (no owner restriction).

#### Scenario: Filename with explicit scope

- **WHEN** the parser processes `org.kb.company-policy.skill.md`
- **THEN** it SHALL resolve `scope=org`, `module=kb`, `name=company-policy`, `type=skill`

#### Scenario: Filename without scope segment

- **WHEN** the parser processes `kb.faq.skill.md`
- **THEN** it SHALL resolve `scope=null` (shared), `module=kb`, `name=faq`, `type=skill`

##### Example: scope parsing table

| Filename | scope | module | name | type |
| --- | --- | --- | --- | --- |
| `org.kb.company-policy.skill.md` | org | kb | company-policy | skill |
| `prj.code.setup.agent.md` | prj | code | setup | agent |
| `usr.docs.notes.instructions.md` | usr | docs | notes | instructions |
| `kb.faq.skill.md` | null (shared) | kb | faq | skill |

### Requirement: Non-standard asset filenames bypass scope parsing

The system SHALL fall back to the legacy filename-derived selector (no scope resolution) for asset files that do not match any known `type` suffix (for example `scripts` and `docs` module resources using template/extension-based naming).

#### Scenario: Script asset without a type suffix

- **WHEN** the parser processes `di-ioc-inventory-script.template.ps1`
- **THEN** it SHALL resolve using the legacy selector logic (`di-ioc-inventory-script`) with `scope=null` and no `type` value

### Requirement: Selector filter supports scope and module combinations

The system SHALL accept a selector filter in one of three forms: a bare module name (e.g. `kb`), a scope-qualified module (e.g. `org.kb`), or a bare scope (e.g. `org`, matching all modules under that scope).

#### Scenario: Filtering by scope-qualified module

- **WHEN** a caller filters assets using the selector `org.kb`
- **THEN** only assets parsed with `scope=org` and `module=kb` SHALL match

#### Scenario: Filtering by bare scope

- **WHEN** a caller filters assets using the selector `usr`
- **THEN** all assets parsed with `scope=usr`, regardless of module, SHALL match

### Requirement: No dedicated namespace dimension for assistant-generated content

The system SHALL NOT define an `auto.` (or equivalent) filename dimension in the template asset namespace. Assistant-generated, unconfirmed content produced at runtime in a target project is out of scope for this package's own asset classification and SHALL instead be governed by injected policy content (see `entry-policy-injection`), which directs that such content be classified under the `usr.` scope.

#### Scenario: Parser does not special-case an `auto` segment

- **WHEN** the parser encounters a filename containing an `auto` segment (e.g. `usr.auto.code.snippet.skill.md`)
- **THEN** it SHALL treat `auto` as an ordinary, unrecognized segment rather than resolving it as a first-class scope or dimension, and SHALL NOT alter its scope/module parsing behavior based on its presence
