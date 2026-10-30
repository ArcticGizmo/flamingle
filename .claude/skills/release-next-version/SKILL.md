---
name: release-next-version
description: Ship the current branch as the next Flamingle release. Runs the tests locally, opens (or reuses) a PR for the current branch, waits for any PR checks, stops and reports a FAIL if the tests or a check fail, otherwise squash-merges it, then tags the new main head with the csproj version (vX.Y.Z) and pushes the tag, which kicks off the Release workflow — and watches that workflow until the GitHub Release is published or the build fails. Use when the user says "release next version", "release this", "/release-next-version", or wants to merge the current branch and cut the tagged release.
---

# Release Next Version

Takes the current branch from "done" to "published release": local tests → PR → squash-merge → tag the new
`main` head → push the tag → watch the Release workflow to completion. Pushing a `v*` tag triggers
`.github/workflows/release.yml`, which builds and publishes the GitHub Release. (Ported from Perch.)

Invoking this skill is the user's go-ahead for the outward-facing steps (pushing the branch, opening the
PR, merging, pushing the tag). Anything *not* listed here (force-pushes, bypassing checks, deleting
branches, re-tagging) is out of scope — stop and ask.

## Repo facts this relies on

- Repo is `ArcticGizmo/flamingle` (the local `origin` may still say `gaggle` — GitHub redirects it); **squash-merge
  only** (merge commits and rebase are disabled). The remote branch is **not** auto-deleted on merge.
- Tags are **lightweight**, `vMAJOR.MINOR.PATCH`, and must match `<Version>` in
  `src/Flamingle.App/Flamingle.App.csproj` (the release build takes its version from the tag).
- `CHANGELOG.md` must have a `## [v$VERSION]` section — the Release workflow fails its first step otherwise
  (the app's "what's new" window reads it). `/bump-version` writes both the csproj and that section.
- There is **no PR CI workflow** — a PR normally has **no checks**, so the local `dotnet test` in step 2 is the
  gate before merging. (The Release workflow runs the tests again before it builds.) If PR checks do show up
  (CI added later, or Dependabot), they're waited on too.
- The Release workflow (`release.yml`) has two jobs: `build-windows`, then `release` (`needs` it), which is the
  **only** job that creates the GitHub Release. A failed build means no Release is published at all — never a
  half-populated one. A tag-triggered run's `headBranch` is the tag name (e.g. `v0.2.2`). A healthy run takes
  roughly 2–4 minutes.
- Use the Bash tool (Git Bash) for the snippets below.

## Steps

### 1. Preflight — gather everything in one batch

```bash
BRANCH=$(git rev-parse --abbrev-ref HEAD); echo "BRANCH=$BRANCH"
echo "=== status ==="; git status --porcelain
git fetch origin --tags --quiet
VERSION=$(grep -o '<Version>[^<]*</Version>' src/Flamingle.App/Flamingle.App.csproj | head -1 | sed 's/<[^>]*>//g'); echo "VERSION=$VERSION"
echo "LAST_TAG=$(git tag --sort=-v:refname | head -1)"
echo "TAG_EXISTS_LOCAL=$(git tag -l "v$VERSION")"
echo "TAG_EXISTS_REMOTE=$(git ls-remote --tags origin "refs/tags/v$VERSION")"
echo "CHANGELOG_SECTION=$(grep -cF "## [v$VERSION]" CHANGELOG.md)"
echo "=== commits on $BRANCH not on origin/main ==="
git log origin/main..HEAD --no-merges --pretty=format:'%h %s'
echo; echo "=== existing PR ==="; gh pr view --json number,state,url,title 2>&1
```

Stop and report (don't try to fix it yourself) if any of these hold:

- `BRANCH` is `main` (or detached `HEAD`) — there's nothing to PR. Say so.
- The working tree is dirty — uncommitted changes wouldn't be in the release. List them and ask
  whether to commit them first.
- There are no commits on the branch ahead of `origin/main`.
- `v$VERSION` already exists (locally or on the remote), `VERSION` isn't greater than `LAST_TAG`, or
  `CHANGELOG_SECTION` is `0` — the version hasn't been bumped. Suggest running `/bump-version` (and committing
  its csproj + `CHANGELOG.md` edits) first. Never move or overwrite an existing tag.
- An existing PR for the branch is `CLOSED` or `MERGED` — ask rather than open a second one.

### 2. Run the tests locally

With no PR CI, this is what stands between the branch and `main`:

```bash
dotnet test tests/Flamingle.Tests 2>&1 | tail -15
echo "RESULT=EXIT_${PIPESTATUS[0]}"
```

Anything but `EXIT_0` is a FAIL: **stop**, push nothing, open no PR. Report it headed
**`RELEASE FAILED — tests did not pass`** with the failing test names / compiler errors. Don't attempt a fix
unless the user asks.

### 3. Push the branch and open (or reuse) the PR

```bash
git push -u origin HEAD
```

If step 1 found an `OPEN` PR, reuse it. Otherwise create one against `main`:

- **Title:** a concise summary of the branch's net change (if the branch is a single feature, its name;
  otherwise the headline items), ending with `; bump to $VERSION` when the branch carries the bump —
  e.g. `Layouts per virtual desktop; bump to 0.2.2`.
