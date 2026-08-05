using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class PendingMigrationGuardTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EnsureNoPendingMigrationsAsync_PassesOnAFullyMigratedDatabase()
    {
        await using var db = fixture.CreateDbContext();
        var guard = new PendingMigrationGuard(db);

        await guard.EnsureNoPendingMigrationsAsync();
    }

    [Fact]
    public async Task EnsureNoPendingMigrationsAsync_ThrowsWhenTheDatabaseIsBehindTheBuild()
    {
        // A second, empty database on the same container: from the guard's point of view every
        // compiled migration is pending there — the same state as a prod deploy whose manual
        // migration step was forgotten.
        var databaseName = $"pending_check_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
            // CREATE DATABASE cannot run inside a transaction; ExecuteSqlRawAsync sends it as a
            // single non-transactional command. EF1002 (injection) does not apply — the name is a
            // locally generated Guid, and CREATE DATABASE cannot be parameterized anyway.
#pragma warning disable EF1002
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using var db = fixture.CreateDbContext(databaseName);
        var guard = new PendingMigrationGuard(db);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.EnsureNoPendingMigrationsAsync());

        // The message must name at least one concrete migration, otherwise the crash loop it
        // causes is undiagnosable from container logs alone.
        Assert.Contains("Migration", exception.Message);
        Assert.Contains("Initial", exception.Message);
    }
}
