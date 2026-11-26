using Microsoft.Extensions.Logging;
using Octokit;
using Octokit.Internal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace abaci_bot.Services;

public class GitHubService : IGitHubService, IRepositoryPermissionService, IPackageUpdatePermissionProbeGateway, IBotIdentityProvider, IEphemeralCommentGateway, IRepositoryArchiveGateway, IPagedIssueSearchGateway, IOpenPullRequestInventoryGateway, IOptimisticFileContentGateway, IDisposable
{
    private static readonly AsyncLocal<CancellationToken> _ambientCancellationToken = new();

    private readonly int _appId;
    private readonly string _privateKey;
    private readonly long _installationId;
    private readonly string? _botPat;
    private readonly IGitHubClient _client;
    private readonly HttpClient _archiveHttpClient;
    private readonly Func<Credentials, IGitHubClient>? _jwtClientFactory;
    private DateTimeOffset _tokenExpiry;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly SemaphoreSlim _botLoginLock = new(1, 1);
    private volatile string? _cachedToken;
    private string? _cachedBotLogin;
    private readonly ILogger<GitHubService>? _logger;

    public GitHubService(int appId, string privateKey, long installationId, ILogger<GitHubService>? logger = null)
        : this(appId, privateKey, installationId, (string?)null, CreateDefaultTransport(), logger)
    {
    }

    public GitHubService(int appId, string privateKey, long installationId, string? botPat, ILogger<GitHubService>? logger = null)
        : this(appId, privateKey, installationId, botPat, CreateDefaultTransport(), logger)
    {
    }

    internal GitHubService(int appId, string privateKey, long installationId, HttpMessageHandler httpMessageHandler, ILogger<GitHubService>? logger = null)
        : this(
            appId,
            privateKey,
            installationId,
            null,
            CreateClientForTransport(CreateCancellableTransport(new HttpClientAdapter(() => httpMessageHandler))),
            creds => CreateClientForTransport(CreateCancellableTransport(new HttpClientAdapter(() => httpMessageHandler)), creds),
            logger,
            httpMessageHandler)
    {
    }

    private GitHubService(
        int appId,
        string privateKey,
        long installationId,
        string? botPat,
        Octokit.Internal.IHttpClient cancellableTransport,
        ILogger<GitHubService>? logger)
        : this(
            appId,
            privateKey,
            installationId,
            botPat,
            CreateClientForTransport(cancellableTransport),
            creds => CreateClientForTransport(cancellableTransport, creds),
            logger)
    {
    }

    internal GitHubService(
        int appId,
        string privateKey,
        long installationId,
        IGitHubClient client,
        ILogger<GitHubService>? logger = null,
        HttpMessageHandler? archiveHttpHandler = null)
        : this(appId, privateKey, installationId, null, client, null, logger, archiveHttpHandler)
    {
    }

    internal GitHubService(
        int appId,
        string privateKey,
        long installationId,
        string? botPat,
        IGitHubClient client,
        ILogger<GitHubService>? logger = null,
        HttpMessageHandler? archiveHttpHandler = null)
        : this(appId, privateKey, installationId, botPat, client, null, logger, archiveHttpHandler)
    {
    }

    private GitHubService(
        int appId,
        string privateKey,
        long installationId,
        string? botPat,
        IGitHubClient client,
        Func<Credentials, IGitHubClient>? jwtClientFactory,
        ILogger<GitHubService>? logger,
        HttpMessageHandler? archiveHttpHandler = null)
    {
        _appId = appId;
        _privateKey = privateKey;
        _installationId = installationId;
        _botPat = botPat;
        _client = client;
        _jwtClientFactory = jwtClientFactory;
        _logger = logger;
        _archiveHttpClient = archiveHttpHandler != null
            ? new HttpClient(archiveHttpHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) }
            : new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    private static Octokit.Internal.IHttpClient CreateDefaultTransport() =>
        CreateCancellableTransport(new HttpClientAdapter(() => new HttpClientHandler()));

    private static Octokit.Internal.IHttpClient CreateCancellableTransport(Octokit.Internal.IHttpClient httpClient) =>
        new CancellableHttpClient(httpClient, () => _ambientCancellationToken.Value);

    private static IGitHubClient CreateClientForTransport(
        Octokit.Internal.IHttpClient cancellableTransport,
        Credentials? credentials = null)
    {
        var connection = new Connection(new ProductHeaderValue("abaci-bot"), cancellableTransport);
        var client = new GitHubClient(connection);
        if (credentials != null)
        {
            client.Credentials = credentials;
        }
        return client;
    }

    private static IDisposable SetAmbientToken(CancellationToken cancellationToken)
    {
        var prior = _ambientCancellationToken.Value;
        _ambientCancellationToken.Value = cancellationToken;
        return new TokenScope(prior);
    }

    private sealed class TokenScope : IDisposable
    {
        private readonly CancellationToken _prior;
        public TokenScope(CancellationToken prior) => _prior = prior;
        public void Dispose() => _ambientCancellationToken.Value = _prior;
    }