- **Body:** a short `## Summary` of the user-facing changes (from the commit list in step 1 and the new
  `## [v$VERSION]` section in `CHANGELOG.md`), a `## Test plan` noting the local test run (pass count) and
  anything still unverified live, then the PR attribution line.

Write the body to a temp file and pass it with `--body-file` (avoids quoting trouble):

```bash
gh pr create --base main --head "$BRANCH" --title "<title>" --body-file <file>
```

Record the PR number and URL.

### 4. Wait for any checks

Checks can take a few seconds to register after the PR opens, hence the short lead-in loop. Normally none
appear (no PR CI) and this returns `NO_CHECKS` after about a minute; run it in the foreground.

```bash
PR=<number>
found=0
for i in $(seq 1 6); do
  if gh pr checks "$PR" 2>&1 | grep -q 'no checks reported'; then sleep 10; else found=1; break; fi
done
if [ "$found" = 0 ]; then echo "RESULT=NO_CHECKS"; exit 0; fi
gh pr checks "$PR" --watch --fail-fast --interval 30
echo "RESULT=EXIT_$?"
```

If checks were found and are still running when the foreground call would time out, re-run just the
`--watch` line with the Bash tool's `run_in_background: true` (timeout `2700000` ms) and wait for the
completion notification — don't poll.

Then branch on the result:

- **`NO_CHECKS`** — expected (there's no PR CI); step 2's local run is the gate. Continue to step 5.
- **`EXIT_0`** — all checks passed. Continue to step 5.
- **Any other exit** — a check failed (or was cancelled). **Stop: this is a FAIL that needs fixing.**
  Do not merge, do not tag. Gather the evidence:

  ```bash
  gh pr checks "$PR"
  gh run list --branch "$BRANCH" --limit 1 --json databaseId,url,conclusion,workflowName
  gh run view <databaseId> --log-failed | tail -80
  ```

  Report back headed **`RELEASE FAILED — checks did not pass`**, with the PR URL, which job/step failed,
  the run URL, and the relevant tail of the failed log. Leave the PR open. Don't attempt a fix unless the
  user asks.

### 5. Squash-merge

```bash
gh pr merge "$PR" --squash
```

Don't pass `--delete-branch` — deleting branches is the user's call (and after a squash the local branch can
only be force-deleted). Don't pass `--admin` or `--auto`. If the merge is refused (required review, branch
protection, merge conflict), stop and report the message.

Then confirm the merge and capture the squash commit:

```bash
gh pr view "$PR" --json state,mergeCommit -q '.state + " " + .mergeCommit.oid'
git fetch origin --tags --quiet
git checkout main && git pull --ff-only origin main
echo "MAIN_HEAD=$(git rev-parse HEAD)"
```

`state` must be `MERGED`. Normally `MAIN_HEAD` equals the merge commit. If it doesn't, something else
landed on `main` in the meantime — stop and ask which commit to tag rather than guessing.

