# Conversation Guard & Moderation Module

Provides pull request conversation locking, comment spam suppression, PR archiving commands, and automated locking of inactive closed pull requests.

---

## 1. Purpose and Trigger Events

High-profile or contentious package changes can attract heated discussions, post-closure harassment, or spam comments. `ConversationGuardModule` (Priority: 95) empowers PR authors and maintainers to control discussion state using slash commands. In addition, `LockSweepService` scans closed pull requests and automatically locks discussions after an extended period of inactivity.

### Trigger Events
- `issue_comment` (`created`) on **Pull Requests only** (plain repository issues are ignored by the webhook processor)
- Periodic background timer (`LockSweepService`, default every 12 hours)

---

## 2. Slash Commands, Authorization, and Moderation Workflow

When `ConversationGuard:Enabled` is `true`, maintainer authority is verified against **GitHub's authoritative repository collaborator permissions** (`triage`, `write`, `maintain`, or `admin` via `IRepositoryPermissionService`). This accurately handles first-time contributors, external collaborators without organization membership, and read-only organization members.

> [!IMPORTANT]
> Enabling `ConversationGuard` requires granting the GitHub App **Administration: Read-only** (or **Contents: Read & write**) so `ConversationGuardPermissionProbe` and command authorization can query repository collaborator permissions for `@<bot> lock`, `@<bot> unlock`, and `@<bot> hide`.
> Enabling `ConversationGuard:ArchiveEnabled` (`@<bot> archive`) requires a repository **`triage`+ user PAT** configured via `PackageUpdate:BotPat` (note that configuring `BotPat` shifts all bot GitHub API writes to that user account), because GitHub's `archivePullRequest` and `unarchivePullRequest` GraphQL mutations require a user role (`triage` or higher) and always return `FORBIDDEN` for GitHub App installation tokens even when **Administration: Read & write** is granted. When `ArchiveEnabled == true`, both startup configuration validation (`ValidateOnStart`) and `ConversationGuardPermissionProbe` fail fast at startup (before webhook dispatch or `LockSweepService` runs) if `PackageUpdate:BotPat` is absent or lacks `triage`+ repository access. Leave `ConversationGuard:ArchiveEnabled` set to `false` for GitHub App-only deployments (and set `ConversationGuard:Enabled` to `false` to stay on the baseline minimal permissions in [deployment.md](../deployment.md)).

```mermaid
flowchart TD
    Comment["PR Issue Comment Created"] --> MatchCmd{"Matches @bot mention command?"}
    MatchCmd -- No --> Ignore["Ignore comment"]
    MatchCmd -- "@bot lock" --> CheckAuthLock{"Sender permission?"}
    CheckAuthLock -- "Collaborator (triage+)" --> LockImmediate["Lock conversation immediately<br/>(Reason: TooHeated)"]
    CheckAuthLock -- "PR Author" --> CheckConfirm{"Active '@bot lock confirm' within timeout?"}
    CheckConfirm -- Yes --> LockImmediate
    CheckConfirm -- No --> PromptConfirm["Post prompt: reply '@bot lock confirm'<br/>within AuthorLockConfirmTimeoutMinutes"]
    CheckAuthLock -- "Other User" --> RejectLock["Post permission warning"]

    MatchCmd -- "@bot unlock" --> CheckAuthUnlock{"Has triage+?"}
    CheckAuthUnlock -- Yes --> UnlockPr["Unarchive if needed + Unlock conversation"]
    CheckAuthUnlock -- No --> RejectUnlock["Post permission warning"]

    MatchCmd -- "@bot hide @user" --> CheckAuthHide{"Has triage+?"}
    CheckAuthHide -- Yes --> HideComments["Minimize target comments (SPAM)<br/>Skip triage+ users & report actual count"]
    CheckAuthHide -- No --> RejectHide["Post permission warning"]

    MatchCmd -- "@bot archive" --> CheckAuthArchive{"triage+ or confirmed Author<br/>& ArchiveEnabled?"}
    CheckAuthArchive -- Yes --> PostExitAndArchive["1. Verify PR & post exit comment<br/>2. GraphQL archivePullRequest<br/>3. On failure, try compensation comment"]
    CheckAuthArchive -- No --> RejectArchive["Post permission / confirmation / disabled notice"]
```

### Supported Mention Commands

All commands address the bot using its live GitHub handle (resolved automatically from GitHub API via `BotPat` or GitHub App slug, e.g., `@abaci-bot`).

