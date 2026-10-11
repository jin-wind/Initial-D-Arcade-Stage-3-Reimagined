# Unsigned iPhoneOS EOS build on GitHub Actions

Workflow: [`.github/workflows/eos-ios-build.yml`](../.github/workflows/eos-ios-build.yml).
This is a real Unity/IL2CPP, native archive and Xcode device build. It extends the
[opt-in EOS integration](ios-eos-integration.md). The existing Android workflow
also supports an opt-in development `main` push; Simulator and ordinary iOS
workflows remain separate.

## Scope and prerequisites

- Build one ARM64 **iPhoneOS** app, with EOS SDK **1.19.2.1**, from the exact
  workflow source commit. The full game data is included; a player's ROM is not.
- No EOS product/client credentials are read or packaged. A preflight rejects
  an existing `Idas3EOS.json`. Consequently this artifact cannot sign in to EOS
  or exercise Internet crossplay. It is intended to establish the actual mobile
  compilation, linking and packaging result first.
- No Apple login, certificates, provisioning profiles, device installation,
  TestFlight or store upload occurs. An unsigned IPA is not a directly
  installable iPhone app. Signing and service configuration remain separate work.
- Actions must be enabled, the selected actions permitted, and the repository's
  existing Unity activation secrets available to this trusted branch. The only
  user-configured secret names referenced are `UNITY_LICENSE`, `UNITY_EMAIL`
  and `UNITY_PASSWORD`, matching `mobile-build.yml` and `android-apk.yml`.
  The Ubuntu gate checks presence only and prints missing names, never values.
  Presence does not prove entitlement, valid credentials or activation success.

### Unity licensing and downloads

