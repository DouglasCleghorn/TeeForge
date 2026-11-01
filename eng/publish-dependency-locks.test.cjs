const assert = require('node:assert/strict');
const { test } = require('node:test');
const publish = require('./publish-dependency-locks.cjs');

function fixture() {
  const sha = 'a'.repeat(40);
  const path = 'src/TeeForge/packages.lock.json';
  const calls = [];
  const pr = {
    state: 'open', user: { login: 'dependabot[bot]' },
    head: { repo: { id: 1 }, sha, ref: 'dependabot/all-dependencies' },
    base: { repo: { id: 1 }, ref: 'main' },
  };
  const tree = { truncated: false, tree: [{ path, type: 'blob', mode: '100644' }] };
  const args = {
    context: {
      repo: { owner: 'owner', repo: 'repo' },
      payload: {
        repository: { id: 1, full_name: 'owner/repo', default_branch: 'main' },
        workflow_run: {
          event: 'pull_request', conclusion: 'failure', actor: { login: 'dependabot[bot]' },
          head_repository: { id: 1 }, head_branch: pr.head.ref, head_sha: sha,
        },
      },
    },
    core: { info() {} },
    payload: { headSha: sha, updates: [{ path, content: '{"version":2,"dependencies":{}}' }] },
    prNumber: 11,
    github: {
      rest: {
        pulls: { get: async () => ({ data: pr }) },
        git: {
          getCommit: async () => ({ data: { tree: { sha: 'tree' } } }),
          getTree: async () => ({ data: tree }),
        },
        actions: { createWorkflowDispatch: async input => { calls.push(['dispatch', input]); } },
      },
      graphql: async (query, input) => {
        calls.push(['commit', input]);
        return { createCommitOnBranch: { commit: { oid: 'b'.repeat(40) } } };
      },
    },
  };
  return { args, pr, tree, calls };
}

test('commits only lock data to the expected head, then dispatches real CI', async () => {
  const { args, calls } = fixture();
  await publish(args);
  assert.deepEqual(calls.map(call => call[0]), ['commit', 'dispatch']);
  const input = calls[0][1].input;
  assert.equal(input.expectedHeadOid, args.payload.headSha);
  assert.deepEqual(input.branch, {
    repositoryNameWithOwner: 'owner/repo', branchName: 'dependabot/all-dependencies',
  });
  assert.equal(input.fileChanges.additions.length, 1);
  assert.equal(Buffer.from(input.fileChanges.additions[0].contents, 'base64').toString(),
    args.payload.updates[0].content);
  assert.deepEqual(calls[1][1], {
    owner: 'owner', repo: 'repo', workflow_id: 'ci.yml', ref: 'dependabot/all-dependencies',
  });
});

test('never writes to forks, human PRs, closed PRs, or a different base or branch', async () => {
  const changes = [
    f => { f.args.context.payload.workflow_run.head_repository.id = 2; },
    f => { f.args.context.payload.workflow_run.actor.login = 'someone'; },
    f => { f.args.context.payload.workflow_run.event = 'workflow_dispatch'; },
    f => { f.args.context.payload.workflow_run.conclusion = 'success'; },
    f => { f.pr.user.login = 'someone'; },
    f => { f.pr.state = 'closed'; },
    f => { f.pr.head.repo.id = 2; },
    f => { f.pr.base.repo.id = 2; },
    f => { f.pr.base.ref = 'release'; },
    f => { f.pr.head.ref = 'other-branch'; },
  ];
  for (const change of changes) {
    const f = fixture();
    change(f);
    await assert.rejects(publish(f.args));
    assert.deepEqual(f.calls, []);
  }
});

test('ignores superseded heads and rejects artifacts generated for another commit', async () => {
  const stale = fixture();
  stale.pr.head.sha = 'c'.repeat(40);
  await publish(stale.args);
  assert.deepEqual(stale.calls, []);

  const mismatched = fixture();
  mismatched.args.payload.headSha = 'c'.repeat(40);
  await assert.rejects(publish(mismatched.args));
  assert.deepEqual(mismatched.calls, []);
});

test('rejects workflow edits, traversal, new files, symlinks, duplicates, and malformed lock data', async () => {
  const changes = [
    f => { f.args.payload.updates[0].path = '.github/workflows/ci.yml'; },
    f => { f.args.payload.updates[0].path = 'src/../packages.lock.json'; },
    f => { f.args.payload.updates[0].path = 'src/New/packages.lock.json'; },
    f => { f.tree.tree[0].mode = '120000'; },
    f => { f.tree.truncated = true; },
    f => { f.args.payload.updates.push(f.args.payload.updates[0]); },
    f => { f.args.payload.updates[0].content = 'not json'; },
    f => { f.args.payload.updates[0].content = '{"version":2,"dependencies":[]}'; },
    f => { f.args.payload.updates[0].content = '{"version":1,"dependencies":{}}'; },
    f => { f.args.payload.updates = []; },
  ];
  for (const change of changes) {
    const f = fixture();
    change(f);
    await assert.rejects(publish(f.args));
    assert.deepEqual(f.calls, []);
  }
});

test('a concurrent update rejected by GitHub never dispatches CI for a stale write', async () => {
  const { args, calls } = fixture();
  args.github.graphql = async () => { throw new Error('Expected head does not match'); };
  await assert.rejects(publish(args), /Expected head/);
  assert.deepEqual(calls, []);
});