| Command | Allowed Callers | Confirmation / Behavior |
|---|---|---|
| `@<bot> lock` | Collaborators (`triage`+), PR Author | Collaborators (`triage`+) lock immediately. Non-privileged PR authors receive a prompt requiring `@<bot> lock confirm` (or `@<bot> lock yes`) within `AuthorLockConfirmTimeoutMinutes`. Checks `IsIssueLockedAsync` first, posts the lock announcement **before** locking (since locked issues reject App comments with 403), and locks with reason `TooHeated`. |
| `@<bot> lock confirm` | PR Author, Collaborators (`triage`+) | Atomically completes author self-lock if an active request exists within the TTL window; otherwise starts a new confirmation window. |
| `@<bot> unlock` | Collaborators (`triage`+) only | Checks `IsIssueLockedAsync` first; when locked and `ArchiveEnabled == true`, attempts unarchiving first (logging a warning and noting any unarchive failure in the reply if GraphQL fails), unlocks the conversation inside an isolated error handler, and posts the unlock notice via HTTP 403-resilient `TryPostReplyAsync`. If not currently locked, replies without posting a false unlock confirmation. |
| `@<bot> hide @username` | Collaborators (`triage`+) only | Matches exact username first; only when zero exact matches exist and the input omits `[bot]`, falls back one-way to `@username[bot]` (never strips `[bot]` to match a human user). Folds all matched comments using GitHub GraphQL `minimizeComment(classifier: SPAM)`. Never deletes comments. Protects comments authored by users with `triage`+ repository access (on both exact and `[bot]`-resolved targets); all status/error/summary replies handle HTTP 403 on locked/archived PRs gracefully without crashing the pipeline. |
| `@<bot> archive` | Collaborators (`triage`+), PR Author | Archives the pull request via GraphQL `archivePullRequest`. Requires `ArchiveEnabled == true` **and** a `triage`+ user PAT via `PackageUpdate:BotPat` (GitHub App installation tokens cannot archive pull requests even with `Administration: Read & write`); non-privileged PR authors must confirm via `@<bot> archive confirm` within `AuthorLockConfirmTimeoutMinutes`. Posts a tracked exit comment before archiving (returning cleanly on 403 if already locked/archived), and updates that exit comment in-place via `UpdateCommentAsync` on failure (falling back to a compensation comment only if tracked ID is unavailable or update fails). |

---

## 3. Inactive Pull Request Locking

`LockSweepService` runs periodically (default every 12 hours) to lock abandoned closed pull requests:
- Evaluates `closed_at` timestamps using paginated GitHub search (`is:pr is:closed is:unlocked closed:<{date}`, where `500` is the search transport cap of `maxPages=5` x `perPage=100`).
- Locks at most `MaxLocksPerSweep` (default: `50`, range: `1..500`) unlocked pull requests per repository in each sweep cycle with reason `TooHeated`, deferring any remaining eligible PRs to subsequent sweeps.
- PRs closed longer than `InactiveCloseDays` (default: 60 days) are eligible for locking.
- When search results exceed the 500-item transport limit or reach `MaxLocksPerSweep`, truncation/deferral is logged explicitly and remaining items continue in subsequent sweeps.

---

## 4. Configuration Options

Configured under the `ConversationGuard` JSON section:

| Parameter | Type | Default | Range / Allowed | Description |
|---|---|---|---|---|
| `Enabled` | `bool` | `false` | `true`, `false` | Master switch for conversation guard commands, startup permission probe, and lock sweep service. |
| `DryRun` | `bool` | `true` | `true`, `false` | When true, performs **zero GitHub writes** (logs moderation actions and suppresses rejection, usage, and confirmation prompt comments). |
| `RepositoryAllowList` | `string[]` | `[]` | Strict `owner/repo` list | Repositories permitted to execute conversation guard actions (bare repository names fail startup validation). Empty disables moderation globally. |
| `ArchiveEnabled` | `bool` | `false` | `true`, `false` | Dedicated safety switch for PR-level archiving/unarchiving (`@<bot> archive`). When `false`, archive commands are rejected; when `true`, both `ConversationGuardOptionsValidator` (`ValidateOnStart`) and `ConversationGuardPermissionProbe` require a `triage`+ user PAT (`PackageUpdate:BotPat`) and abort startup in GitHub App-only mode. Note: `LockSweepService` uses REST `LockIssueAsync` only (compatible with App-only mode and never calls `archivePullRequest`). |
| `InactiveCloseDays` | `int` | `60` | `7..365` | Inactivity threshold in days based on `closed_at` before closed PRs are locked. |
| `SweepIntervalHours` | `int` | `12` | `1..72` | Execution interval in hours for the background inactive PR lock sweep service. |
| `AuthorLockConfirmTimeoutMinutes` | `int` | `15` | `1..60` | Time window in minutes for the PR author to reply with `@<bot> lock confirm` (or `@<bot> archive confirm`) before the confirmation request expires. |
| `MaxLocksPerSweep` | `int` | `50` | `1..500` | Maximum inactive closed pull requests locked per repository per sweep; remaining PRs defer to subsequent sweeps (500 is the search transport cap). |

