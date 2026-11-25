# abaci-bot

`abaci-bot` is a webhook-driven governance daemon and compliance bot built for `openRuyi-Project` package repositories and related infrastructure, and configurable for other RPM-based Linux distributions.

---

## Getting Started

> **Prerequisites:** An existing GitHub organization you administer (`<your-org>`) containing your target package repository and host access to expose an HTTPS webhook endpoint.

1. **Create and install the GitHub App.** Configure the webhook URL
   (`https://<your-host>/api/webhook`), the webhook secret, the repository and
   organization permissions (`Contents: Read and write` when `PackageUpdate:Enabled=true && DryRun=false`,
   or `Read-only` when disabled or in `DryRun`), the private key, and the event subscriptions. Install
   the App on your organization (`<your-org>`) and note the installation ID.
   Follow [GitHub App Setup from Scratch](docs/deployment.md#5-github-app-setup-from-scratch).

2. **Create the maintainer team.** Create an organization team under `<your-org>`
   (for example slug `<team-slug>` such as `captains`, matching `GitHubApp:TeamName`)
   and add the maintainers who should move pull requests into review. The team must
   be visible to the App's `Members` permission.

3. **Create the managed labels.** Every label the bot writes must exist before the
   first webhook arrives; a missing label fails the pipeline for that event.
   Use the commands in [docs/labels.md](docs/labels.md).

4. **Configure the application.** Copy the reference file and fill the five App
   values plus the listen address:

   ```bash
   cp appsettings.demo.jsonc appsettings.json
   ```

   Required: `GitHubApp:AppId`, `GitHubApp:InstallationId`,
   `GitHubApp:PrivateKey`, `GitHubApp:WebhookSecret`, `GitHubApp:TeamName`.
   See [docs/configuration.md](docs/configuration.md) for precedence, secrets, and
   the full option reference.

5. **Prepare persistent state and config permissions (`uid 1654`) and start the service.** Ensure the
   non-root container user (`uid 1654`) owns the persistent `state/` volume (`bot-state.json`, locks,
   and Lookout first-seen timestamps) and can read the mounted `appsettings.json`:

   ```bash
   mkdir -p state && sudo chown 1654:1654 state
   sudo chown 1654:1654 appsettings.json && chmod 600 appsettings.json
   docker compose up -d
   ```

6. **Verify.** Check the health endpoint, then open a draft pull request and confirm
   that `Status: New`, `Workflow: In Dev`, `Target: Rolling`, and the build-system
   labels appear.

   ```bash
   curl -f http://localhost:3456/health
   ```

---

## Capabilities & Modules

| Module | Scope & Function | Documentation |
|---|---|---|
| **Workflow State** | Tracks review progression, WIP titles, draft states, and merge completion. | [`docs/modules/workflow-state.md`](docs/modules/workflow-state.md) |
| **New PR Triage** | Tags newly opened PRs with `Status: New`; removes upon first human response. | [`docs/modules/workflow-state.md`](docs/modules/workflow-state.md#shiny-new-pr-governance-shinynewprmodule) |
| **Contributor Classification** | Classifies student and external community contributions by commit author email. | [`docs/modules/user-contribution.md`](docs/modules/user-contribution.md) |
| **Target Stream** | Classifies release branches into `Target: Rolling` vs `Target: LTS`. | [`docs/modules/target-classification.md`](docs/modules/target-classification.md) |
| **Stale Lifecycle** | Monitors inactivity, marks `Status: Stale`, and closes abandoned items. | [`docs/modules/stale-lifecycle.md`](docs/modules/stale-lifecycle.md) |
| **Change Type** | Classifies changes as `Type: Packaging`, `Documentation`, `Infrastructure`, `Frontend`. | [`docs/modules/change-type.md`](docs/modules/change-type.md) |
| **Build System** | Detects build tools in specs (`autotools`, `cmake`, `meson`, `rust`, etc.) and tags `CI`. | [`docs/modules/build-system.md`](docs/modules/build-system.md) |
| **AI Assistance** | Detects author disclosures regarding AI assistance in pull request descriptions. | [`docs/modules/ai-assistance.md`](docs/modules/ai-assistance.md) |
| **SPDX Compliance** | Validates that spec `License:` headers conform to canonical SPDX identifiers. | [`docs/modules/spdx-compliance.md`](docs/modules/spdx-compliance.md) |
| **CVE Security** | Scans PRs for CVE mentions, queries OSV/NVD, and manages severity and lifecycle. | [`docs/modules/vulnerability.md`](docs/modules/vulnerability.md) |
| **Rebuild Impact** | Computes transitive reverse build dependencies via RPM repodata; gates heavy PRs. | [`docs/modules/rebuild-impact.md`](docs/modules/rebuild-impact.md) |
| **ABI Compliance** | Compares compiled RPMs using Libabigail `abipkgdiff` to detect binary breaking changes. | [`docs/modules/abi-compliance.md`](docs/modules/abi-compliance.md) |
| **Package Updates** | Periodically checks upstream releases, rewrites specs, publishes PRs, and verifies post-merge OBS builds. | [`docs/modules/package-update.md`](docs/modules/package-update.md) |
| **Active CVE Watch** | Periodically scans package catalog against OSV and NVD to file and update security tracking issues. | [`docs/modules/cve-active-watch.md`](docs/modules/cve-active-watch.md) |
| **Milestone Automation** | Automatically tags merged PRs with release milestone `YYYY.MM`; creates missing milestones. | [`docs/modules/milestone-sync.md`](docs/modules/milestone-sync.md) |
| **Conversation Guard** | Moderates PR discussions via `@<bot> lock`, `unlock`, `hide @user`, `archive` and locks inactive closed PRs. | [`docs/modules/conversation-guard.md`](docs/modules/conversation-guard.md) |
| **Bot Control Plane** | Handles `@<bot> ignore`, `unignore`, `status`, and `lang` mention commands with persistent state. | [`docs/modules/bot-control-plane.md`](docs/modules/bot-control-plane.md) |

---

## Health and Diagnostics

```bash
curl -f http://localhost:3456/health
curl -f "http://localhost:3456/health?verbose=true"
```

`/health` returns liveness and immutable build/artifact metadata (`status`, `timestamp`, `version`, `commit`, `imageDigest`, `buildTime`, `deployment`).
Passing `?verbose=true` additionally returns `startedAt`, `uptimeSeconds`, `capabilities` (`abiEngine`, `conversationGuardPermission`, `packageUpdatePermission`), `modules` (per-module `enabled`, `dryRun`, and safety switches such as `archiveEnabled` and `allowManagedFallback`), `runtime` (`lastError`, `lastErrorAt`), and `sources` (`Lookout`, `RustCrates`, `CveActiveWatch`).
When the native Libabigail tool (`abipkgdiff`) is missing at startup (from which
RPM extraction support is also inferred), the process aborts unless
`AbiCompliance:AllowManagedFallback` is explicitly enabled. See [docs/modules/abi-compliance.md](docs/modules/abi-compliance.md).

---

## Further Reading

- [docs/deployment.md](docs/deployment.md) — GitHub App setup, Docker, systemd, reverse proxy, rollback.
- [docs/configuration.md](docs/configuration.md) — configuration precedence, comments, secrets, environment variables.
- [docs/labels.md](docs/labels.md) — label taxonomy, mutex groups, provisioning and migration.
- [docs/architecture.md](docs/architecture.md) — pipeline, module priorities, background workers.
- [docs/testing.md](docs/testing.md) — test harness, trigger matrix, sandbox validation.
- [docs/README.md](docs/README.md) — documentation index and reading paths.

---

## License

This project is licensed under the MIT License. See the `LICENSE` file for details.
