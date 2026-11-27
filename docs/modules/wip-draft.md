# WIP Draft Guidance Module

Maintains a single idempotent GitHub `> [!TIP]` reminder comment helping contributors keep `WIP` pull request titles and GitHub draft state aligned without altering manual drafts.

---

## 1. Purpose and Trigger Events

`WipDraftModule` (Priority: **22**) inspects the pull request title (via shared token matcher `WipTitleHelper.IsWip`, matching prefix or bracketed tokens such as `WIP:`, `WIP-`, `WIP.`, `WIP,`, `WIP：`, `[WIP]`, `(WIP)`, while ignoring words like `SWIPE`), current draft status, terminal state, and any existing bot-owned TIP comment (`<!-- abaci-bot:wip-tip:draft -->` or `<!-- abaci-bot:wip-tip:ready -->`) to maintain at most one reminder comment per pull request.

### Trigger Events
- `pull_request` (`opened`, `reopened`, `edited`, `synchronize`, `converted_to_draft`, `ready_for_review`, `closed`)

---

## 2. State Reconciliation Table

| PR State | Title Has WIP | Is Draft | Existing Bot TIP | Action |
|---|---|---|---|---|
| Closed / Merged / Complete | Any | Any | Any | Delete existing TIP(s) |
| Open | Yes | No | None / Draft / Ready | Create or update single `draft` TIP (and delete duplicates) |
| Open | Yes | Yes | Draft / Ready | Delete existing TIP(s) |
| Open | No | Yes | Draft | Update existing TIP to `ready` TIP (and delete duplicates) |
| Open | No | Yes | Ready | Keep existing `ready` TIP (and delete duplicates; otherwise no-op) |
| Open | No | Yes | None | Do nothing (respect manual draft) |
| Open | No | No | Any | Delete existing TIP(s) |

The comment uses pure GitHub Flavored Markdown (`> [!TIP]` + `[Open the pull request](https://github.com/openRuyi-Project/openRuyi/pull/1)`) without embedded HTML `<button>` elements, requires no GraphQL mutations, and does not write any labels.

---

## 3. Configuration Options

Configured under the `WipDraft` section in `appsettings.json` (`Options/WipDraftOptions.cs`):

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Enables or disables WIP-to-Draft TIP reminder comment reconciliation on pull requests. |
| `DryRun` | `bool` | `false` | When `true`, logs intended TIP comment actions without creating, updating, or deleting comments on GitHub. |
| `DocumentationLinks` | `DocumentationLink[]` | `[]` | Max 5 unique `http`/`https` links (`Title`, optional `TitleZh`, `Url`) rendered inside `> [!TIP]` reminder comments. |

---

## 4. Failure Handling and Fallback Semantics

- **Comment Inspection Errors**: If reading existing comments fails transiently, `WipDraftModule` logs a warning, records the error in `RuntimeHealthState`, and skips reconciliation for that delivery without blocking other modules or label updates.
- **Idempotent 404 Handling**: When `PullRequestPipeline` flushes `PendingNotices`, concurrent deletion (`NotFoundException`) on an update or delete is ignored idempotently without aborting label synchronization.
- **Transient / Non-Retriable Comment Errors**: Non-retriable API errors log into `RuntimeHealthState` without reverting labels; transient rate-limit/network failures bubble through `CommentPostingTransientException` for `WebhookDispatcher` retry.
- **DryRun Mode**: Set `WipDraft:DryRun` to `true` for initial rollout observation before allowing live comment writes.

---

## 5. Relevant Test Suites

- `tests/abaci-bot.Tests/WipDraftModuleTests.cs`
- `tests/abaci-bot.Tests/PullRequestPipelineCommentTests.cs`
