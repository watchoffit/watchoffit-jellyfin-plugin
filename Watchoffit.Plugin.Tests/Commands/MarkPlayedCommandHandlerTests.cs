using System.Text.Json;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchoffit.Commands;
using Jellyfin.Plugin.Watchoffit.Commands.Handlers;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

using NSubstitute;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Commands;

/// <summary>
/// Tests for the v1 watched-state command handlers. These handlers are
/// the first non-ping consumers of the durable command channel.
/// </summary>
public sealed class MarkPlayedCommandHandlerTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void MarkPlayedCommandKind_IsWireLiteral()
    {
        var services = NewServices(NewUserData());
        var handler = new MarkPlayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        Assert.Equal("mark_played", handler.CommandKind);
    }

    [Fact]
    public async Task MarkPlayedAsync_SavesPlayedState()
    {
        var userData = new UserItemData
        {
            Key = ItemId.ToString("N"),
            Played = false,
            PlayCount = 0,
            LastPlayedDate = null,
            PlaybackPositionTicks = 123,
            IsFavorite = true,
        };
        var services = NewServices(userData);
        var handler = new MarkPlayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);
        var commandIdDuringSave = string.Empty;
        services.UserDataManager
            .When(m => m.SaveUserData(
                services.User,
                services.Item,
                userData,
                UserDataSaveReason.TogglePlayed,
                Arg.Any<CancellationToken>()))
            .Do(_ => commandIdDuringSave = services.CausationContext.CurrentCommandId);

        var result = await handler.HandleAsync(NewMarkPlayedCommand(), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.True(userData.Played);
        Assert.Equal(1, userData.PlayCount);
        Assert.Equal(new DateTime(2026, 8, 26, 20, 34, 0, DateTimeKind.Utc), userData.LastPlayedDate);
        Assert.Equal(0, userData.PlaybackPositionTicks);
        Assert.True(userData.IsFavorite);
        Assert.Equal("cmd_test", commandIdDuringSave);
        Assert.Null(services.CausationContext.CurrentCommandId);
        services.UserDataManager.Received(1).SaveUserData(
            services.User,
            services.Item,
            userData,
            UserDataSaveReason.TogglePlayed,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPlayedAsync_ExistingPlayedStateReturnsNoop()
    {
        var userData = new UserItemData
        {
            Key = ItemId.ToString("N"),
            Played = true,
            PlayCount = 2,
            LastPlayedDate = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        var services = NewServices(userData);
        var handler = new MarkPlayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        var result = await handler.HandleAsync(NewMarkPlayedCommand(), CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("already_played", result.Note);
        services.UserDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(),
            Arg.Any<BaseItem>(),
            Arg.Any<UserItemData>(),
            Arg.Any<UserDataSaveReason>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPlayedAsync_MissingUserDataCreatesRow()
    {
        var services = NewServices(null);
        var handler = new MarkPlayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        var result = await handler.HandleAsync(NewMarkPlayedCommand(), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        services.UserDataManager.Received(1).SaveUserData(
            services.User,
            services.Item,
            Arg.Is<UserItemData>(data =>
                data.Key == ItemId.ToString("N")
                && data.Played
                && data.PlayCount == 1
                && data.PlaybackPositionTicks == 0),
            UserDataSaveReason.TogglePlayed,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPlayedAsync_InvalidWatchedAtReturnsNoop()
    {
        var services = NewServices(new UserItemData { Key = ItemId.ToString("N") });
        var handler = new MarkPlayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        var result = await handler.HandleAsync(
            NewLeasedCommand(
                "mark_played",
                """
                {
                  "kind": "mark_played",
                  "jellyfinItemId": "22222222222222222222222222222222",
                  "watchoffitUserId": "11111111111111111111111111111111",
                  "mediaKind": "movie",
                  "watchedAt": "not-a-date"
                }
                """),
            CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("invalid_watched_at", result.Note);
        services.UserDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(),
            Arg.Any<BaseItem>(),
            Arg.Any<UserItemData>(),
            Arg.Any<UserDataSaveReason>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MarkUnplayedCommandKind_IsWireLiteral()
    {
        var services = NewServices(NewUserData());
        var handler = new MarkUnplayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        Assert.Equal("mark_unplayed", handler.CommandKind);
    }

    [Fact]
    public async Task MarkUnplayedAsync_SavesUnplayedState()
    {
        var userData = new UserItemData
        {
            Key = ItemId.ToString("N"),
            Played = true,
            PlayCount = 3,
            LastPlayedDate = new DateTime(2026, 8, 26, 20, 34, 0, DateTimeKind.Utc),
            PlaybackPositionTicks = 456,
            IsFavorite = true,
        };
        var services = NewServices(userData);
        var handler = new MarkUnplayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        var result = await handler.HandleAsync(NewMarkUnplayedCommand(), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.False(userData.Played);
        Assert.Equal(0, userData.PlayCount);
        Assert.Null(userData.LastPlayedDate);
        Assert.Equal(0, userData.PlaybackPositionTicks);
        Assert.True(userData.IsFavorite);
        services.UserDataManager.Received(1).SaveUserData(
            services.User,
            services.Item,
            userData,
            UserDataSaveReason.TogglePlayed,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkUnplayedAsync_ExistingUnplayedStateReturnsNoop()
    {
        var userData = new UserItemData
        {
            Key = ItemId.ToString("N"),
            Played = false,
            PlayCount = 0,
            LastPlayedDate = null,
        };
        var services = NewServices(userData);
        var handler = new MarkUnplayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        var result = await handler.HandleAsync(NewMarkUnplayedCommand(), CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("already_unplayed", result.Note);
        services.UserDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(),
            Arg.Any<BaseItem>(),
            Arg.Any<UserItemData>(),
            Arg.Any<UserDataSaveReason>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_InvalidUserIdReturnsNoop()
    {
        var services = NewServices(NewUserData());
        var handler = new MarkPlayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        var result = await handler.HandleAsync(
            NewLeasedCommand(
                "mark_played",
                """
                {
                  "kind": "mark_played",
                  "jellyfinItemId": "22222222222222222222222222222222",
                  "watchoffitUserId": "not-a-guid",
                  "mediaKind": "movie"
                }
                """),
            CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("invalid_user_id", result.Note);
    }

    [Fact]
    public async Task HandleAsync_CancellationHonored()
    {
        var services = NewServices(NewUserData());
        var handler = new MarkPlayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(NewMarkPlayedCommand(), cts.Token));
    }

    [Fact]
    public async Task HandleAsync_NullCommandThrows()
    {
        var services = NewServices(NewUserData());
        var handler = new MarkPlayedCommandHandler(
            services.LibraryManager,
            services.UserManager,
            services.UserDataManager,
            services.CausationContext);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => handler.HandleAsync(null!, CancellationToken.None));
    }

    private static HandlerServices NewServices(UserItemData? userData)
    {
        var user = new User("test", "Jellyfin.Plugin.Watchoffit.Tests", "Jellyfin.Plugin.Watchoffit.Tests")
        {
            Id = UserId,
        };
        var item = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Id = ItemId,
        };

        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemById(ItemId).Returns(item);

        var userManager = Substitute.For<IUserManager>();
        userManager.GetUserById(UserId).Returns(user);

        var userDataManager = Substitute.For<IUserDataManager>();
        userDataManager.GetUserData(user, item).Returns(userData);
        var causationContext = new CommandCausationContext();

        return new HandlerServices(libraryManager, userManager, userDataManager, causationContext, user, item);
    }

    private static UserItemData NewUserData() => new()
    {
        Key = ItemId.ToString("N"),
    };

    private static V1LeasedCommand NewMarkPlayedCommand() => NewLeasedCommand(
        "mark_played",
        """
        {
          "kind": "mark_played",
          "jellyfinItemId": "22222222222222222222222222222222",
          "watchoffitUserId": "11111111111111111111111111111111",
          "mediaKind": "movie",
          "watchedAt": "2026-08-26T20:34:00.000Z"
        }
        """);

    private static V1LeasedCommand NewMarkUnplayedCommand() => NewLeasedCommand(
        "mark_unplayed",
        """
        {
          "kind": "mark_unplayed",
          "jellyfinItemId": "22222222222222222222222222222222",
          "watchoffitUserId": "11111111111111111111111111111111",
          "mediaKind": "movie"
        }
        """);

    private static V1LeasedCommand NewLeasedCommand(string commandKind, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return new V1LeasedCommand
        {
            CommandId = "cmd_test",
            CommandKind = commandKind,
            Payload = doc.RootElement.Clone(),
            LeaseUntil = 0,
            AttemptToken = "att_test",
        };
    }

    private sealed record HandlerServices(
        ILibraryManager LibraryManager,
        IUserManager UserManager,
        IUserDataManager UserDataManager,
        ICommandCausationContext CausationContext,
        User User,
        BaseItem Item);
}