The pinned Editor is **6000.6.4f1** (revision **12bfff696524**), with matching
macOS ARM64 Editor and iOS Build Support. Both are listed on the
[official Unity release page](https://unity.com/releases/editor/whats-new/6000.6.4f1).
`game-ci/unity-builder@v5.0.1` installs the required Editor/module on the hosted
Mac and uses the established activation setup. The license is still required
even though no Apple signing is performed.

GameCI documents the existing three-secret setup for a previously activated
Personal license. Pro/other license arrangements may require a serial or license
server instead; this workflow does not create or assume either. If activation
fails, have the account owner resolve it using the
[GameCI activation guide](https://game.ci/docs/github/activation/). Do not paste
license files or passwords into issue comments/logs, scrape a local computer for
credentials, or buy/change a license as a build fallback. Existing Unity terms
and a suitable license must already cover this use.

### EOS SDK licensing

`prepare-eos-ios.py` downloads the same public
[official plugin 6.2.0 release](https://github.com/EOS-Contrib/eos_plugin_for_unity/releases/tag/v6.2.0)
already pinned by Android. It checks archive and framework SHA-256 hashes,
iPhoneOS platform, architecture, minimum OS and install name before staging.
No Epic login or agreement-acceptance operation is part of this workflow.

Public download availability does not establish a right to use or redistribute
the SDK. The SDK's bundled `Runtime/EOS_SDK/license.txt` identifies the
[Epic Online Services Developer Agreement](https://dev.epicgames.com/services/terms/agreements)
as governing its use. The owner must already have the applicable rights; the
workflow does not accept new terms on their behalf. The existing exporter
includes `license.txt` and `ThirdPartySoftwareNotice.txt` in the app. Do not
commit the downloaded SDK/framework or add signing/service credentials.

## Starting the bounded run

1. Review the exact source commit and approve publishing it to development
   **`main`** or **`feature/ios-eos-crossplay`** and running Actions. Verify remote
   ancestry and preserve all current branch history before publishing.
2. The **head commit message** of that push must contain **`[ios-eos-ci]`**.
   A push without this marker leaves the expensive jobs skipped. Pushes to
   other branches, tags and pull requests do not start this workflow.
   There is no scheduled trigger, build matrix or automatic retry.
   To test Android from the same `main` commit, also include **`[android-ci]`**;
   this starts the existing Android APK workflow once, independently of iOS.
3. Find `iOS EOS unsigned device build` in Actions. Check its `head_sha` against
   the intended commit; do not associate an older green run with newer source.
   First the 15-minute Linux gate checks secret presence and the 20 offline
   staging/package tests. Only a successful gate starts the Mac.
4. Follow native build, Unity export, Xcode and package verification separately.
   Stop and diagnose a failing stage before authorizing another attempt.

`workflow_dispatch` is available once the workflow is on the default branch.
GitHub documents that manual dispatch requires the workflow on that branch;
a new feature-branch file alone is insufficient. The exact-branch marked push
supports the initial authorized test. See
[GitHub's manual-run requirements](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).

## Runner, runtime and cost boundaries

- `macos-26` selects a standard hosted ARM64 runner. Xcode **26.6** is selected
  explicitly using `DEVELOPER_DIR`; it must already exist. The job records the
  actual image, Xcode and iPhoneOS SDK versions. There is no Xcode download,
  license-accept command or silent fallback to a different version. The
  [published image inventory](https://github.com/actions/runner-images/blob/main/images/macos/macos-26-arm64-Readme.md)
  currently lists Xcode 26.6 and iPhoneOS SDK 26.5 (checked 2026-10-11).
- The Mac job has a **240-minute total ceiling**, including checkout and
  artifact work. Per-step limits are additional upper bounds, not added time.
  Cold Editor/module downloads, importing several GiB of game data, IL2CPP and
  Xcode can be slow. No hosted timing has been measured yet, so four hours is
  a cancellation limit, not a promised completion time.
- The documented standard ARM64 runner has **3 cores, 7 GB RAM and 14 GB SSD**.
  Native/Xcode parallelism and Unity workers are capped at two. Actual free
  space is measured **after checkout**, and the workflow stops before downloads
  or native compilation unless at least **35 GiB is free**. This is a conservative
  estimate: about 12.2 GiB installed Editor/iOS modules, 5.5 GiB downloads,
  12 GiB import/export/native intermediates, and 5 GiB IPA/headroom. The tool
  sizes come from Unity's
  [release metadata](https://services.api.unity.com/unity/editor/release/v1/releases?version=6000.6.4f1);
  intermediate sizes are an estimate, not a measured peak or guarantee.
  The published 14 GB storage specification alone is insufficient for this
  budget; the actual runner may fail the guard. Do not lower it to force a run.
  Standard-runner memory may also be insufficient.
  This workflow does not delete system tools or upgrade to a larger runner.
  See [runner specifications](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
- Staging preseeds `Native/data` with per-file APFS clones, then runs the unchanged
  base stager's complete hash checks. Only afterward does it clone `RuntimeAssets`
  and run the supplemental hash stager, so the base stage sees no unexpected
  supplemental files. The clone helper requires fresh destinations and rejects
  links/special files. It calls `clonefile(2)` directly and fails on cloning
  errors, with no ordinary-copy fallback. This avoids the documented best-effort
  behavior of [`cp -c`](https://raw.githubusercontent.com/apple-oss-distributions/file_cmds/main/cp/cp.1).
  Existing app-data/IPA staging retains best-effort `cp -cR` copies, bounded by
  the conservative storage guard; its behavior for framework symlinks is unchanged.
  Changed/new data, any copy fallback there, and the compressed IPA still consume
  storage.
- Standard hosted compute is free for public repositories. Private repositories
  consume included minutes and can incur overage; the currently listed standard
  macOS price is **USD 0.062/minute**, so 240 billable minutes would be **USD 14.88**
  for that job, before any other usage. Linux and artifact storage are separate.
  Artifacts expire after seven days, and Editor/Library caching is disabled.
  Recheck account budgets and current
  [runner pricing](https://docs.github.com/en/billing/reference/actions-runner-pricing)
  before paid use. Do not enable paid runners or increase limits without approval.

## Evidence and interpretation

The success artifact is named
`InitialDUnity-eos-iphoneos-<source-sha>-<attempt>` and contains:

- `InitialDUnity-EOS-iPhone-unsigned.ipa`
- `InitialDUnity.verification.json`: actual ARM64 app/Unity/EOS platform checks,
  expected EOS location/notices, release label/native manifest, runtime-data
  file hashes and ROM exclusion
- `ipa-verification.json`: a complete archived file-list check and SHA-256
  comparison of every archived file against the verified app, plus run/source
  IDs, IPA size/hash and explicit unsigned/no-service/no-device-test flags
- `SHA256SUMS` and `ios-eos-toolchain.txt`

Only the verified package step can reach the package upload. Diagnostic artifacts
are uploaded on failure when available, using a narrow list of native/Xcode/
verification logs; no home directory, activation file, license file or arbitrary
GameCI artifacts are uploaded. Unity's build-step output remains in Actions logs.

A successful run establishes real native compilation, Unity export and Xcode
packaging. It does **not** establish iPhone launch, EOS native initialization,
room discovery, relay connectivity or Android/iPhone racing. Those need a
separately authorized service-configured, signed physical-device build and the
two-host-role test plan in [the integration guide](ios-eos-integration.md).
