namespace abaci_bot.Services;

/// <summary>
/// Port used at startup by <see cref="PackageUpdatePermissionProbe"/> to verify repository write access
/// and optional <c>BotPat</c> organization team-read permissions without widening <see cref="IGitHubService"/>.
/// </summary>
public interface IPackageUpdatePermissionProbeGateway
{
    Task<string> VerifyPackageUpdateWritePermissionAsync(string owner, string repo, CancellationToken ct = default);

    Task VerifyBotPatTeamReadPermissionAsync(string owner, string repo, string teamName, CancellationToken ct = default);
}
