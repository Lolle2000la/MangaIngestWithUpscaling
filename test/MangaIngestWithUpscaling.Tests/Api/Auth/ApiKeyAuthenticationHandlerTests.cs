using System.Security.Claims;
using System.Text.Encodings.Web;
using MangaIngestWithUpscaling.Api.Auth;
using MangaIngestWithUpscaling.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Api.Auth;

public class ApiKeyAuthenticationHandlerTests
{
    private static ApplicationDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAuthenticateAsync_ValidApiKey_CachesTicketAndReusesOnSecondCall()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryDbContext(dbName);

        var user = new ApplicationUser
        {
            Id = "user1",
            UserName = "testuser",
            Email = "test@example.com",
        };
        var apiKey = new ApiKey
        {
            Id = 1,
            Key = "valid-api-key-123",
            IsActive = true,
            UserId = "user1",
            User = user,
        };
        context.Users.Add(user);
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var handler = new ApiKeyAuthenticationHandler(
            options,
            loggerFactory,
            UrlEncoder.Default,
            context,
            memoryCache
        );

        var scheme = new AuthenticationScheme(
            "ApiKey",
            "ApiKey",
            typeof(ApiKeyAuthenticationHandler)
        );
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "ApiKey valid-api-key-123";

        await handler.InitializeAsync(scheme, httpContext);

        // Act 1: First call -> populates cache
        var result1 = await handler.AuthenticateAsync();

        // Assert 1
        Assert.True(result1.Succeeded);
        Assert.Equal("user1", result1.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));

        // Delete key from DB to verify second call uses memory cache and does not fail or hit DB
        context.ApiKeys.Remove(apiKey);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act 2: Second call using a new handler and HttpContext to verify IMemoryCache across requests
        var handler2 = new ApiKeyAuthenticationHandler(
            options,
            loggerFactory,
            UrlEncoder.Default,
            context,
            memoryCache
        );
        var httpContext2 = new DefaultHttpContext();
        httpContext2.Request.Headers["Authorization"] = "ApiKey valid-api-key-123";

        await handler2.InitializeAsync(scheme, httpContext2);
        var result2 = await handler2.AuthenticateAsync();

        // Assert 2
        Assert.True(result2.Succeeded);
        Assert.Equal("user1", result2.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
    }
}
