using RateTray.Localization;
using RateTray.Model;
using RateTray.Ui;

namespace RateTray.Tests;

public class TrayAuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    public TrayAuthTests() => Loc.Use("en");

    [Fact]
    public void Expiry_is_checked_at_the_actual_access_token_deadline()
    {
        var auth = new AuthStatus { Group = "Claude", IsValid = true, ExpiresAt = Now };

        Assert.False(TrayApp.TokenExpired(auth, Now.AddMilliseconds(-1)));
        Assert.True(TrayApp.TokenExpired(auth, Now));
        Assert.True(TrayApp.TokenExpired(auth, Now.AddMinutes(1)));
    }

    [Fact]
    public void Missing_expiry_and_other_auth_failures_are_not_treated_as_token_expiry()
    {
        Assert.False(TrayApp.TokenExpired(null, Now));
        Assert.False(TrayApp.TokenExpired(new AuthStatus { Group = "Claude", IsValid = false }, Now));
        Assert.False(TrayApp.TokenExpired(
            new AuthStatus { Group = "Claude", IsValid = false, ExpiresAt = Now.AddHours(1) }, Now));
    }

    [Fact]
    public void Expiry_timer_tracks_the_deadline_and_handles_long_lifetimes()
    {
        var auth = new AuthStatus { Group = "Claude", IsValid = true, ExpiresAt = Now.AddSeconds(5) };

        Assert.Equal(5000, TrayApp.TokenExpiryDelay(auth, Now));
        Assert.Equal(1, TrayApp.TokenExpiryDelay(auth, Now.AddSeconds(5).AddTicks(-1)));
        Assert.Null(TrayApp.TokenExpiryDelay(auth, Now.AddSeconds(5)));
        Assert.Equal(int.MaxValue, TrayApp.TokenExpiryDelay(auth with { ExpiresAt = Now.AddDays(40) }, Now));
        Assert.Null(TrayApp.TokenExpiryDelay(null, Now));
    }

    [Fact]
    public void Expired_token_masks_a_cached_percentage_without_discarding_it()
    {
        var reading = new LimitReading { Id = "claude.extra_usage", Group = "Claude", Label = "Spend limit", Percent = 77 };

        Assert.Equal("??", TrayApp.IconTextFor(reading, tokenExpired: true));
        Assert.Equal("77", TrayApp.IconTextFor(reading, tokenExpired: false));
        Assert.Equal(77, reading.Percent);
    }

    [Fact]
    public void Expired_token_has_two_question_marks_even_without_a_cached_reading()
    {
        Assert.Equal("??", TrayApp.IconTextFor(null, tokenExpired: true));
        Assert.Equal("?", TrayApp.IconTextFor(null, tokenExpired: false));
    }

    [Fact]
    public void Expiry_icon_renders_both_question_marks()
    {
        using var icon = TrayIconRenderer.Render("??", Color.White, "Segoe UI");
        using var bitmap = icon.ToBitmap();
        var left = false;
        var right = false;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A == 0) continue;
            if (x < bitmap.Width / 2) left = true;
            else right = true;
        }

        Assert.True(left && right);
    }

    [Theory]
    [InlineData("claude.extra_usage", true, true)]
    [InlineData("CLAUDE.session", true, true)]
    [InlineData("codex.primary", true, false)]
    [InlineData("claude.session", false, false)]
    public void Expiry_only_masks_claude_icons(string id, bool tokenExpired, bool expected) =>
        Assert.Equal(expected, TrayApp.IsExpiredClaudeIcon(id, tokenExpired));

    [Fact]
    public void Details_marks_cached_readings_as_expired_without_losing_the_existing_error()
    {
        var reading = new LimitReading { Id = "claude.session", Group = "Claude", Label = "Session", Percent = 77 };
        var result = ProviderResult.Failed("Claude", "Rate limited") with { Readings = [reading] };
        var auth = new AuthStatus { Group = "Claude", IsValid = false, ExpiresAt = Now };

        var displayed = TrayApp.DisplayResult(result, tokenExpired: true, auth);

        Assert.Contains("Token expired", displayed.Error);
        Assert.Contains("Rate limited", displayed.Error);
        Assert.Same(reading, Assert.Single(displayed.Readings));
        Assert.Same(auth, displayed.Auth);
        Assert.Equal("Rate limited", result.Error);
    }

    [Fact]
    public void Details_does_not_change_other_providers_or_unexpired_readings()
    {
        var codex = ProviderResult.Success("Codex", []);
        var claude = ProviderResult.Success("Claude", []);

        Assert.Same(codex, TrayApp.DisplayResult(codex, tokenExpired: true, null));
        Assert.Same(claude, TrayApp.DisplayResult(claude, tokenExpired: false, null));
    }
}