    // Ensures the GitHub client has a valid installation access token before API calls.
    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);

        // If a dedicated Bot PAT is configured, prioritize it
        if (!string.IsNullOrWhiteSpace(_botPat))
        {
            _client.Connection.Credentials = new Credentials(_botPat);
            _cachedToken = _botPat;
            return;
        }

        if (_tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
            return;

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (_tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
                return;

            var jwt = GenerateJwt();

            AccessToken token;
            if (_jwtClientFactory != null)
            {
                var jwtClient = _jwtClientFactory(new Credentials(jwt, AuthenticationType.Bearer));
                token = await jwtClient.GitHubApps.CreateInstallationToken(_installationId);
            }
            else
            {
                _client.Connection.Credentials = new Credentials(jwt, AuthenticationType.Bearer);
                token = await _client.GitHubApps.CreateInstallationToken(_installationId);
            }

            _client.Connection.Credentials = new Credentials(token.Token);
            _cachedToken = token.Token;
            _tokenExpiry = token.ExpiresAt;
        }
        finally
        {
            _authLock.Release();
        }
    }

    // Generates a GitHub App JWT
    // 1) Build the header.
    // 2) Build the payload.
    // 3) Sign `header.payload` with the RSA private key using SHA256.
    // 4) Return `header.payload.signature`.
    private string GenerateJwt()
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64UrlEncode(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64UrlEncode(JsonSerializer.Serialize(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = _appId
        }));

        var dataToSign = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKey);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(dataToSign),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{dataToSign}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(string input) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(input));

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public async Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await _client.PullRequest.Files(owner, repo, prNumber);
    }

    public async Task<string?> GetPullRequestAuthorEmailAsync(
        string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Get commits from the PR endpoint (works on the base repo, no need to access fork)
        var commits = await _client.PullRequest.Commits(owner, repo, prNumber);
        if (commits.Count > 0)
        {
            // Return the author email from the latest commit
            return commits[^1].Commit.Author.Email;
        }
        return null;
    }

    public async Task<string> GetFileContentAsync(
        string owner, string repo, string path, string sha, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var contents = await _client.Repository.Content.GetRawContentByRef(owner, repo, path, sha);
        return Encoding.UTF8.GetString(contents);
    }

    public async Task AddLabelsAsync(
        string owner, string repo, int issueNumber, string[] labels, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await _client.Issue.Labels.AddToIssue(owner, repo, issueNumber, labels);
    }

    public async Task RemoveLabelAsync(
        string owner, string repo, int issueNumber, string label, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _client.Issue.Labels.RemoveFromIssue(owner, repo, issueNumber, label);
        }
        catch (NotFoundException) { }
    }

    public async Task<HashSet<string>> GetTeamMembersAsync(
        string owner, string teamName, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var teamSlug = await _client.Organization.Team.GetByName(owner, teamName.ToLowerInvariant());
        var members = await _client.Organization.Team.GetAllMembers(teamSlug.Id);
        return members.Select(m => m.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<PullRequest?> GetPullRequestAsync(string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _client.PullRequest.Get(owner, repo, prNumber);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Issue>> SearchStaleCandidatesAsync(
        string owner, string repo, DateTimeOffset updatedBefore, bool isPr, string? labelFilter = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();
        var dateStr = updatedBefore.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var baseQuery = isPr
            ? $"repo:{owner}/{repo} is:open is:pr -is:draft updated:<{dateStr}"
            : $"repo:{owner}/{repo} is:open is:issue updated:<{dateStr}";
        var query = !string.IsNullOrWhiteSpace(labelFilter) ? $"{baseQuery} {labelFilter}" : baseQuery;

        var pageResult = await SearchIssuesPaginatedAsync(
            owner,
            repo,
            query,
            isPr ? IssueTypeQualifier.PullRequest : IssueTypeQualifier.Issue,
            nameof(SearchStaleCandidatesAsync),
            ct);
        return pageResult.Items;
    }

    public async Task<GitHubSearchPageResult<Issue>> SearchOpenIssuesByLabelAsync(
        string owner, string repo, string label, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var query = $"repo:{owner}/{repo} is:open is:issue label:\"{label}\"";
        return await SearchIssuesPaginatedAsync(
            owner,
            repo,
            query,
            IssueTypeQualifier.Issue,
            nameof(SearchOpenIssuesByLabelAsync),
            ct);
    }

    private async Task<GitHubSearchPageResult<Issue>> SearchIssuesPaginatedAsync(
        string owner,
        string repo,
        string query,
        IssueTypeQualifier? typeQualifier,
        string operationName,
        CancellationToken ct)
    {
        var allItems = new List<Issue>();
        var totalCount = 0;
        var page = 1;
        const int perPage = 100;
        const int maxPages = 5; // Safety bound to prevent runaway loops

        while (page <= maxPages)
        {
            ct.ThrowIfCancellationRequested();

            var searchRequest = new SearchIssuesRequest(query)
            {
                Page = page,
                PerPage = perPage
            };

            if (typeQualifier.HasValue)
            {
                searchRequest.Type = typeQualifier.Value;
            }

            SearchIssuesResult searchResult;
            try
            {
                searchResult = await _client.Search.SearchIssues(searchRequest);
            }
            catch (RateLimitExceededException ex)
            {
                _logger?.LogDebug(ex, "Search API rate limit exceeded during {Operation} pagination (page {Page}) for {Owner}/{Repo}. Discarding {Count} partially retrieved items.",
                    operationName, page, owner, repo, allItems.Count);
                throw;
            }

            if (searchResult?.Items == null || searchResult.Items.Count == 0)
                break;

            totalCount = searchResult.TotalCount;
            allItems.AddRange(searchResult.Items);
            if (searchResult.Items.Count < perPage || allItems.Count >= totalCount)
                break;

            if (page == maxPages && allItems.Count < totalCount)
            {
                _logger?.LogWarning("{Operation} truncated for {Owner}/{Repo}: retrieved {Retrieved} of {Total} items (max {MaxPages} pages).",
                    operationName, owner, repo, allItems.Count, totalCount, maxPages);
            }

            page++;
            if (page <= maxPages)
            {
                // Throttle between pagination requests to prevent search API bursts
                await Task.Delay(500, ct);
            }
        }

        var isTruncated = allItems.Count < totalCount;
        return new GitHubSearchPageResult<Issue>(allItems, totalCount, isTruncated);
    }

    public async Task<Issue?> GetIssueAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();
        try
        {
            return await _client.Issue.Get(owner, repo, number);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task CloseIssueAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        var update = new IssueUpdate { State = ItemState.Closed };
        await _client.Issue.Update(owner, repo, number, update);
    }

    public async Task<Issue> CreateIssueAsync(
        string owner, string repo, string title, string body, string[]? labels = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var newIssue = new NewIssue(title)
        {
            Body = body
        };

        if (labels != null && labels.Length > 0)
        {
            foreach (var label in labels)
            {
                newIssue.Labels.Add(label);
            }
        }

        return await _client.Issue.Create(owner, repo, newIssue);
    }

    public async Task<Issue> UpdateIssueAsync(
        string owner, string repo, int number, string title, string body, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var update = new IssueUpdate
        {
            Title = title,
            Body = body
        };

        return await _client.Issue.Update(owner, repo, number, update);
    }

    public async Task CreateCommentAsync(string owner, string repo, int number, string comment, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        await _client.Issue.Comment.Create(owner, repo, number, comment);
    }

    public async Task<CreatedCommentRef> CreateCommentWithMetadataAsync(string owner, string repo, int number, string comment, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        var created = await _client.Issue.Comment.Create(owner, repo, number, comment);
        return new CreatedCommentRef(created?.Id ?? 0, created?.NodeId);
    }

    public async Task DeleteCommentAsync(string owner, string repo, long commentId, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        try
        {
            await _client.Issue.Comment.Delete(owner, repo, commentId);
        }
        catch (NotFoundException)
        {
            // Comment already deleted; define error out of existence.
        }
    }

    public async Task UpdateCommentAsync(string owner, string repo, long commentId, string comment, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        await _client.Issue.Comment.Update(owner, repo, commentId, comment);
    }

    public async Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        return await _client.Issue.Comment.GetAllForIssue(owner, repo, number, new ApiOptions { PageSize = 100 });
    }

    public async Task<GitHubRateLimitSnapshot> GetRateLimitSnapshotAsync(CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        try
        {
            await EnsureAuthenticatedAsync(ct);
            var rateLimits = await _client.RateLimit.GetRateLimits();
            return new GitHubRateLimitSnapshot(
                CoreRemaining: rateLimits?.Rate?.Remaining,
                CoreReset: rateLimits?.Rate?.Reset,
                SearchRemaining: rateLimits?.Resources?.Search?.Remaining,
                SearchReset: rateLimits?.Resources?.Search?.Reset);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuthorizationException authEx)
        {
            _logger?.LogError(authEx, "Authentication failure while requesting GitHub rate limit quota.");
            return new GitHubRateLimitSnapshot(null, null, null, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to query GitHub rate limits due to network or API error.");
            var lastInfo = _client.GetLastApiInfo()?.RateLimit;
            return new GitHubRateLimitSnapshot(
                CoreRemaining: lastInfo?.Remaining,
                CoreReset: lastInfo?.Reset,
                SearchRemaining: null,
                SearchReset: null);
        }
    }

    public async Task<IReadOnlyList<(long Id, string Name, long SizeInBytes)>> ListWorkflowRunArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var response = await _client.Actions.Artifacts.ListWorkflowArtifacts(owner, repo, runId);
            if (response?.Artifacts == null)
                return Array.Empty<(long Id, string Name, long SizeInBytes)>();

            return response.Artifacts
                .Select(a => (a.Id, a.Name, (long)a.SizeInBytes))
                .ToList();
        }
        catch (NotFoundException)
        {
            _logger?.LogDebug("Workflow run {RunId} not found or has no artifacts for {Owner}/{Repo}", runId, owner, repo);
            return Array.Empty<(long Id, string Name, long SizeInBytes)>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to list artifacts for workflow run {RunId} in {Owner}/{Repo}", runId, owner, repo);
            return Array.Empty<(long Id, string Name, long SizeInBytes)>();
        }
    }

    public async Task<Stream> DownloadArtifactZipStreamAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            return await _client.Actions.Artifacts.DownloadArtifact(owner, repo, artifactId, "zip");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to download artifact {ArtifactId} for {Owner}/{Repo}", artifactId, owner, repo);
            throw;
        }
    }

    public async Task<string?> GetRefShaAsync(
        string owner, string repo, string branchName, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var refPath = branchName.StartsWith("heads/", StringComparison.OrdinalIgnoreCase)
            ? branchName
            : $"heads/{branchName.TrimStart('/')}";

        try
        {
            var reference = await _client.Git.Reference.Get(owner, repo, refPath);
            return reference?.Object?.Sha;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task CreateOrUpdateRefAsync(
        string owner, string repo, string branchName, string commitSha, bool force = true, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var cleanBranch = branchName.TrimStart('/');
        if (cleanBranch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            cleanBranch = cleanBranch.Substring("refs/heads/".Length);
        else if (cleanBranch.StartsWith("heads/", StringComparison.OrdinalIgnoreCase))
            cleanBranch = cleanBranch.Substring("heads/".Length);

        var fullRefName = $"refs/heads/{cleanBranch}";
        var subRef = $"heads/{cleanBranch}";

        try
        {
            var existing = await _client.Git.Reference.Get(owner, repo, subRef);
            if (existing != null)
            {
                await _client.Git.Reference.Update(owner, repo, subRef, new ReferenceUpdate(commitSha, force));
                return;
            }
        }
        catch (NotFoundException)
        {
            // Fall through to create
        }

        await _client.Git.Reference.Create(owner, repo, new NewReference(fullRefName, commitSha));
    }

    public async Task DeleteRefAsync(
        string owner, string repo, string branchName, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var cleanBranch = branchName.TrimStart('/');
        if (cleanBranch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            cleanBranch = cleanBranch.Substring("refs/heads/".Length);
        else if (cleanBranch.StartsWith("heads/", StringComparison.OrdinalIgnoreCase))
            cleanBranch = cleanBranch.Substring("heads/".Length);

        try
        {
            await _client.Git.Reference.Delete(owner, repo, $"heads/{cleanBranch}");
        }
        catch (NotFoundException)
        {
            // Idempotent delete
        }
    }

    public async Task<string> UpdateFileContentAsync(
        string owner, string repo, string path, string message, string content, string branch, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var existing = await _client.Repository.Content.GetAllContentsByRef(owner, repo, path, branch);
            var existingSha = existing != null && existing.Count > 0 ? existing[0].Sha : null;

            if (!string.IsNullOrEmpty(existingSha))
            {
                var updateResult = await _client.Repository.Content.UpdateFile(
                    owner, repo, path, new UpdateFileRequest(message, content, existingSha, branch));
                return updateResult.Commit.Sha;
            }
        }
        catch (NotFoundException)
        {
            // File does not exist yet on branch, create it
        }

        var createResult = await _client.Repository.Content.CreateFile(
            owner, repo, path, new CreateFileRequest(message, content, branch));
        return createResult.Commit.Sha;
    }

    public async Task<RepositoryFileContent?> GetFileContentWithShaAsync(
        string owner, string repo, string path, string reference, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<RepositoryContent>? existing;
        try
        {
            existing = await _client.Repository.Content.GetAllContentsByRef(owner, repo, path, reference);
        }
        catch (NotFoundException)
        {
            return null;
        }

        if (existing == null || existing.Count == 0 || string.IsNullOrWhiteSpace(existing[0].Sha))
        {
            return null;
        }

        var content = existing[0].Content;
        if (string.IsNullOrEmpty(content))
        {
            var raw = await _client.Repository.Content.GetRawContentByRef(owner, repo, path, reference);
            content = raw != null ? Encoding.UTF8.GetString(raw) : string.Empty;
        }

        return new RepositoryFileContent(content, existing[0].Sha);
    }

    public async Task<string> UpdateFileContentIfUnchangedAsync(
        string owner, string repo, string path, string message, string content, string branch, string expectedBlobSha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedBlobSha))
        {
            throw new ArgumentException("Expected blob SHA must be provided for optimistic file updates.", nameof(expectedBlobSha));
        }

        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        // Enforce compare-and-swap directly with caller's expectedBlobSha; never re-fetch latest blob SHA here.
        var updateResult = await _client.Repository.Content.UpdateFile(
            owner, repo, path, new UpdateFileRequest(message, content, expectedBlobSha, branch));
        return updateResult.Commit.Sha;
    }

    public async Task<PullRequest?> FindOpenPullRequestByHeadAsync(
        string owner, string repo, string headBranch, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var cleanHead = headBranch.TrimStart('/');
        if (cleanHead.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            cleanHead = cleanHead.Substring("refs/heads/".Length);
        else if (cleanHead.StartsWith("heads/", StringComparison.OrdinalIgnoreCase))
            cleanHead = cleanHead.Substring("heads/".Length);

        var request = new PullRequestRequest
        {
            State = ItemStateFilter.Open,
            Head = $"{owner}:{cleanHead}"
        };

        var prs = await _client.PullRequest.GetAllForRepository(owner, repo, request);
        return prs != null && prs.Count > 0 ? prs[0] : null;
    }

    public async Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsByHeadPrefixAsync(
        string owner, string repo, string branchPrefix, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(branchPrefix))
        {
            return Array.Empty<PullRequest>();
        }

        const int perPage = 100;
        const int maxPages = 5;
        var expectedHeadRepo = $"{owner}/{repo}";
        var matched = new List<PullRequest>();
        var request = new PullRequestRequest { State = ItemStateFilter.Open };

        for (int page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var pageItems = await _client.PullRequest.GetAllForRepository(
                owner,
                repo,
                request,
                new ApiOptions { PageSize = perPage, PageCount = 1, StartPage = page });

            if (pageItems == null || pageItems.Count == 0)
            {
                break;
            }

            foreach (var pr in pageItems)
            {
                var headRepo = pr.Head?.Repository?.FullName;
                var headRef = pr.Head?.Ref;
                if (headRepo != null &&
                    string.Equals(headRepo, expectedHeadRepo, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(headRef) &&
                    headRef.StartsWith(branchPrefix, StringComparison.Ordinal))
                {
                    matched.Add(pr);
                }
            }

            if (pageItems.Count < perPage)
            {
                break;
            }
        }

        return matched;
    }

    public async Task<IReadOnlyList<PullRequest>> FindOpenPullRequestsByHeadShaAsync(
        string owner, string repo, string? headOwner, string? headBranch, string headSha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(headSha))
        {
            return Array.Empty<PullRequest>();
        }

        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(headOwner) && !string.IsNullOrWhiteSpace(headBranch))
        {
            var cleanBranch = headBranch.TrimStart('/');
            if (cleanBranch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            {
                cleanBranch = cleanBranch.Substring("refs/heads/".Length);
            }
            else if (cleanBranch.StartsWith("heads/", StringComparison.OrdinalIgnoreCase))
            {
                cleanBranch = cleanBranch.Substring("heads/".Length);
            }

            var headPrs = await _client.PullRequest.GetAllForRepository(
                owner,
                repo,
                new PullRequestRequest
                {
                    State = ItemStateFilter.Open,
                    Head = $"{headOwner}:{cleanBranch}"
                });

            var directMatches = headPrs?
                .Where(pr => string.Equals(pr.Head?.Sha, headSha, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (directMatches is { Count: > 0 })
            {
                return directMatches;
            }
        }

        var searchQuery = $"repo:{owner}/{repo} is:pr is:open {headSha}";
        var searchResult = await _client.Search.SearchIssues(new SearchIssuesRequest(searchQuery)
        {
            Page = 1,
            PerPage = 10
        });

        var fallbackMatches = new List<PullRequest>();
        if (searchResult?.Items == null || searchResult.Items.Count == 0)
        {
            return fallbackMatches;
        }

        foreach (var item in searchResult.Items)
        {
            ct.ThrowIfCancellationRequested();
            var fullPr = await GetPullRequestAsync(owner, repo, item.Number, ct);
            if (fullPr != null &&
                string.Equals(fullPr.State.StringValue, "open", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fullPr.Head?.Sha, headSha, StringComparison.OrdinalIgnoreCase))
            {
                fallbackMatches.Add(fullPr);
            }
        }

        return fallbackMatches;
    }

    public async Task<PullRequest> CreatePullRequestAsync(
        string owner, string repo, string title, string head, string baseBranch, string body, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var newPr = new NewPullRequest(title, head, baseBranch)
        {
            Body = body
        };

        return await _client.PullRequest.Create(owner, repo, newPr);
    }

    public async Task<PullRequest> UpdatePullRequestAsync(
        string owner, string repo, int prNumber, string title, string body, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var update = new PullRequestUpdate
        {
            Title = title,
            Body = body
        };

        return await _client.PullRequest.Update(owner, repo, prNumber, update);
    }

    public async Task<Stream> DownloadArtifactAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default)
    {
        return await DownloadArtifactZipStreamAsync(owner, repo, artifactId, ct);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListWorkflowRunsAsync(
        string owner, string repo, string? branch = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var request = new WorkflowRunsRequest();
        if (!string.IsNullOrWhiteSpace(branch))
        {
            request.Branch = branch;
        }

        var response = await _client.Actions.Workflows.Runs.List(owner, repo, request);
        return response?.WorkflowRuns ?? (IReadOnlyList<WorkflowRun>)Array.Empty<WorkflowRun>();
    }

    public async Task<IReadOnlyList<PullRequest>> SearchMergedPullRequestsAsync(
        string owner, string repo, string author, DateTimeOffset updatedAfter, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var dateStr = updatedAfter.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        // Search merged auto-update PRs by label and optionally by author to support both GitHub App and PAT
        var authorFilter = !string.IsNullOrWhiteSpace(author) ? $"author:{author} " : "";
        var query = $"repo:{owner}/{repo} is:pr is:merged label:\"{GitHubLabels.AutoUpdate}\" {authorFilter}updated:>{dateStr}";

        const int perPage = 100;
        const int maxPages = 3;
        var seenPrNumbers = new HashSet<int>();
        var prList = new List<PullRequest>();

        for (int page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var searchResult = await _client.Search.SearchIssues(new SearchIssuesRequest(query)
            {
                Page = page,
                PerPage = perPage
            });

            if (searchResult?.Items == null || searchResult.Items.Count == 0)
                break;

            foreach (var issue in searchResult.Items)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (!seenPrNumbers.Add(issue.Number))
                    continue;

                var pr = await GetPullRequestAsync(owner, repo, issue.Number, ct);
                if (pr != null)
                {
                    prList.Add(pr);
                }
            }

            if (searchResult.Items.Count < perPage)
                break;
        }

        return prList;
    }

    public async Task<BranchCommitInfo?> GetBranchLatestCommitAsync(
        string owner, string repo, string branch, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var branchModel = await _client.Repository.Branch.Get(owner, repo, branch);
            if (branchModel?.Commit?.Sha == null)
                return null;

            var commit = await _client.Repository.Commit.Get(owner, repo, branchModel.Commit.Sha);
            if (commit == null)
                return null;

            var sha = commit.Sha;
            var msg = commit.Commit?.Message ?? string.Empty;
            var authorLogin = commit.Author?.Login ?? string.Empty;
            var authorEmail = commit.Commit?.Author?.Email ?? string.Empty;
            var committerLogin = commit.Committer?.Login ?? string.Empty;
            var committerEmail = commit.Commit?.Committer?.Email ?? string.Empty;

            return new BranchCommitInfo(sha, msg, authorLogin, authorEmail, committerLogin, committerEmail);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<TreeResponse?> GetTreeRecursiveAsync(
        string owner, string repo, string branchOrSha, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            return await _client.Git.Tree.GetRecursive(owner, repo, branchOrSha);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream?> OpenRepositoryTarballStreamAsync(
        string owner,
        string repo,
        string reference,
        long maxArchiveBytes = 0,
        CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var configuredBase = _client.Connection?.BaseAddress ?? new Uri("https://api.github.com/");
        var normalizedBase = configuredBase.AbsoluteUri.EndsWith('/')
            ? configuredBase
            : new Uri(configuredBase.AbsoluteUri + "/");
        var escapedRef = string.Join("/", reference.Split('/').Select(Uri.EscapeDataString));
        var requestUri = new Uri(normalizedBase, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/tarball/{escapedRef}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("abaci-bot");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        var token = _cachedToken ?? _client.Connection?.Credentials?.GetToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await _archiveHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Failed to open repository tarball stream for {Owner}/{Repo}@{Reference}: HTTP {StatusCode}",
                    owner, repo, reference, (int)response.StatusCode);
                response.Dispose();
                return null;
            }

            if (maxArchiveBytes > 0 &&
                response.Content.Headers.ContentLength is long contentLength &&
                contentLength > maxArchiveBytes)
            {
                _logger?.LogWarning(
                    "Repository tarball Content-Length for {Owner}/{Repo}@{Reference} ({ContentLength} bytes) exceeds MaxArchiveBytes ({LimitBytes}); rejecting before reading body",
                    owner, repo, reference, contentLength, maxArchiveBytes);
                response.Dispose();
                return null;
            }

            var rawStream = await response.Content.ReadAsStreamAsync(ct);
            var bounded = new BoundedReadStream(
                rawStream,
                maxArchiveBytes > 0 ? maxArchiveBytes : long.MaxValue,
                leaveOpen: false,
                ownedResource: response);
            response = null;
            return bounded;
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            _logger?.LogWarning(ex, "Failed to download repository tarball stream for {Owner}/{Repo}@{Reference}", owner, repo, reference);
            return null;
        }
    }

    public async Task<IReadOnlyList<Artifact>> ListWorkflowArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var response = await _client.Actions.Artifacts.ListWorkflowArtifacts(owner, repo, runId);
            return response?.Artifacts ?? (IReadOnlyList<Artifact>)Array.Empty<Artifact>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list artifacts for workflow run {RunId} in {Owner}/{Repo}", runId, owner, repo);
            return Array.Empty<Artifact>();
        }
    }

    public async Task EnsureLabelsExistAsync(
        string owner, string repo, IEnumerable<(string Name, string Color, string Description)> labels, CancellationToken ct = default)
    {
        if (!await EnsureLabelsInternalAsync(owner, repo, labels, ct))
        {
            throw new InvalidOperationException($"Failed to ensure required labels exist in '{owner}/{repo}'.");
        }
    }

    public Task<bool> EnsureRequiredLabelsVerifiedAsync(
        string owner, string repo, IEnumerable<(string Name, string Color, string Description)> labels, CancellationToken ct = default)
    {
        return EnsureLabelsInternalAsync(owner, repo, labels, ct);
    }

    private async Task<bool> EnsureLabelsInternalAsync(
        string owner, string repo, IEnumerable<(string Name, string Color, string Description)> labels, CancellationToken ct)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<Label> existingLabels;
        try
        {
            existingLabels = await _client.Issue.Labels.GetAllForRepository(owner, repo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to query existing labels for {Owner}/{Repo}", owner, repo);
            return false;
        }

        var existingSet = new HashSet<string>(existingLabels.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        bool allEnsured = true;

        foreach (var (name, color, description) in labels)
        {
            if (existingSet.Contains(name))
                continue;

            try
            {
                var newLabel = new NewLabel(name, color.TrimStart('#'))
                {
                    Description = description
                };
                await _client.Issue.Labels.Create(owner, repo, newLabel);
                _logger?.LogInformation("Successfully created missing label '{Label}' in {Owner}/{Repo}", name, owner, repo);
                existingSet.Add(name);
            }
            catch (ApiValidationException ex)
            {
                try
                {
                    var refreshed = await _client.Issue.Labels.GetAllForRepository(owner, repo);
                    if (refreshed.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        existingSet.Add(name);
                        continue;
                    }
                }
                catch
                {
                    // Fall through to fail-closed logging below.
                }

                allEnsured = false;
                _logger?.LogWarning(ex, "Failed to verify/create label '{Label}' in {Owner}/{Repo}", name, owner, repo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                allEnsured = false;
                _logger?.LogWarning(ex, "Failed to create label '{Label}' in {Owner}/{Repo}", name, owner, repo);
            }
        }

        return allEnsured;
    }

    public async Task<IReadOnlyList<Milestone>> GetMilestonesAsync(
        string owner, string repo, ItemStateFilter state = ItemStateFilter.All, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var request = new MilestoneRequest { State = state };
        return await _client.Issue.Milestone.GetAllForRepository(owner, repo, request);
    }

    public async Task<Milestone> CreateMilestoneAsync(
        string owner, string repo, string title, DateTimeOffset? dueOn = null, string? description = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var newMilestone = new NewMilestone(title)
        {
            DueOn = dueOn,
            Description = description
        };

        var created = await _client.Issue.Milestone.Create(owner, repo, newMilestone);

        if (dueOn.HasValue && created.DueOn.HasValue && created.DueOn.Value.Date != dueOn.Value.Date)
        {
            try
            {
                var update = new MilestoneUpdate
                {
                    DueOn = dueOn.Value
                };
                created = await _client.Issue.Milestone.Update(owner, repo, created.Number, update);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to calibrate milestone due date for {Owner}/{Repo}#{Number}", owner, repo, created.Number);
            }
        }

        return created;
    }

    public async Task<Milestone> UpdateMilestoneAsync(
        string owner, string repo, int milestoneNumber, string title, DateTimeOffset? dueOn = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var update = new MilestoneUpdate
        {
            Title = title,
            DueOn = dueOn
        };

        return await _client.Issue.Milestone.Update(owner, repo, milestoneNumber, update);
    }

    public async Task SetIssueMilestoneAsync(
        string owner, string repo, int issueNumber, int? milestoneNumber, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var update = new IssueUpdate
        {
            Milestone = milestoneNumber
        };

        await _client.Issue.Update(owner, repo, issueNumber, update);
    }

    public async Task<GitHubSearchPageResult<Issue>> SearchMergedPullRequestsWithoutMilestoneAsync(
        string owner, string repo, DateTimeOffset mergedAfter, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var dateStr = mergedAfter.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var query = $"repo:{owner}/{repo} is:pr is:merged no:milestone merged:>={dateStr}";

        return await SearchIssuesPaginatedAsync(
            owner,
            repo,
            query,
            IssueTypeQualifier.PullRequest,
            nameof(SearchMergedPullRequestsWithoutMilestoneAsync),
            ct);
    }

    public async Task LockIssueAsync(
        string owner, string repo, int issueNumber, LockReason? lockReason = LockReason.TooHeated, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        await _client.Issue.LockUnlock.Lock(owner, repo, issueNumber, lockReason);
    }

    public async Task UnlockIssueAsync(
        string owner, string repo, int issueNumber, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            await _client.Issue.LockUnlock.Unlock(owner, repo, issueNumber);
        }
        catch (NotFoundException)
        {
            // Idempotent: ignore if already unlocked
        }
    }

    public async Task<bool> IsIssueLockedAsync(
        string owner, string repo, int issueNumber, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var issue = await GetIssueAsync(owner, repo, issueNumber, ct);
        return issue?.Locked ?? false;
    }

    public async Task<bool> MinimizeCommentAsync(
        string owner, string repo, long commentId, string? subjectNodeId = null, string classifier = "SPAM", CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var nodeId = subjectNodeId;
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            var comment = await _client.Issue.Comment.Get(owner, repo, commentId);
            nodeId = comment?.NodeId;
        }

        if (string.IsNullOrWhiteSpace(nodeId))
        {
            _logger?.LogWarning("Cannot minimize comment {CommentId}: nodeId unavailable", commentId);
            return false;
        }

        const string mutation = """
            mutation($subjectId: ID!, $classifier: ReportedContentClassifiers!) {
              minimizeComment(input: { subjectId: $subjectId, classifier: $classifier }) {
                minimizedComment {
                  isMinimized
                }
              }
            }
            """;

        var variables = new Dictionary<string, object>
        {
            ["subjectId"] = nodeId,
            ["classifier"] = classifier
        };

        try
        {
            var graphQlError = await ExecuteGraphQlMutationAsync(mutation, variables, ct);
            if (!string.IsNullOrEmpty(graphQlError))
            {
                _logger?.LogWarning("GraphQL error minimizing comment {CommentId} (NodeId: {NodeId}): {Error}", commentId, nodeId, graphQlError);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to minimize comment {CommentId} (NodeId: {NodeId})", commentId, nodeId);
            return false;
        }
    }

    public async Task ArchivePullRequestAsync(
        string owner, string repo, int prNumber, string? pullRequestNodeId = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var nodeId = pullRequestNodeId;
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            var pr = await GetPullRequestAsync(owner, repo, prNumber, ct);
            nodeId = pr?.NodeId;
        }

        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new InvalidOperationException($"Cannot archive PR #{prNumber}: nodeId unavailable");
        }

        const string mutation = """
            mutation($pullRequestId: ID!) {
              archivePullRequest(input: { pullRequestId: $pullRequestId }) {
                pullRequest {
                  id
                }
              }
            }
            """;

        var variables = new Dictionary<string, object>
        {
            ["pullRequestId"] = nodeId
        };

        var graphQlError = await ExecuteGraphQlMutationAsync(mutation, variables, ct);
        if (!string.IsNullOrEmpty(graphQlError))
        {
            throw new InvalidOperationException($"GraphQL server error archiving PR #{prNumber}: {graphQlError}");
        }
    }

    public async Task UnarchivePullRequestAsync(
        string owner, string repo, int prNumber, string? pullRequestNodeId = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var nodeId = pullRequestNodeId;
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            var pr = await GetPullRequestAsync(owner, repo, prNumber, ct);
            nodeId = pr?.NodeId;
        }

        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new InvalidOperationException($"Cannot unarchive PR #{prNumber}: nodeId unavailable");
        }

        const string mutation = """
            mutation($pullRequestId: ID!) {
              unarchivePullRequest(input: { pullRequestId: $pullRequestId }) {
                pullRequest {
                  id
                }
              }
            }
            """;

        var variables = new Dictionary<string, object>
        {
            ["pullRequestId"] = nodeId
        };

        var graphQlError = await ExecuteGraphQlMutationAsync(mutation, variables, ct);
        if (!string.IsNullOrEmpty(graphQlError))
        {
            throw new InvalidOperationException($"GraphQL server error unarchiving PR #{prNumber}: {graphQlError}");
        }
    }

    private async Task<string?> ExecuteGraphQlMutationAsync(
        string mutation,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        // Dictionary<string, object> preserves exact camelCase GraphQL variable names;
        // Octokit's SimpleJsonSerializer converts anonymous-type property names to snake_case.
        var payload = new Dictionary<string, object>
        {
            ["query"] = mutation,
            ["variables"] = variables
        };

        var response = await _client.Connection.Post<object>(
            new Uri("graphql", UriKind.Relative),
            payload,
            "application/json",
            "application/json",
            (IDictionary<string, string>?)null,
            ct);

        return TryGetGraphQlErrorMessage(response);
    }

    public async Task<GitHubSearchPageResult<Issue>> SearchClosedPullRequestsAsync(
        string owner, string repo, DateTimeOffset closedBefore, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var dateStr = closedBefore.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var query = $"repo:{owner}/{repo} is:pr is:closed is:unlocked closed:<{dateStr}";

        return await SearchIssuesPaginatedAsync(
            owner,
            repo,
            query,
            IssueTypeQualifier.PullRequest,
            nameof(SearchClosedPullRequestsAsync),
            ct);
    }

    public async Task<RepositoryAccessLevel> GetUserPermissionAsync(
        string owner, string repo, string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return RepositoryAccessLevel.None;
        }

        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var response = await _client.Repository.Collaborator.ReviewPermission(owner, repo, username.Trim());
            if (response == null)
            {
                return RepositoryAccessLevel.None;
            }

            return MapPermissionLevel(response.RoleName) ??
                   MapPermissionLevel(response.Permission) ??
                   RepositoryAccessLevel.None;
        }
        catch (NotFoundException)
        {
            return RepositoryAccessLevel.None;
        }
    }

    public async Task VerifyCollaboratorPermissionProbeAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(_botPat))
        {
            // Verify repository visibility and push+ access directly before ReviewPermission so a
            // read-only/triage PAT on a public repo or an invisible private repo fails immediately.
            var repoInfo = await _client.Repository.Get(owner, repo);
            ct.ThrowIfCancellationRequested();
            if (repoInfo?.Permissions != null &&
                !(repoInfo.Permissions.Push || repoInfo.Permissions.Maintain || repoInfo.Permissions.Admin))
            {
                throw new InvalidOperationException(
                    $"Configured BotPat lacks push/maintain/admin permission on '{owner}/{repo}' required to query collaborator permissions.");
            }
        }

        var probeUser = owner;
        try
        {
            var resolved = await GetBotLoginAsync(ct);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                var trimmedResolved = resolved.Trim();
                // In PAT mode, always probe a distinct login so a triage-only PAT cannot pass by checking itself.
                probeUser = !string.IsNullOrWhiteSpace(_botPat)
                    ? (string.Equals(owner, trimmedResolved, StringComparison.OrdinalIgnoreCase) ? "ghost" : owner)
                    : trimmedResolved;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            probeUser = owner;
        }

        try
        {
            await _client.Repository.Collaborator.ReviewPermission(owner, repo, probeUser);
        }
        catch (NotFoundException)
        {
            // HTTP 404 confirms the ReviewPermission endpoint is reachable with sufficient permission;
            // lacking collaborator permission fails with HTTP 403 Forbidden.
        }
    }

    public async Task VerifyArchivePermissionProbeAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(_botPat))
        {
            await EnsureAuthenticatedAsync(ct);
            ct.ThrowIfCancellationRequested();

            var repoInfo = await _client.Repository.Get(owner, repo);
            var perms = repoInfo?.Permissions;
            if (perms == null || !(perms.Triage || perms.Push || perms.Maintain || perms.Admin))
            {
                throw new InvalidOperationException(
                    $"Configured BotPat lacks triage+ permission on '{owner}/{repo}' required for ConversationGuard:ArchiveEnabled=true.");
            }

            return;
        }

        throw new InvalidOperationException(
            $"ConversationGuard:ArchiveEnabled=true is not supported in GitHub App-only mode for '{owner}/{repo}'. " +
            "GitHub's archivePullRequest/unarchivePullRequest GraphQL mutations require a user role (triage or higher) " +
            "and always return FORBIDDEN for GitHub App installation tokens even when Administration: Read & write is granted. " +
            "Set ConversationGuard:ArchiveEnabled=false (recommended) or configure a triage+ user PAT via PackageUpdate:BotPat.");
    }

    public async Task<string> VerifyPackageUpdateWritePermissionAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(_botPat))
        {
            await EnsureAuthenticatedAsync(ct);
            ct.ThrowIfCancellationRequested();

            Repository? repoInfo = null;
            IReadOnlyDictionary<string, string>? responseHeaders = null;
            try
            {
                var apiResponse = await _client.Connection.Get<Repository>(
                    ApiUrls.Repository(owner, repo),
                    new Dictionary<string, string>(),
                    "application/vnd.github+json",
                    ct);
                repoInfo = apiResponse?.Body;
                responseHeaders = apiResponse?.HttpResponse?.Headers;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not ApiException)
            {
                // Fall back to IRepositoriesClient.Get when unit tests mock only Repository.Get
            }

            repoInfo ??= await _client.Repository.Get(owner, repo);
            var perms = repoInfo?.Permissions;
            if (perms == null || !(perms.Push || perms.Maintain || perms.Admin))
            {
                throw new InvalidOperationException(
                    $"Configured PackageUpdate:BotPat lacks write access (push+) on '{owner}/{repo}' required for PackageUpdate:Enabled=true && DryRun=false.");
            }

            var oauthScopes = TryGetHeaderIgnoreCase(responseHeaders, "X-OAuth-Scopes");
            if (!string.IsNullOrWhiteSpace(oauthScopes))
            {
                var scopes = oauthScopes
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hasClassicRepoScope = scopes.Contains("repo") || (!repoInfo!.Private && scopes.Contains("public_repo"));
                if (!hasClassicRepoScope)
                {
                    var expectedScope = repoInfo!.Private ? "repo" : "repo (or public_repo)";
                    throw new InvalidOperationException(
                        $"Configured classic PackageUpdate:BotPat is missing required OAuth scope '{expectedScope}' on '{owner}/{repo}' (actual X-OAuth-Scopes='{oauthScopes}').");
                }

                await VerifyBotPatResourceReadableAsync(
                    "Actions: Read-only",
                    () => _client.Actions.Workflows.Runs.List(
                        owner, repo, new WorkflowRunsRequest(), new ApiOptions { PageSize = 1, PageCount = 1 }),
                    owner,
                    repo);
                return "verified";
            }

            // Fine-grained PATs do not emit X-OAuth-Scopes and expose no non-mutating write-scope introspection API;
            // probe zero-side-effect read access across all four required resources and surface honest "partial" status.
            await VerifyBotPatResourceReadableAsync(
                "Actions: Read-only",
                () => _client.Actions.Workflows.Runs.List(
                    owner, repo, new WorkflowRunsRequest(), new ApiOptions { PageSize = 1, PageCount = 1 }),
                owner,
                repo);
            await VerifyBotPatResourceReadableAsync(
                "Pull requests: Read and write",
                () => _client.PullRequest.GetAllForRepository(
                    owner, repo, new PullRequestRequest { State = ItemStateFilter.All }, new ApiOptions { PageSize = 1, PageCount = 1 }),
                owner,
                repo);
            await VerifyBotPatResourceReadableAsync(
                "Issues: Read and write",
                () => _client.Issue.Labels.GetAllForRepository(
                    owner, repo, new ApiOptions { PageSize = 1, PageCount = 1 }),
                owner,
                repo);
            await VerifyBotPatResourceReadableAsync(
                "Contents: Read and write",
                async () =>
                {
                    try
                    {
                        await _client.Repository.Content.GetAllContents(owner, repo);
                    }
                    catch (NotFoundException)
                    {
                        // Empty repository returns 404 rather than 403 Forbidden
                    }
                },
                owner,
                repo);

            _logger?.LogWarning(
                "PackageUpdate:BotPat on '{Owner}/{Repo}' passed push+ and four zero-side-effect read probes (Actions, Pull requests, Issues, Contents), " +
                "but GitHub REST API provides no non-mutating write-scope introspection for fine-grained PATs; reporting 'partial' capability status.",
                owner,
                repo);
            return "partial";
        }

        Installation? installation;
        if (_jwtClientFactory != null)
        {
            var jwtClient = _jwtClientFactory(new Credentials(GenerateJwt(), AuthenticationType.Bearer));
            installation = await jwtClient.GitHubApps.GetRepositoryInstallationForCurrent(owner, repo);
        }
        else
        {
            await _authLock.WaitAsync(ct);
            try
            {
                var priorCredentials = _client.Connection.Credentials;
                try
                {
                    _client.Connection.Credentials = new Credentials(GenerateJwt(), AuthenticationType.Bearer);
                    installation = await _client.GitHubApps.GetRepositoryInstallationForCurrent(owner, repo);
                }
                finally
                {
                    _client.Connection.Credentials = priorCredentials;
                }
            }
            finally
            {
                _authLock.Release();
            }
        }

        var installPerms = installation?.Permissions;
        var checks = new (string ScopeName, string? Actual, bool RequireWrite)[]
        {
            ("Contents", installPerms?.Contents?.StringValue, true),
            ("Pull requests", installPerms?.PullRequests?.StringValue, true),
            ("Issues", installPerms?.Issues?.StringValue, true),
            ("Actions", installPerms?.Actions?.StringValue, false)
        };

        var missing = new List<string>();
        foreach (var (scopeName, actual, requireWrite) in checks)
        {
            var ok = requireWrite
                ? string.Equals(actual, "write", StringComparison.OrdinalIgnoreCase)
                : string.Equals(actual, "read", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(actual, "write", StringComparison.OrdinalIgnoreCase);

            if (!ok)
            {
                var expected = requireWrite ? "write" : "read";
                missing.Add($"{scopeName}='{actual ?? "none"}' (expected '{expected}')");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"GitHub App installation on '{owner}/{repo}' is missing required PackageUpdate permissions: {string.Join("; ", missing)}. " +
                "Grant 'Repository -> Contents: Read and write', 'Pull requests: Read and write', 'Issues: Read and write', and 'Actions: Read-only' " +
                "on the GitHub App and accept updated permissions on the installation before running PackageUpdate with DryRun=false.");
        }

        return "verified";
    }

    private static string? TryGetHeaderIgnoreCase(IReadOnlyDictionary<string, string>? headers, string headerName)
    {
        if (headers == null)
        {
            return null;
        }

        foreach (var kv in headers)
        {
            if (string.Equals(kv.Key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }

        return null;
    }

    private static async Task VerifyBotPatResourceReadableAsync(
        string requiredPermission,
        Func<Task> probe,
        string owner,
        string repo)
    {
        try
        {
            await probe();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Configured PackageUpdate:BotPat failed read probe for '{requiredPermission}' on '{owner}/{repo}' ({ex.Message}). " +
                $"Grant '{requiredPermission}' on PackageUpdate:BotPat.",
                ex);
        }
    }

    public async Task VerifyBotPatTeamReadPermissionAsync(
        string owner, string repo, string teamName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_botPat) || string.IsNullOrWhiteSpace(teamName))
        {
            return;
        }

        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var repoInfo = await _client.Repository.Get(owner, repo);
        ct.ThrowIfCancellationRequested();

        if (repoInfo?.Owner?.Type == AccountType.User)
        {
            _logger?.LogWarning(
                "Repository '{Owner}/{Repo}' is owned by a personal user account rather than an organization; skipping BotPat team lookup probe for '{TeamName}'.",
                owner, repo, teamName);
            return;
        }

        try
        {
            await GetTeamMembersAsync(owner, teamName, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Configured PackageUpdate:BotPat failed to read organization team '{owner}/{teamName}' ({ex.Message}). " +
                "Because PackageUpdate:BotPat switches all GitHub API calls globally, it must include classic 'read:org' " +
                "or fine-grained 'Organization -> Members: Read-only' permission in addition to repository scopes so WorkflowStateModule does not block issue_comment commands.",
                ex);
        }
    }

    public async Task<string> GetBotLoginAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedBotLogin))
        {
            return _cachedBotLogin;
        }

        await _botLoginLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedBotLogin))
            {
                return _cachedBotLogin;
            }

            if (_jwtClientFactory != null)
            {
                string? isolatedLogin;
                if (!string.IsNullOrWhiteSpace(_botPat))
                {
                    var patClient = _jwtClientFactory(new Credentials(_botPat));
                    var currentUser = await patClient.User.Current();
                    isolatedLogin = currentUser?.Login;
                }
                else
                {
                    var jwtClient = _jwtClientFactory(new Credentials(GenerateJwt(), AuthenticationType.Bearer));
                    var app = await jwtClient.GitHubApps.GetCurrent();
                    isolatedLogin = app?.Slug;
                }

                if (string.IsNullOrWhiteSpace(isolatedLogin))
                {
                    throw new InvalidOperationException("GitHub API returned an empty bot login/slug.");
                }

                _cachedBotLogin = isolatedLogin.Trim();
                return _cachedBotLogin;
            }

            await _authLock.WaitAsync(cancellationToken);
            try
            {
                var priorCredentials = _client.Connection.Credentials;
                try
                {
                    string? resolvedLogin;
                    if (!string.IsNullOrWhiteSpace(_botPat))
                    {
                        _client.Connection.Credentials = new Credentials(_botPat);
                        var currentUser = await _client.User.Current();
                        resolvedLogin = currentUser?.Login;
                    }
                    else
                    {
                        var jwt = GenerateJwt();
                        _client.Connection.Credentials = new Credentials(jwt, AuthenticationType.Bearer);
                        var app = await _client.GitHubApps.GetCurrent();
                        resolvedLogin = app?.Slug;
                    }

                    if (string.IsNullOrWhiteSpace(resolvedLogin))
                    {
                        throw new InvalidOperationException("GitHub API returned an empty bot login/slug.");
                    }

                    _cachedBotLogin = resolvedLogin.Trim();
                    return _cachedBotLogin;
                }
                finally
                {
                    _client.Connection.Credentials = priorCredentials;
                }
            }
            finally
            {
                _authLock.Release();
            }
        }
        finally
        {
            _botLoginLock.Release();
        }
    }

    private static RepositoryAccessLevel? MapPermissionLevel(string? rawLevel)
    {
        if (string.IsNullOrWhiteSpace(rawLevel))
        {
            return null;
        }

        return rawLevel.Trim().ToLowerInvariant() switch
        {
            "admin" => RepositoryAccessLevel.Admin,
            "maintain" => RepositoryAccessLevel.Maintain,
            "write" => RepositoryAccessLevel.Write,
            "triage" => RepositoryAccessLevel.Triage,
            "read" => RepositoryAccessLevel.Read,
            "none" => RepositoryAccessLevel.None,
            _ => null
        };
    }

    private static string? TryGetGraphQlErrorMessage(IApiResponse<object>? response)
    {
        if (response == null)
        {
            return null;
        }

        if (response.Body is IDictionary<string, object> dict &&
            dict.TryGetValue("errors", out var errorsVal) &&
            errorsVal != null)
        {
            return errorsVal.ToString();
        }

        var rawBody = response.HttpResponse?.Body as string ?? response.Body as string;
        if (string.IsNullOrWhiteSpace(rawBody) && response.Body is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("errors", out var errorsProp) &&
                errorsProp.ValueKind == JsonValueKind.Array &&
                errorsProp.GetArrayLength() > 0)
            {
                return errorsProp.GetRawText();
            }
            return null;
        }

        if (!string.IsNullOrWhiteSpace(rawBody) && rawBody.Contains("\"errors\"", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("errors", out var errorsProp) &&
                    errorsProp.ValueKind == JsonValueKind.Array &&
                    errorsProp.GetArrayLength() > 0)
                {
                    return errorsProp.GetRawText();
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON bodies
            }
        }

        return null;
    }

    public void Dispose()
    {
        _archiveHttpClient.Dispose();
        _authLock.Dispose();
        _botLoginLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
