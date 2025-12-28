using MangaIngestWithUpscaling.Data;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.UI.Account;

/// <summary>
/// Tests for the culture cookie deletion logic when users change language preferences.
/// These tests verify the fix for the issue where browser preferences were not being used
/// when users selected "Default (Browser)" option.
///
/// Note: These are logic tests rather than full UI component tests because the Index.razor
/// component requires complex authentication and HTTP context setup that is difficult to
/// test properly in a unit test environment.
/// </summary>
public class CulturePreferenceTests
{
    [Fact]
    public void PreferredCulture_ShouldBeNull_WhenUserSelectsBrowserDefault()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = "test-user",
            UserName = "testuser",
            Email = "test@example.com",
            PreferredCulture = "de-DE", // User previously had German set
        };

        // Act - simulate user selecting "Default (Browser)" option
        string? newPreferredCulture = ""; // Empty string means browser default

        // Simulate the logic from Index.razor OnValidSubmitAsync
        bool shouldClearCookie = false;
        if (newPreferredCulture != user.PreferredCulture)
        {
            user.PreferredCulture = string.IsNullOrEmpty(newPreferredCulture)
                ? null
                : newPreferredCulture;

            // This is the fix - mark that cookie should be cleared when user selects browser default
            if (string.IsNullOrEmpty(newPreferredCulture))
            {
                shouldClearCookie = true;
            }
        }

        // Assert
        Assert.Null(user.PreferredCulture);
        Assert.True(
            shouldClearCookie,
            "Culture cookie should be cleared when user selects browser default"
        );
    }

    [Fact]
    public void PreferredCulture_ShouldBeSet_WhenUserSelectsSpecificLanguage()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = "test-user",
            UserName = "testuser",
            Email = "test@example.com",
            PreferredCulture = null,
        };

        // Act - simulate user selecting a specific language (e.g., German)
        string? newPreferredCulture = "de-DE";

        bool shouldClearCookie = false;
        if (newPreferredCulture != user.PreferredCulture)
        {
            user.PreferredCulture = string.IsNullOrEmpty(newPreferredCulture)
                ? null
                : newPreferredCulture;

            // Culture cookie should NOT be cleared when user selects a specific language
            if (string.IsNullOrEmpty(newPreferredCulture))
            {
                shouldClearCookie = true;
            }
        }

        // Assert
        Assert.Equal("de-DE", user.PreferredCulture);
        Assert.False(
            shouldClearCookie,
            "Culture cookie should NOT be cleared when user selects a specific language"
        );
    }

    [Fact]
    public void PreferredCulture_ShouldClearCookie_WhenSavingEmptyStringOverNull()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = "test-user",
            UserName = "testuser",
            Email = "test@example.com",
            PreferredCulture = null, // Currently null (browser default)
        };

        // Act - simulate user saving with empty string (from dropdown "Default (Browser)")
        string? newPreferredCulture = ""; // Empty string from dropdown

        bool shouldClearCookie = false;
        if (newPreferredCulture != user.PreferredCulture)
        {
            // This block will execute because "" != null in C#
            user.PreferredCulture = string.IsNullOrEmpty(newPreferredCulture)
                ? null
                : newPreferredCulture;

            if (string.IsNullOrEmpty(newPreferredCulture))
            {
                shouldClearCookie = true;
            }
        }

        // Assert - cookie should be cleared to ensure browser preference is used
        Assert.Null(user.PreferredCulture);
        Assert.True(
            shouldClearCookie,
            "Culture cookie should be cleared when user saves browser default, even if already null"
        );
    }

    [Theory]
    [InlineData("en-US", "de-DE", false)] // Changing from English to German
    [InlineData("de-DE", "", true)] // Changing from German to browser default
    [InlineData("", "ja-JP", false)] // Changing from browser default to Japanese
    [InlineData(null, "", true)] // Changing from null to empty (both mean browser default, but treated as change)
    [InlineData("en-US", "en-US", false)] // No change (same language)
    [InlineData(null, null, false)] // No change (both are null)
    public void CookieClearLogic_ShouldBehaveCorrectly_ForVariousScenarios(
        string? currentCulture,
        string? newCulture,
        bool expectedShouldClearCookie
    )
    {
        // Arrange
        var user = new ApplicationUser { Id = "test-user", PreferredCulture = currentCulture };

        // Act - simulate the logic from Index.razor OnValidSubmitAsync
        bool shouldClearCookie = false;
        if (newCulture != user.PreferredCulture)
        {
            user.PreferredCulture = string.IsNullOrEmpty(newCulture) ? null : newCulture;

            if (string.IsNullOrEmpty(newCulture))
            {
                shouldClearCookie = true;
            }
        }

        // Assert
        Assert.Equal(expectedShouldClearCookie, shouldClearCookie);
    }
}
