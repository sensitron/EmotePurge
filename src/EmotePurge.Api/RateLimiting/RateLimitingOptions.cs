namespace EmotePurge.Api.RateLimiting;

/// <summary>
/// The budget of every rate-limit policy, bound from the <c>RateLimiting</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the numbers were literals in <c>Program.cs</c>, so every adjustment was a
/// rebuild and a redeploy of the image — and the tests had to mirror the literals to be able to
/// exhaust a budget at all. Environment variables override each value individually
/// (<c>RateLimiting__InteractiveRead__TokenLimit</c> and so on).
/// </para>
/// <para>
/// Read once at startup and never again: there is no write endpoint and no reload hook, so a changed
/// value takes effect on the next restart. That is deliberate — a limiter whose budget can move
/// under a running process cannot be reasoned about from a log line after the fact.
/// </para>
/// </remarks>
internal sealed class RateLimitingOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    internal const string SectionName = "RateLimiting";

    /// <summary>Ordinary navigation. Generous on purpose: an abuse guard, not a provider surrogate.</summary>
    public TokenBucketPolicy InteractiveRead { get; set; } = new()
    {
        TokenLimit = 300,
        TokensPerPeriod = 5,
        ReplenishmentPeriodSeconds = 1,
    };

    /// <summary>Vote mutations. A burst of clicking is normal here and must not read as abuse.</summary>
    public TokenBucketPolicy Voting { get; set; } = new()
    {
        TokenLimit = 120,
        TokensPerPeriod = 2,
        ReplenishmentPeriodSeconds = 1,
    };

    /// <summary>Writes against our own database, guarded loosely because dropping one costs data.</summary>
    public FixedWindowPolicy Bookkeeping { get; set; } = new() { PermitLimit = 120 };

    /// <summary>Manual channel resync: one unconditional 7TV call plus a live fan-out per hit.</summary>
    public FixedWindowPolicy ChannelResync { get; set; } = new() { PermitLimit = 5 };

    /// <summary>The anonymous health endpoint, whose legitimate callers are machines on fixed cadences.</summary>
    public FixedWindowPolicy PublicHealth { get; set; } = new() { PermitLimit = 30 };

    /// <summary>
    /// Throws unless every budget is usable. Called during startup, so a typo in an environment
    /// variable stops the container with a readable message instead of silently handing some policy
    /// a capacity of zero — which is not a lax limiter but a total outage of every route it guards,
    /// and one that looks exactly like a rate-limit incident in the log.
    /// </summary>
    public void Validate()
    {
        InteractiveRead.Validate(nameof(InteractiveRead));
        Voting.Validate(nameof(Voting));
        Bookkeeping.Validate(nameof(Bookkeeping));
        ChannelResync.Validate(nameof(ChannelResync));
        PublicHealth.Validate(nameof(PublicHealth));
    }

    private static void RequirePositive(string policyName, string valueName, int value)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException(
                $"Ungültige Rate-Limit-Konfiguration: '{SectionName}:{policyName}:{valueName}' muss größer als 0 sein, ist aber {value}.");
        }
    }

    /// <summary>A token bucket: a capacity that refills continuously while the app runs.</summary>
    internal sealed class TokenBucketPolicy
    {
        /// <summary>Capacity of the bucket, and therefore the largest burst a caller may spend at once.</summary>
        public int TokenLimit { get; set; }

        /// <summary>Tokens added per replenishment period.</summary>
        public int TokensPerPeriod { get; set; }

        /// <summary>Length of one replenishment period.</summary>
        public int ReplenishmentPeriodSeconds { get; set; }

        public void Validate(string policyName)
        {
            RequirePositive(policyName, nameof(TokenLimit), TokenLimit);
            RequirePositive(policyName, nameof(TokensPerPeriod), TokensPerPeriod);
            RequirePositive(policyName, nameof(ReplenishmentPeriodSeconds), ReplenishmentPeriodSeconds);
        }
    }

    /// <summary>A fixed window: a permit count that resets wholesale at every window boundary.</summary>
    internal sealed class FixedWindowPolicy
    {
        /// <summary>Permits granted per <see cref="RateLimitRejection.Window"/>.</summary>
        public int PermitLimit { get; set; }

        public void Validate(string policyName) => RequirePositive(policyName, nameof(PermitLimit), PermitLimit);
    }
}