---

## 5. Failure Handling and Fallback Semantics

- **Dual Startup Archive Gate & Fail-Fast Permission Probe**: Whenever `ConversationGuard:Enabled == true` (including `DryRun == true`), `ConversationGuardOptionsValidator` (`ValidateOnStart`) checks that `ArchiveEnabled=true` is never combined with GitHub App-only mode before webhooks or `LockSweepService` start, and `ConversationGuardPermissionProbe` probes the same `ReviewPermission` endpoint used by runtime authorization on every repository in `RepositoryAllowList` (plus verifying a `triage`+ user PAT via `PackageUpdate:BotPat` when `ArchiveEnabled == true`), aborting startup if required permissions are missing. `LockSweepService` only performs REST `LockIssueAsync` and never invokes `archivePullRequest`/`unarchivePullRequest`. At runtime, permission lookup errors fail closed (denying privileged commands), and HTTP 403 (`ForbiddenException`) or transient API failures when replying on locked/archived PRs across `ConversationGuardService` and `BotControlPlaneService` are logged safely without crashing the webhook pipeline.
- **Strict Zero-Write DryRun**: When `DryRun == true`, `ConversationGuardService` suppresses all GitHub write operations, including unauthorized warning replies, `hide` usage/protection notices, `archive` disabled notices, and PR author confirmation prompt comments.
- **Pending Confirmation State Lifecycle**: Author confirmation tokens are held in-memory inside `PendingConfirmationStore` (bounded to 4096 entries with lazy expiration pruning). Expired tokens are pruned automatically and cannot be replayed; pending unconfirmed requests do not survive process restarts, requiring the author to re-initiate `@<bot> lock` or `@<bot> archive`.
- **Tracked Announcement & In-Place Failure Replacement**: Because locked and archived PRs reject comments with HTTP 403, `@<bot> lock` and `@<bot> archive` post their announcement comment first via `IEphemeralCommentGateway.CreateCommentWithMetadataAsync` (aborting cleanly if HTTP 403 indicates concurrent lock/archive). If the subsequent `LockIssueAsync` or `ArchivePullRequestAsync` call fails, the bot updates the announcement comment in-place via `UpdateCommentAsync` so contradictory "succeeded" and "failed" comments never accumulate on the PR timeline (falling back to a separate compensation comment only if no tracked ID is available or updating fails).
- **Safe One-Way `[bot]` Resolution for `hide`**: `@<bot> hide @username` matches exact logins first and only falls back one-way to `@username[bot]` when no exact matches exist and the input omits `[bot]` — never stripping `[bot]` to match human accounts — while enforcing maintainer protection on the resolved login.
- **Cross-Module 403 Isolation**: Background sweep workers (`MilestoneBackfillSweepService`, `LockSweepService`, `ObsPostMergeVerifier`) catch per-item exceptions including HTTP 403 on archived PRs, while `StaleLifecycleService` scans only open items (`is:open`).
- **No Comment Deletions**: `@<bot> hide` exclusively folds comments via `SPAM` minimization; comments are never deleted.

---

## 6. Minimal Configuration Example

```json
{
  "ConversationGuard": {
    "Enabled": true,
    "DryRun": false,
    "RepositoryAllowList": [
      "5teve2NT/openRuyi"
    ],
    "ArchiveEnabled": false,
    "InactiveCloseDays": 60,
    "SweepIntervalHours": 12,
    "AuthorLockConfirmTimeoutMinutes": 15
  }
}
```

---

## 7. Relevant Test Suites

- `tests/abaci-bot.Tests/ConversationGuardOptionsValidatorTests.cs`
- `tests/abaci-bot.Tests/ConversationGuardPermissionProbeTests.cs`
- `tests/abaci-bot.Tests/PendingConfirmationStoreTests.cs`
- `tests/abaci-bot.Tests/ConversationGuardServiceTests.cs`
- `tests/abaci-bot.Tests/ConversationGuardModuleTests.cs`
- `tests/abaci-bot.Tests/LockSweepServiceTests.cs`
- `tests/abaci-bot.Tests/RepositoryAllowListTests.cs`
