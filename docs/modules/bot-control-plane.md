# Bot Control Plane (`BotControlPlaneModule`)

**Priority:** `96` (`issue_comment.created`)

`BotControlPlaneModule` is always registered in the webhook pipeline (it has no `Enabled` or `DryRun` toggle) and activates only when addressed via GitHub `@<bot>` mention commands (`ignore`, `unignore`, `status`, `lang`) using the bot's live GitHub handle (resolved automatically from the GitHub API via `BotPat` or GitHub App slug). Mutating commands (`ignore` / `unignore`) verify `triage`+ collaborator permission via `IRepositoryPermissionService`, requiring only mandatory `Metadata: Read-only` on the GitHub App (or `push+` plus organization/repo scopes when `PackageUpdate:BotPat` is configured).

---

## 1. Supported Mention Commands

| Command | Allowed Callers | Behavior |
|---|---|---|
| `@<bot> ignore <package> [reason]` | Collaborators (`triage`+) | Persists a runtime ignore entry for `<package>` in `bot-state.json` (`owner/repo`). `PackageUpdateService` skips the package on subsequent sweeps without requiring a spec commit. |
| `@<bot> unignore <package>` | Collaborators (`triage`+) | Removes `<package>` from the runtime ignore list in `bot-state.json` so automated updates can resume. |
| `@<bot> status [package]` | Any commenter | Reports runtime ignore status, in-spec `#!NoAutoUpdate` directive status, persistent CI failure cooldown, OBS post-merge fallback cooldown (`ObsBlockedUntil`), and any open `Auto-Update: Blocked` tracking issue (`#<N>`). When `<package>` is omitted on an `autoupdate/<pkg>` PR, the package name is inferred from the head branch; otherwise lists all runtime-ignored packages in the repository. |
| `@<bot> lang <en\|zh\|reset>` | Any commenter (unless disabled by `RepositoryForcedLocales`) | Sets or clears the caller's personal response language preference (`en` or `zh`). Disabled when the repository configures `BotState:RepositoryForcedLocales`. |

---

## 2. Language Resolution & Retention Policy

- **Direct command replies** (`@<bot> ...` responses in `BotControlPlaneService` and `ConversationGuardService`) resolve the locale for the **command caller (`senderLogin`)**:
  1. `BotState:RepositoryForcedLocales["owner/repo"]` (if set, disables `@<bot> lang`)
  2. Caller preference (`UserLocales[senderLogin]` in `bot-state.json`)
  3. `BotState:RepositoryDefaultLocales["owner/repo"]`
  4. `BotState:DefaultLocale` (default: `"en"`)
- **PR-level governance review comments** (`PullRequestPipeline` consolidated reports) resolve the locale for the **PR author (`AuthorLogin`)** using the same precedence chain.
- **Autonomous bot pull requests** (`PackageUpdateService`) resolve `RepositoryForcedLocales -> RepositoryDefaultLocales -> DefaultLocale` without mixing languages.
- **Retention**: Inactive user language preferences older than `BotState:LocaleRetentionDays` (default: `180` days) are pruned automatically on startup (`JsonBotStateStore` initialization) and whenever preferences are updated.

---

## 3. Unified State Storage & Configuration (`BotState`)

Configured under the `BotState` JSON section:

| Parameter | Type | Default | Description |
|---|---|---|---|
| `StateDirectory` | `string` | `""` | Absolute directory path storing `bot-state.json` (runtime ignores, failure cooldowns with `LastFailureAt` decay timestamps, user locales, cursor watermarks under `Cursors` such as `rust-crates`, `cve-active-watch`, `cve-active-watch-poison`, and `candidate-scheduler-origin`) and default `lookout-first-seen.json` (relative paths fail startup validation). When empty, resolves in order: `LocalApplicationData/abaci-bot` -> `$XDG_DATA_HOME/abaci-bot` -> `$HOME/.local/share/abaci-bot` -> ephemeral temp directory (logging a warning). Mount this directory as a persistent volume in container deployments. |
| `LocaleRetentionDays` | `int` | `180` | Days to retain inactive user language preferences before pruning (`>= 1`). |
| `DefaultLocale` | `string` | `"en"` | Global fallback locale (`"en"`, `"zh"`, or `"en-*"` / `"zh-*"` subtags normalized on validation). |
| `RepositoryForcedLocales` | `object` | `{}` | Map of `"owner/repo"` to `"en"`, `"zh"`, or `"en-*"` / `"zh-*"` subtags. Locks the repository language and disables `@<bot> lang`. |
| `RepositoryDefaultLocales` | `object` | `{}` | Map of `"owner/repo"` to `"en"`, `"zh"`, or `"en-*"` / `"zh-*"` subtags. Fallback when the user has not chosen a personal preference. |
| `EphemeralReplyDeleteMinutes` | `int` | `5` | Delay in minutes (`0..1440`) before ephemeral usage and rejection replies are processed by `EphemeralReplyService` (author confirmation prompts use `AuthorLockConfirmTimeoutMinutes`). Setting `0` disables automatic cleanup. |
| `EphemeralReplyAction` | `enum` | `"Delete"` | Action taken on expired ephemeral replies: `"Delete"` (deletes the comment) or `"Minimize"` (minimizes as `OUTDATED`). |

---

## 4. Failure Handling

- **Single-Instance Exclusive State Lock**: `JsonBotStateStore` acquires an OS-level exclusive lock handle (`FileShare.None`) on `bot-state.lock` in `BotState:StateDirectory` at startup. Starting a second instance targeting the same state directory fails fast with `InvalidOperationException` rather than silently overwriting `bot-state.json` (the OS releases the lock automatically on process exit).
- **Atomic Persistence**: `JsonBotStateStore` writes to a temporary file and atomically replaces `bot-state.json`.
- **Corrupt State vs. I/O Read Failure**: Invalid JSON syntax or null-deserializing `bot-state.json` logs `LogLevel.Error` (surfaced via `/health?verbose=true` `runtime.lastError`), moves the corrupt file to `bot-state.json.corrupt.<yyyyMMddHHmmss>`, and starts with an empty in-memory state (failing fast with `InvalidOperationException` if moving the corrupt file fails). Conversely, filesystem read/permission errors (`IOException` / `UnauthorizedAccessException`) reading an existing `bot-state.json` fail fast immediately without renaming or clearing the file.
- **Persistence Failure Handling**: Runtime disk write errors during `SaveState` log `LogLevel.Error` and throw; `JsonBotStateStore` rolls back in-memory state for control-plane mutations so memory never diverges from disk, and `BotControlPlaneService` replies explicitly that persistence failed and the command was not applied.
- **State-First Commit & Reply Resilience**: Control-plane mutations persist on disk before posting GitHub confirmation comments; HTTP 403 (`ForbiddenException`) or transient reply failures on locked/archived pull requests are logged safely without rolling back committed state.
- **Fail-Closed Authorization**: `ignore` and `unignore` verify `triage`+ repository permission via `IRepositoryPermissionService`; lookup errors deny the mutation.
