using System.Security.Claims;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.DbMigrator;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// Verifies that the data migrator preserves ASP.NET Core Identity data — which is owned by the
/// framework, not by this codebase — so that users can still authenticate after switching
/// providers. A user, role, claim and external login are created through the real
/// <see cref="UserManager{TUser}"/>; before/after each leg the password is verified through
/// <see cref="UserManager{TUser}.CheckPasswordAsync"/> rather than by comparing columns.
/// </summary>
[Trait("Category", "Integration")]
public class DbMigratorIdentityTests
{
    private const string SkipReason =
        "Set TEST_DB_PROVIDER=postgres to run the DbMigrator identity tests (Docker required).";

    private const string UserName = "identity-user";
    private const string Email = "identity@example.com";
    private const string Password = "P@ssw0rd!";
    private const string RoleName = "Administrator";
    private const string ClaimType = "test-claim";
    private const string ClaimValue = "claim-value";
    private const string LoginProvider = "test-provider";
    private const string LoginKey = "external-key";
    private const string PreferredCulture = "de-DE";

    [Fact]
    public async Task Migrate_PreservesIdentityDataAndLoginAcrossBothDirections()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );
        await using TestDatabase sqliteTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );

        string userId;
        await using (ApplicationDbContext context = sqliteSource.CreateContext())
        {
            userId = await SeedIdentityAsync(context, ct);
        }

        // SQLite -> PostgreSQL
        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Sqlite,
                sqliteSource.ConnectionString,
                DatabaseProvider.Postgres,
                postgresTarget.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: false,
                FromLogsConnection: null,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );

        await using (
            ApplicationDbContext postgres = await postgresTarget.CreateContextAsync(
                ensureSchema: false,
                ct
            )
        )
        {
            await AssertIdentityWorksAsync(postgres, userId, ct);
        }

        // PostgreSQL -> SQLite
        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Postgres,
                postgresTarget.ConnectionString,
                DatabaseProvider.Sqlite,
                sqliteTarget.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: false,
                FromLogsConnection: null,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );

        await using ApplicationDbContext roundTripped = await sqliteTarget.CreateContextAsync(
            ensureSchema: false,
            ct
        );
        await AssertIdentityWorksAsync(roundTripped, userId, ct);
    }

    private static async Task<string> SeedIdentityAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken
    )
    {
        using ServiceProvider provider = BuildIdentityProvider(context);
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();

        (await roleManager.CreateAsync(new IdentityRole(RoleName))).EnsureSucceeded();

        var user = new ApplicationUser
        {
            UserName = UserName,
            Email = Email,
            EmailConfirmed = true,
            PreferredCulture = PreferredCulture,
        };
        (await userManager.CreateAsync(user, Password)).EnsureSucceeded(
            "creating the identity user"
        );

        (await userManager.AddToRoleAsync(user, RoleName)).EnsureSucceeded();
        (await userManager.AddClaimAsync(user, new Claim(ClaimType, ClaimValue))).EnsureSucceeded();
        (
            await userManager.AddLoginAsync(
                user,
                new UserLoginInfo(LoginProvider, LoginKey, "Test Provider")
            )
        ).EnsureSucceeded();

        return user.Id;
    }

    private static async Task AssertIdentityWorksAsync(
        ApplicationDbContext context,
        string expectedUserId,
        CancellationToken cancellationToken
    )
    {
        using ServiceProvider provider = BuildIdentityProvider(context);
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByNameAsync(UserName);
        Assert.NotNull(user);
        Assert.Equal(expectedUserId, user!.Id);
        Assert.Equal(Email, user.Email);
        Assert.Equal(PreferredCulture, user.PreferredCulture);
        Assert.NotNull(user.PasswordHash);
        Assert.NotNull(user.SecurityStamp);

        // The strongest check: the framework can still verify the password on the new provider.
        Assert.True(await userManager.CheckPasswordAsync(user, Password));
        Assert.False(await userManager.CheckPasswordAsync(user, "wrong-password"));

        Assert.True(await userManager.IsInRoleAsync(user, RoleName));

        IList<Claim> claims = await userManager.GetClaimsAsync(user);
        Assert.Contains(claims, c => c.Type == ClaimType && c.Value == ClaimValue);

        IList<UserLoginInfo> logins = await userManager.GetLoginsAsync(user);
        Assert.Contains(logins, l => l.LoginProvider == LoginProvider && l.ProviderKey == LoginKey);

        Assert.NotNull(await userManager.FindByEmailAsync(Email));
    }

    private static ServiceProvider BuildIdentityProvider(ApplicationDbContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        return services.BuildServiceProvider();
    }
}

internal static class IdentityResultAssertions
{
    public static void EnsureSucceeded(this IdentityResult result, string? action = null)
    {
        Assert.True(
            result.Succeeded,
            $"Identity operation{(action is null ? "" : $" ({action})")} failed: "
                + string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))
        );
    }
}
