namespace EmotePurge.Core.Services;

/// <summary>
/// Fail-fast check for the manual-migration workflow (review finding S3-34): migrations are never
/// applied automatically in this project, so a deploy against a database that is missing one used
/// to start up "healthy" and then answer every affected request with a silent 500
/// (<c>PostgresException 42703</c>). Throwing at startup turns that into a visible crash loop in
/// Portainer instead. Deliberately a guard, not an auto-migrate — applying migrations remains a
/// deliberate, manual step (see CLAUDE.md, "Prod-Migration").
/// </summary>
public interface IPendingMigrationGuard
{
    /// <summary>Throws when at least one compiled migration has not been applied to the database.</summary>
    Task EnsureNoPendingMigrationsAsync(CancellationToken cancellationToken = default);
}
