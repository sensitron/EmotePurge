using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class PendingMigrationGuard(AppDbContext db) : IPendingMigrationGuard
{
    public async Task EnsureNoPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"Die Datenbank ist nicht auf dem Migrationsstand dieses Builds — ausstehende Migration(en): {string.Join(", ", pending)}. "
                + "Migrationen sind in diesem Projekt bewusst manuell: 'dotnet ef database update' ausführen "
                + "(Prod: Anleitung 'Prod-Migration' in CLAUDE.md), danach den Dienst neu starten.");
        }
    }
}