If `git pull --ff-only` fails (local `main` had diverged), stop and report — don't reset it.

### 6. Tag the new main head and push the tag

```bash
git tag "v$VERSION" "$MAIN_HEAD"
git push origin "v$VERSION"
```

(Lightweight tag, matching the existing ones.) Then find the Release run the tag triggered, filtering on
the tag so a stale or concurrent run can't be picked up. It can take a few seconds to register:

```bash
RUN=""
for i in $(seq 1 24); do
  RUN=$(gh run list --workflow release.yml --branch "v$VERSION" --event push --limit 1 --json databaseId -q '.[0].databaseId')
  [ -n "$RUN" ] && break
  sleep 5
done
echo "RUN=$RUN"
[ -n "$RUN" ] && gh run view "$RUN" --json url -q .url
```

If `RUN` is still empty after ~2 minutes, stop and report: the tag is pushed but no Release run started
(check the Actions tab — the workflow may be disabled). Don't re-push or re-tag.

### 7. Watch the Release workflow

A healthy run takes 2–4 minutes, so run the watch with the Bash tool's `run_in_background: true` and a
timeout of 20 minutes (`1200000` ms), then wait for the completion notification — don't poll. The watch
output is redirected because `gh run watch` redraws the whole job table every interval; the summary after
it is what you read:

```bash
RUN=<id>
gh run watch "$RUN" --exit-status --interval 15 > /dev/null 2>&1
echo "RESULT=EXIT_$?"
gh run view "$RUN" --json status,conclusion,url,jobs \
  -q '"run: " + .status + " " + (.conclusion // "-") + "  " + .url, (.jobs[] | "  " + .name + ": " + .status + " " + (.conclusion // "-"))'
```

Then branch on the result:

- **`EXIT_0`** — the run succeeded. Confirm the Release really exists with the assets the install path
  depends on:

  ```bash
  gh release view "v$VERSION" --json url,isDraft,assets \
    -q '.url, ("draft: " + (.isDraft|tostring)), (.assets[] | "  " + .name)'
  ```

  `Flamingle-win-Setup.exe` and `SHA256SUMS.txt` must both be listed (`install.ps1` resolves the first and
  verifies it against the second; the in-app update check reads the Release too). If either is missing, or
  the Release is a draft, report it as a problem — the run went green but the installer can't use this release.
- **Any other exit** — a job failed or was cancelled. **Stop: this is a RELEASE BUILD FAILURE.** The tag is
  pushed but, because `release` needs the build, **no GitHub Release was published**. Gather the evidence:

  ```bash
  gh run view "$RUN" --log-failed | tail -80
  ```

  Report back headed **`RELEASE BUILD FAILED — v$VERSION tagged, no Release published`**, with the run URL,
  which job/step failed, and the relevant tail of the failed log. (A failure in "Check csproj and changelog
  match the tag" means the bump wasn't committed as it should have been.) Don't re-run, re-tag, delete the tag
  or push a fix on your own — say that the usual recovery once the cause is understood is
  `gh run rerun "$RUN" --failed` (a flaky runner) or a fix plus a new patch version (a real break), and let
  the user choose.
- **The background task timed out** (no `RESULT` line after 20 minutes) — the run is still going or stuck.
  Report the run URL and the current job statuses (the `gh run view` line above), and don't wait further
  unless asked.

### 8. Report back

Print, plainly:

- **`Released v$VERSION`**
- The local test result, the PR URL (merged) and the squash commit SHA that was tagged.
- The Release workflow run URL and its outcome (both jobs green).
- The GitHub Release URL and confirmation that `Flamingle-win-Setup.exe` and `SHA256SUMS.txt` are attached.
- That local `main` is checked out and up to date, and that the branch `$BRANCH` still exists locally and on
  the remote (delete them if you like — nothing here does).

On any stop in steps 1–7, report exactly which step stopped, why, and what state things were left in
(e.g. "tests failed, nothing pushed", "PR #3 open, not merged, no tag", or "PR #3 merged, v0.2.2 tagged,
Release build failed on build-windows — no Release published").
