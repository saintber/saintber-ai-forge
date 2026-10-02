import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { matchesGlob } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { parseDocument } from 'yaml';

const workflowPath = fileURLToPath(new URL('../../../.github/workflows/assistant-ci.yml', import.meta.url));
const expectedPaths = ['assistant/**', '.github/workflows/assistant-ci.yml'];

function workflow() {
  assert.ok(existsSync(workflowPath), 'assistant CI workflow must exist');
  const document = parseDocument(readFileSync(workflowPath, 'utf8'), { uniqueKeys: true });
  assert.deepEqual(document.errors, [], 'workflow must be valid YAML with unique keys');
  return document.toJS();
}

function runSteps(job) {
  return job.steps.filter(step => 'run' in step);
}

function assertFailsNormally(job) {
  assert.ok(!job['continue-on-error'], 'job failure must fail the workflow');
  assert.equal(job.if, undefined, 'default success condition must govern the job');
  for (const step of job.steps) {
    assert.ok(!step['continue-on-error'], 'step failures must not be ignored');
    assert.equal(step.if, undefined, 'steps must not bypass the success condition');
    if (step.run) assert.doesNotMatch(step.run, /\|\|\s*true|continue-on-error|set\s+\+e/);
  }
}

test('only assistant and workflow changes trigger branch pushes or pull requests', () => {
  const actual = workflow();
  assert.deepEqual(Object.keys(actual.on).sort(), ['pull_request', 'push']);
  assert.deepEqual(actual.on.push.branches, ['**'], 'tag pushes must not bypass path filters');
  for (const event of ['push', 'pull_request']) {
    const trigger = actual.on[event];
    assert.deepEqual(trigger.paths, expectedPaths);
    assert.equal(trigger['paths-ignore'], undefined);
    for (const changedPath of ['assistant/src/Host.cs', 'assistant/scripts/fake-line-api.cs', '.github/workflows/assistant-ci.yml']) {
      assert.ok(trigger.paths.some(pattern => matchesGlob(changedPath, pattern)), changedPath);
    }
    for (const changedPath of ['src/cli.ts', 'package.json', 'README.md', '.github/workflows/unrelated.yml', 'assistant-other/file.cs']) {
      assert.ok(!trigger.paths.some(pattern => matchesGlob(changedPath, pattern)), changedPath);
    }
  }
});

test('failed restore build or test blocks all image jobs using the normal success dependency', () => {
  const actual = workflow();
  assert.deepEqual(Object.keys(actual.jobs).sort(), ['images', 'test']);
  const validation = actual.jobs.test;
  const images = actual.jobs.images;
  assert.equal(validation['runs-on'], 'ubuntu-24.04');
  assert.equal(validation.defaults.run['working-directory'], 'assistant');
  const setup = validation.steps.find(step => step.uses?.startsWith('actions/setup-dotnet@'));
  assert.ok(setup, '.NET SDK setup must run before restore');
  assert.equal(setup.with['dotnet-version'], '10.0.x');
  assert.equal(setup.with['dotnet-quality'], 'ga');
  assert.deepEqual(runSteps(validation).map(step => step.run), [
    'dotnet restore Assistant.sln',
    'dotnet build Assistant.sln --configuration Release --no-restore',
    'dotnet test Assistant.sln --configuration Release --no-build --no-restore',
    'npm ci --prefix tests/Assistant.Ci.Tests',
    'npm test --prefix tests/Assistant.Ci.Tests',
    'dotnet run --file scripts/fake-webhook-tests.cs -- scripts/fake-webhook.cs',
    'dotnet run --file scripts/fake-line-api-tests.cs -- scripts/fake-line-api.cs'
  ]);
  assert.deepEqual(images.needs, ['test']);
  assert.equal(validation.needs, undefined);
  assertFailsNormally(validation);
  assertFailsNormally(images);
});

test('passing validation builds once per Docker and Podman without publishing images', () => {
  const actual = workflow();
  const images = actual.jobs.images;
  assert.equal(images['runs-on'], 'ubuntu-24.04');
  assert.deepEqual(images.strategy.matrix, { engine: ['docker', 'podman'] });
  assert.equal(images.strategy['fail-fast'], false, 'both engine outcomes must remain observable');
  assert.deepEqual(images.steps.filter(step => step.uses).map(step => step.uses.split('@')[0]), ['actions/checkout']);
  const builds = runSteps(images);
  assert.equal(builds.length, 1, 'each matrix leg builds once');
  assert.equal(builds[0].env.CONTAINER_ENGINE, '$' + '{{ matrix.engine }}');
  assert.equal(builds[0].run, '"$CONTAINER_ENGINE" build --file assistant/Containerfile --tag assistant-host:ci assistant/');
  for (const job of Object.values(actual.jobs)) {
    for (const step of runSteps(job)) assert.doesNotMatch(step.run, /\b(push|login)\b|--push/);
  }
  assert.deepEqual(actual.permissions, { contents: 'read' });
});
