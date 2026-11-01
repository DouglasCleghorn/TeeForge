// Runs only from the default branch in the privileged publishing job.
module.exports = async function publishDependencyLocks({ github, context, core, payload, prNumber }) {
  const repository = context.payload.repository;
  const run = context.payload.workflow_run;
  if (run.event !== 'pull_request' || run.conclusion !== 'failure' ||
      run.actor.login !== 'dependabot[bot]' || run.head_repository?.id !== repository.id ||
      !Number.isSafeInteger(prNumber) || prNumber < 1) {
    throw new Error('Expected a failed same-repository Dependabot PR run.');
  }

  const { data: pr } = await github.rest.pulls.get({ ...context.repo, pull_number: prNumber });
  if (pr.state !== 'open' || pr.user.login !== 'dependabot[bot]' ||
      pr.head.repo?.id !== repository.id || pr.base.repo.id !== repository.id ||
      pr.base.ref !== repository.default_branch || pr.head.ref !== run.head_branch) {
    throw new Error('Refusing to write outside an open same-repository Dependabot PR.');
  }
  if (pr.head.sha !== run.head_sha) {
    core.info('PR head changed while locks were generated; leaving the newer commit alone.');
    return;
  }
  if (payload.headSha !== run.head_sha || !Array.isArray(payload.updates) ||
      payload.updates.length === 0 || payload.updates.length > 100) {
    throw new Error('Invalid lock-file artifact or mismatched head SHA.');
  }

  const { data: commit } = await github.rest.git.getCommit({
    ...context.repo, commit_sha: pr.head.sha,
  });
  const { data: tree } = await github.rest.git.getTree({
    ...context.repo, tree_sha: commit.tree.sha, recursive: 'true',
  });
  if (tree.truncated) throw new Error('Cannot validate a truncated repository tree.');
  const allowedPaths = new Set(tree.tree
    .filter(entry => entry.type === 'blob' && entry.mode === '100644' &&
      /^(src|tests|tools|benchmarks|samples)\/[A-Za-z0-9_.-]+\/packages\.lock\.json$/.test(entry.path))
    .map(entry => entry.path));
  const seen = new Set();
  const additions = payload.updates.map(update => {
    if (!allowedPaths.has(update.path) || seen.has(update.path) ||
        typeof update.content !== 'string' || Buffer.byteLength(update.content) > 2_000_000) {
      throw new Error('Artifact may only update existing regular NuGet lock files once.');
    }
    seen.add(update.path);
    const lock = JSON.parse(update.content);
    if (lock?.version !== 2 || !lock.dependencies || typeof lock.dependencies !== 'object' ||
        Array.isArray(lock.dependencies)) {
      throw new Error(`Invalid NuGet lock file: ${update.path}`);
    }
    return { path: update.path, contents: Buffer.from(update.content).toString('base64') };
  });

  // The atomic expected-head check prevents overwriting concurrent bot or human
  // changes. GitHub signs this commit to satisfy main's signature requirement.
  const result = await github.graphql(`
    mutation($input: CreateCommitOnBranchInput!) {
      createCommitOnBranch(input: $input) { commit { oid } }
    }`, {
    input: {
      branch: { repositoryNameWithOwner: repository.full_name, branchName: pr.head.ref },
      expectedHeadOid: run.head_sha,
      message: { headline: 'Synchronize dependency lock files across the full solution' },
      fileChanges: { additions },
    },
  });
  core.info(`Updated lock files in ${result.createCommitOnBranch.commit.oid}.`);

  // Commits made with GITHUB_TOKEN do not automatically run normal push CI.
  // Dispatch explicitly so required checks validate the repaired head.
  await github.rest.actions.createWorkflowDispatch({
    ...context.repo, workflow_id: 'ci.yml', ref: pr.head.ref,
  });
};
