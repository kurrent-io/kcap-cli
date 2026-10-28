---
name: worktrees
description: >-
  This skill should be used when git worktrees break across a sandbox or
  container and its host: you are running in a sandbox, container or VM that
  mounts the repository from the host; git says `.git file does not reference
  a repository`; a worktree is missing or `git worktree list` marks it
  prunable; the host (desktop app, IDE, the user's own git) cannot open a
  worktree an agent made; or the SessionStart context asks about relative
  worktree links. Covers enabling and undoing relative worktree links, why
  never to prune, and how to reinstate a pruned worktree.
---

# Worktrees across a sandbox and its host

Unless configured otherwise, git links a worktree to its repository by
**absolute path**, in two files:

- `<worktree>/.git` holds `gitdir: /abs/path/.git/worktrees/<name>`
- `.git/worktrees/<name>/gitdir` holds `/abs/path/<worktree>/.git`

A sandbox (container or VM) that mounts the host's filesystem usually sees
the repository at a different path from the host. Each side's absolute links
resolve only on that side, so a worktree made in the sandbox can't be opened
on the host and the other way round. Git on the other side also marks it
prunable, and `git gc` eventually prunes its metadata
(`gc.worktreePruneExpire`, 3 months by default)

## Relative worktree links

Git 2.48 added relative links. Relative links only help when the worktree
and the repository's `.git` sit inside the same mounted tree, which is true
for worktrees under `.capacitor/worktrees/` and `.claude/worktrees/`

**Before enabling, tell the user the cost:** the first relative write sets
`extensions.relativeWorktrees` and raises `core.repositoryformatversion` from
0 to 1. Git older than 2.48 then refuses to open the repository at all, the
main checkout included. That covers the host's git, IDEs (JetBrains uses
JGit), libgit2-based tools and git bundled with GUI clients. Check
`git --version` on both sides

**Enable** for this repository (run on either side):

```
git config worktree.useRelativePaths true
git worktree repair --relative-paths <worktree>...
```

The first line makes every later `worktree add`, `move` and `repair` write
relative links, whoever runs them. The second converts the worktrees that
already exist. Pass every worktree's directory as it appears from where you
run it, for example everything under `.capacitor/worktrees/`: without paths,
repair leaves resolving links alone and cannot find a worktree the other side
made, and `git worktree list` shows that worktree at the other side's path.
The setting lives in `.git/config`, so it never reaches a clone.
`git config --global worktree.useRelativePaths true` turns it on for every
repository on that machine instead

**Decline**, so kcap's SessionStart context stops asking in this repository:

```
git config kcap.relativeWorktrees declined
```

`git config --unset kcap.relativeWorktrees` asks again. To silence the
question for every repository, `kcap config set disable_relative_worktrees_nudge true`

**Undo.** Run it **on the host**: run in the sandbox, it writes sandbox paths
back in

1. `git config worktree.useRelativePaths false`, which also overrides a
   global `true`
2. `git worktree repair --no-relative-paths <worktree>...`, passing every
   worktree's directory: without paths it leaves resolving relative links
   as they are
3. `git config --unset extensions.relativeWorktrees`, and set
   `core.repositoryformatversion` back to 0 if no other `extensions.*` is set

## Never prune

Do not run `git worktree prune`, or anything that prunes (`git gc` included),
while the links are absolute and the repository is opened from more than one
place. From the sandbox every worktree the host made looks dangling, and the
other way round, so a prune deletes metadata for worktrees that are fine

A prune removes only `.git/worktrees/<name>/`. What survives: the whole
working tree (tracked files with their edits, untracked and ignored files),
the branch and its reflog. What is lost:

- `HEAD`, which branch was checked out. kcap worktrees always use
  `capacitor/<folder name>`; a detached worktree's commit is genuinely lost
- The index: everything comes back unstaged and newly added files as
  untracked. Partial staging (`git add -p`) is the only real loss
- The worktree's own HEAD reflog, `refs/bisect`, `refs/worktree`,
  `config.worktree` and the lock flag

## Reinstating a pruned worktree

`git worktree repair` can't do it: it fails with
`error: unable to locate repository; .git file does not reference a repository`.
For a worktree at `wt/x` whose branch `capacitor/x` still exists, from the
main checkout:

```
mv wt/x wt/x.old
git worktree add --no-checkout wt/x capacitor/x   # recreates metadata, touches no files
mv wt/x/.git wt/x.old/.git
rmdir wt/x && mv wt/x.old wt/x
git -C wt/x reset                                 # rebuilds the index from HEAD, files untouched
```

Confirm the branch with the user first when the folder name and branch might
not match, and run it on the side that will use the worktree, or enable
relative links first so both sides can open it
