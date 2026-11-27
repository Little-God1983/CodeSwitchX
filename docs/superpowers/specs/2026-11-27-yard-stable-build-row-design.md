# Yard tile: stable build row (version + up to date / outdated)

Short design, 2026-11-27.

## Intent

The user builds several of their apps through the Yard tile menu (Build new version / Run, #423). They want to see on the tile,
without opening the menu, which version is installed as the stable build and whether that build is behind the code, so they know
when a new build is due.

## What the user decided

- The tile shows a **separator line under the chats**, and under it a row with the **installed version** and a **pill**: "up to date"
  or "outdated".
- **Outdated** means the repository's **default branch** has commits the build was not made from. Commits on feature, bugfix or any
  other branch, and open pull requests, do not count. For DiffusionNexus the default branch is `develop`; for every other repo it is
  `main`.

## Approach

One sensible way, no alternatives worth weighing: read what the build script already records, and ask git.

- Every stable build writes a marker into its install folder (`build.ps1`, `Write-InstallMarker`) with `version`, `complete` and
  `commit` (the short hash it was built from). The folder is `<$script:DefaultInstallRoot>\<$script:AppName>`, a junction to the newest
  version; the marker's file name is `$script:InstallMarkerName`. All three are plain quoted settings in each repo's
  `scripts\_common.ps1`, which `StableBuildRepo.Find` already parses (it reads `ShortcutName` and `ReleaseBranch` today). The marker
  name must be read, not derived: one repo's marker still carries the app's former name.
- The build script commits `chore: bump version to <v>` (only `Directory.Build.props`) right after the build, so that commit always
  follows the marker's commit and must not count as "newer".

Ruled out: comparing against the checked-out branch (a feature branch would always look outdated); reading the version from the
exe's file version (the marker already has version and commit together); building something into the build scripts (they already
write everything needed).

## Behaviour (Claude's proposals, approved with this doc)

- **Up to date** (green, the clean pill's colours): the default branch on GitHub has no commit after the marker's commit other than
  bump commits. **Outdated** (amber): it has at least one. Its tooltip says how many: "3 commits on main since 0.1.0.45".
- **Default branch and its newest commit**: GitHub's `DefaultBranch` / `DefaultHead`, which the Yard already reads each round
  (`RepoActivity`, used by the behind pill, #426). The commit count comes from local git (`<marker commit>..<head>`), skipping
  commits whose subject is `chore: bump version to …` and that touch only `Directory.Build.props`.
- **Not known** (GitHub not read yet, offline, the marker's commit or GitHub's head not in the local clone): the row shows the version
  without a pill rather than guessing, the way the git pill shows nothing when git cannot tell.
- **Scripts but no build yet** (no marker, or `complete: false`): grey pill "no stable build", no version.
- **No build scripts**: no separator and no row.
- **Several repos with scripts on one tile**: one row each, named after the folder like the menu entries
  (`StableBuildEntry.Name`).
- **Not clickable**: a click on the row opens the window like the rest of the tile; building stays in the right-click menu.
- **Freshness**: computed in the tile's existing git round, so it follows the Yard's refresh and the GitHub reads; a new stable build
  shows on the next round.

## Out of scope

- Detecting whether the stable build is running.
- One-off builds (`-OneOff`, their own folder): the row only reads the junction's marker.
- Changing the build scripts or the marker format.
- A Build button on the row.
