using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.Account;
using MangaIngestWithUpscaling.Components.Account.Pages.Manage;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.Auth;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.Account;

/// <summary>
/// Coverage for the API-key management page (fix D1). The page used to render a readonly input with
/// no form name and a model whose <c>ApiKey</c> was left at its empty default, so the
/// <c>[Required]</c> form was never valid and clicking Regenerate did nothing.
/// </summary>
public class ApiKeysTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private ApplicationDbContext _dbContext = null!;
    private IApiKeyService _subApiKeyService = null!;
    private UserManager<ApplicationUser> _subUserManager = null!;
    private SignInManager<ApplicationUser> _subSignInManager = null!;
    private ApplicationUser _user = null!;

    public ApiKeysTests()
    {
        SetupDatabase();
        SetupMocks();
        RegisterServices();
    }

    private void SetupDatabase()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _dbContext = _testDb.Context;
    }

    private void SetupMocks()
    {
        _subApiKeyService = Substitute.For<IApiKeyService>();
        _subUserManager = SubstituteClass<UserManager<ApplicationUser>>();
        _subSignInManager = SubstituteClass<SignInManager<ApplicationUser>>();
    }

    /// <summary>
    /// Substitutes a class whose only constructors take dependencies (UserManager/SignInManager).
    /// NSubstitute needs explicit constructor arguments for classes without a parameterless
    /// constructor, so fill every parameter with a substitute or a simple default.
    /// </summary>
    private T SubstituteClass<T>()
        where T : class
    {
        ConstructorInfo constructor = typeof(T)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        object[] arguments = constructor
            .GetParameters()
            .Select(p => CreateConstructorArgument(p.ParameterType)!)
            .ToArray();

        return (T)Substitute.For(new[] { typeof(T) }, arguments);
    }

    private object? CreateConstructorArgument(Type type)
    {
        if (type == typeof(IOptions<IdentityOptions>))
        {
            return Options.Create(new IdentityOptions());
        }

        if (type == typeof(UserManager<ApplicationUser>))
        {
            return _subUserManager;
        }

        if (type.IsInterface || type.IsAbstract)
        {
            return Substitute.For(new[] { type }, Array.Empty<object>());
        }

        return type.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(type);
    }

    private void RegisterServices()
    {
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_dbContext);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            new TestDbContextFactory(_testDb.Database)
        );
        Services.AddSingleton(_subApiKeyService);
        Services.AddSingleton(_subUserManager);
        Services.AddSingleton(_subSignInManager);

        // IdentityUserAccessor and IdentityRedirectManager are internal sealed and cannot be
        // substituted, so construct the real types with substituted/real dependencies. The accessor
        // only uses the user manager; the redirect manager only needs the bUnit navigation manager.
        Services.AddSingleton(sp => new IdentityRedirectManager(
            sp.GetRequiredService<NavigationManager>()
        ));
        Services.AddSingleton(sp => new IdentityUserAccessor(
            _subUserManager,
            sp.GetRequiredService<IdentityRedirectManager>()
        ));
    }

    private async Task<(ApplicationUser user, ApiKey key)> SeedUserWithActiveKeyAsync()
    {
        _user = new ApplicationUser
        {
            Id = "user-1",
            UserName = "user@example.com",
            Email = "user@example.com",
        };
        _dbContext.Users.Add(_user);
        var key = new ApiKey
        {
            Key = "seed-api-key",
            UserId = _user.Id,
            IsActive = true,
            Expiration = DateTime.UtcNow.AddYears(1),
        };
        _dbContext.ApiKeys.Add(key);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        _subUserManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(_user);
        return (_user, key);
    }

    private IRenderedComponent<ApiKeys> RenderApiKeys()
    {
        var httpContext = new DefaultHttpContext();
        return Render<ApiKeys>(parameters =>
            parameters.AddCascadingValue<HttpContext>(httpContext)
        );
    }

    [Fact]
    public async Task ApiKeys_ActiveKey_IsDisplayedAndBindsIntoTheFormModel()
    {
        (ApplicationUser user, ApiKey key) = await SeedUserWithActiveKeyAsync();

        IRenderedComponent<ApiKeys> component = RenderApiKeys();
        component.WaitForAssertion(() =>
            Assert.Equal(key.Key, component.Find("#apiKey").GetAttribute("value"))
        );

        // The regression: with an empty model this returned false and the Regenerate handler never
        // ran. The assigned model value must satisfy [Required].
        EditForm editForm = component.FindComponent<EditForm>().Instance;
        Assert.True(
            editForm.EditContext!.Validate(),
            "The Regenerate form must be valid for an active key"
        );

        IElement input = component.Find("#apiKey");
        // The input must post under the model property name so static-SSR binding can populate it.
        Assert.Equal("Input.ApiKey", input.GetAttribute("name"));
    }

    [Fact]
    public async Task ApiKeys_Regenerate_DeactivatesOldKeyAndCreatesANewActiveOne()
    {
        (ApplicationUser user, ApiKey oldKey) = await SeedUserWithActiveKeyAsync();

        _subApiKeyService
            .CreateApiKeyAsync(Arg.Any<string>())
            .Returns(callInfo =>
            {
                string userId = callInfo.Arg<string>();
                using var db = _testDb.Database.CreateContext();
                var newKey = new ApiKey
                {
                    Key = "regenerated-api-key",
                    UserId = userId,
                    IsActive = true,
                    Expiration = DateTime.UtcNow.AddYears(1),
                };
                db.ApiKeys.Add(newKey);
                db.SaveChanges();
                return Task.FromResult(newKey);
            });

        IRenderedComponent<ApiKeys> component = RenderApiKeys();
        component.WaitForAssertion(() =>
            Assert.Equal(oldKey.Key, component.Find("#apiKey").GetAttribute("value"))
        );

        // The real Regenerate POST is handled by static-SSR form mapping, which bUnit's interactive
        // renderer does not route; invoke the handler the form would call instead. The trailing
        // redirect is only valid during static rendering, so swallow its throw.
        await component.InvokeAsync(async () =>
        {
            MethodInfo handler = typeof(ApiKeys).GetMethod(
                "OnValidSubmitAsync",
                BindingFlags.NonPublic | BindingFlags.Instance
            )!;
            try
            {
                await (Task)handler.Invoke(component.Instance, null)!;
            }
            catch (InvalidOperationException)
            {
                // IdentityRedirectManager can only redirect during static rendering.
            }
        });

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        ApiKey persistedOld = await verifyDb.ApiKeys.FirstAsync(k => k.Id == oldKey.Id);
        Assert.False(persistedOld.IsActive);

        ApiKey persistedNew = await verifyDb.ApiKeys.SingleAsync(k => k.Id != oldKey.Id);
        Assert.Equal("regenerated-api-key", persistedNew.Key);
        Assert.True(persistedNew.IsActive);
        Assert.Equal(user.Id, persistedNew.UserId);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        // Run the whole teardown on the thread pool: bUnit's service-provider disposal (which
        // disposes the shared ApplicationDbContext) and the database drop both resume async
        // continuations, and resuming them on the renderer's synchronization context can deadlock.
        await Task.Run(DisposeCoreAsync).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);

        if (_testDb is not null)
        {
            TestDatabaseHelper.TestDbContext testDb = _testDb;
            _testDb = null!;
            await testDb.DisposeAsync().ConfigureAwait(false);
        }
    }
}
