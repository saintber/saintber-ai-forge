## Purpose

This capability defines how the Assistant is built, run, tested, and verified as a container using either Docker or Podman, including isolation of secrets from the build, the documentation a maintainer needs to install a container engine and perform manual LINE tests, and the CI that continuously builds and tests it.

## ADDED Requirements

### Requirement: Engine-neutral container build

The project SHALL provide a single `Containerfile` under `assistant/` that builds the host and the LINE connector with a multi-stage build and produces an image that places the host in `/app`, the LINE connector assembly and its dependencies under `/app/connectors/line/`, runs as a non-root user, and listens on container port 8080. The image build MUST succeed with both Docker and Podman using the build context `assistant/` and without engine-specific flags, and the image MUST NOT contain channel secrets or access tokens.

#### Scenario: Build with Podman

- **WHEN** `podman build -f assistant/Containerfile -t assistant-host:local assistant/` is run from the repository root
- **THEN** the build succeeds

#### Scenario: Build with Docker

- **WHEN** `docker build -f assistant/Containerfile -t assistant-host:local assistant/` is run from the repository root
- **THEN** the build succeeds

#### Scenario: Runs as non-root

- **WHEN** the built image is started and its process user is inspected
- **THEN** the process user id is not 0

#### Scenario: LINE connector is bundled

- **WHEN** the image file system is inspected
- **THEN** the LINE connector assembly exists under `/app/connectors/line/`

### Requirement: Build context isolation

The build context SHALL be the `assistant/` directory, and `assistant/.dockerignore` SHALL exclude build outputs and any file matching `.env*` or `*.env`. The repository-root `data/` directory MUST be outside the build context. No file from `data/` or matching the excluded patterns SHALL appear in any image layer, build cache, or the final image, with either engine.

#### Scenario: Sentinel outside the context

- **WHEN** a file `data/assistant.env` containing a unique sentinel string exists and the image is built with either engine
- **THEN** the sentinel appears in no layer and not in the final image

#### Scenario: Sentinel inside the context but ignored

- **WHEN** a file `assistant/leak.env` containing a unique sentinel string exists and the image is built with either engine
- **THEN** the sentinel appears in no layer and not in the final image

##### Example: sentinel check

- **GIVEN** the sentinel `SENTINEL-7F3A-NOT-A-SECRET` written to both files above
- **WHEN** every layer of the built image is exported and searched for the sentinel
- **THEN** the search finds zero matches

### Requirement: Compose definition

The project SHALL provide `assistant/compose.yaml` that builds from context `assistant/` with `Containerfile`, reads configuration from the repository-root `data/assistant.env`, and publishes the container port to a host port. The compose file MUST use only fields supported by both Docker Compose and Podman compose, MUST NOT mount the container engine socket, and MUST NOT mount the user's home directory.

#### Scenario: Start with either engine

- **WHEN** `docker compose -f assistant/compose.yaml up --build` or `podman compose -f assistant/compose.yaml up --build` is run with a valid `data/assistant.env`
- **THEN** the host starts and `GET /healthz` on the published port returns 200

#### Scenario: Compose file rules

- **WHEN** the compose file is inspected
- **THEN** it declares no volume that references an engine socket path or a home directory

### Requirement: Secret files are not committed

The project SHALL exclude the repository-root `data/` directory from version control and SHALL provide `assistant/.env.example` containing every required setting name, including the `Connectors__0__...` form for the LINE connector, with placeholder values only.

#### Scenario: Data directory ignored

- **WHEN** a file is created at `data/assistant.env` and `git status` is run
- **THEN** the file is not listed as untracked

#### Scenario: Example file has only placeholders

- **WHEN** `assistant/.env.example` is inspected
- **THEN** it lists `Connectors__0__Settings__ChannelSecret` and `Connectors__0__Settings__ChannelAccessToken` with placeholder values that are not real credentials

### Requirement: Installation and run documentation

The project SHALL provide documentation under `assistant/docs/` covering: installing and verifying Docker and Podman (including the Windows behavior where `podman compose` delegates to an external compose provider and the alternative of running without compose), local run without containers, the architecture with module responsibilities, dependency direction, the connector load unit (published folder with assembly, `.deps.json`, and dependencies) and contract version compatibility, and how a new connector is added, and the LINE manual test procedure. Each document MUST state its prerequisites and the expected result of every verification step.

#### Scenario: Container install guide

- **WHEN** a maintainer follows `assistant/docs/install-container.md` for either engine
- **THEN** the guide lists the install steps, a version check command with its expected output shape, and a hello-run check

#### Scenario: Manual LINE test guide

- **WHEN** a maintainer follows `assistant/docs/manual-test-line.md`
- **THEN** the guide covers creating the LINE channel, exposing the local port through a tunnel, setting the webhook URL to `/webhook/<instance id>`, sending a one-to-one and a group message, the expected echo replies and loading behavior, how to verify the real push path by setting `Assistant:Echo:Mode=push`, how to confirm in the LINE console that webhook error statistics show no `request_timeout`, a statement that the local reply validity is an estimate and that a platform rejection of a reply token is logged without a push fallback, and troubleshooting for signature failures and missing replies

#### Scenario: Architecture guide

- **WHEN** a maintainer opens `assistant/docs/architecture.md`
- **THEN** it states each project's responsibility, what it must not do, the allowed dependency direction, the load unit, and the steps to add a connector assembly

### Requirement: Offline fake webhook tool

The project SHALL provide a script under `assistant/scripts/` that builds a LINE-style webhook payload, signs it with a supplied channel secret, and posts it to a supplied URL so the host can be exercised without a real LINE account.

#### Scenario: Signed fake webhook accepted

- **WHEN** the script is run with the same channel secret the host's LINE connector is configured with
- **THEN** the host responds 200

#### Scenario: Wrong secret rejected

- **WHEN** the script is run with a different channel secret
- **THEN** the host responds 401

### Requirement: Continuous integration

The project SHALL provide a GitHub Actions workflow that, for changes under `assistant/`, restores, builds, and runs all tests with .NET 10, and then builds the container image with both Docker and Podman using context `assistant/` without pushing it. The workflow SHALL fail when any of these steps fails.

#### Scenario: Pull request with a failing test

- **WHEN** a pull request changes files under `assistant/` and one test fails
- **THEN** the workflow reports failure and the image build jobs do not run

#### Scenario: Pull request with passing tests

- **WHEN** a pull request changes files under `assistant/` and all tests pass
- **THEN** the workflow builds the image once with Docker and once with Podman, and neither job pushes an image

#### Scenario: Unrelated change

- **WHEN** a pull request changes only files outside `assistant/` and the workflow file
- **THEN** the workflow does not run

### Requirement: Independence from the saifg package

The `assistant/` directory SHALL be independent of the `@saintber/saifg` npm package: the package `files` list MUST NOT include `assistant/`, and neither product SHALL reference the other's source at build time.

#### Scenario: Package contents

- **WHEN** `npm pack --dry-run` is run at the repository root
- **THEN** no file under `assistant/` appears in the package contents
