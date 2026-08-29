using System.Globalization;
using System.Text.Json;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Watchoffit.Commands.Handlers;

/// <summary>
/// Handles Watchoffit's v1 <c>mark_played</c> command by applying the
/// requested watched state to Jellyfin's user-data store.
/// </summary>
public sealed class MarkPlayedCommandHandler : MarkUserDataCommandHandler<V1MarkPlayedCommand>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MarkPlayedCommandHandler"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library lookup service.</param>
    /// <param name="userManager">Jellyfin user lookup service.</param>
    /// <param name="userDataManager">Jellyfin user-data persistence service.</param>
    /// <param name="causationContext">Command causation context used to tag resulting Jellyfin events.</param>
    public MarkPlayedCommandHandler(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ICommandCausationContext causationContext)
        : base(libraryManager, userManager, userDataManager, causationContext)
    {
    }

    /// <inheritdoc />
    public override string CommandKind => "mark_played";

    /// <inheritdoc />
    protected override V1CommandResult Apply(
        V1MarkPlayedCommand payload,
        User user,
        BaseItem item,
        UserItemData userData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(userData);

        if (userData.Played)
        {
            return V1CommandResult.NoopWithNote("already_played");
        }

        var playedAt = ParseWatchedAt(payload.WatchedAt);
        if (playedAt is null && !string.IsNullOrWhiteSpace(payload.WatchedAt))
        {
            return V1CommandResult.NoopWithNote("invalid_watched_at");
        }

        userData.Played = true;
        userData.PlayCount = Math.Max(1, userData.PlayCount);
        userData.LastPlayedDate = playedAt ?? DateTime.UtcNow;
        userData.PlaybackPositionTicks = 0;

        Save(user, item, userData, cancellationToken);
        return V1CommandResult.Ok();
    }

    private static DateTime? ParseWatchedAt(string? watchedAt)
    {
        if (string.IsNullOrWhiteSpace(watchedAt))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                watchedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return parsed.UtcDateTime;
    }
}

/// <summary>
/// Handles Watchoffit's v1 <c>mark_unplayed</c> command by clearing the
/// watched state in Jellyfin's user-data store.
/// </summary>
public sealed class MarkUnplayedCommandHandler : MarkUserDataCommandHandler<V1MarkUnplayedCommand>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MarkUnplayedCommandHandler"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library lookup service.</param>
    /// <param name="userManager">Jellyfin user lookup service.</param>
    /// <param name="userDataManager">Jellyfin user-data persistence service.</param>
    /// <param name="causationContext">Command causation context used to tag resulting Jellyfin events.</param>
    public MarkUnplayedCommandHandler(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ICommandCausationContext causationContext)
        : base(libraryManager, userManager, userDataManager, causationContext)
    {
    }

    /// <inheritdoc />
    public override string CommandKind => "mark_unplayed";

    /// <inheritdoc />
    protected override V1CommandResult Apply(
        V1MarkUnplayedCommand payload,
        User user,
        BaseItem item,
        UserItemData userData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(userData);

        if (!userData.Played && userData.PlayCount == 0 && userData.LastPlayedDate is null)
        {
            return V1CommandResult.NoopWithNote("already_unplayed");
        }

        userData.Played = false;
        userData.PlayCount = 0;
        userData.LastPlayedDate = null;
        userData.PlaybackPositionTicks = 0;

        Save(user, item, userData, cancellationToken);
        return V1CommandResult.Ok();
    }
}

/// <summary>
/// Shared lookup, payload parsing, and save plumbing for v1 commands that
/// mutate Jellyfin user-data.
/// </summary>
/// <typeparam name="TPayload">Concrete v1 command payload type.</typeparam>
public abstract class MarkUserDataCommandHandler<TPayload> : ICommandHandler
    where TPayload : V1ItemIdentityCommand
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ICommandCausationContext _causationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkUserDataCommandHandler{TPayload}"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library lookup service.</param>
    /// <param name="userManager">Jellyfin user lookup service.</param>
    /// <param name="userDataManager">Jellyfin user-data persistence service.</param>
    /// <param name="causationContext">Command causation context used to tag resulting Jellyfin events.</param>
    protected MarkUserDataCommandHandler(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ICommandCausationContext causationContext)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
        _causationContext = causationContext ?? throw new ArgumentNullException(nameof(causationContext));
    }

    /// <inheritdoc />
    public abstract string CommandKind { get; }

    /// <inheritdoc />
    public Task<V1CommandResult> HandleAsync(
        V1LeasedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = DeserializePayload(command);
        if (payload is null)
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("invalid_payload"));
        }

        if (!TryParseGuid(payload.WatchoffitUserId, out var userId))
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("invalid_user_id"));
        }

        if (!TryParseGuid(payload.JellyfinItemId, out var itemId))
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("invalid_item_id"));
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("user_not_found"));
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("item_not_found"));
        }

        var userData = _userDataManager.GetUserData(user, item) ?? new UserItemData
        {
            Key = item.Id.ToString("N", CultureInfo.InvariantCulture),
        };
        using var scope = _causationContext.Begin(command.CommandId);
        var result = Apply(payload, user, item, userData, cancellationToken);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Apply the command-specific mutation after common lookup succeeds.
    /// </summary>
    /// <param name="payload">Parsed v1 command payload.</param>
    /// <param name="user">Jellyfin user targeted by the command.</param>
    /// <param name="item">Jellyfin item targeted by the command.</param>
    /// <param name="userData">Current Jellyfin user-data row for the item and user.</param>
    /// <param name="cancellationToken">Cancellation token for plugin shutdown.</param>
    /// <returns>The command result that should be acked to Watchoffit.</returns>
    protected abstract V1CommandResult Apply(
        TPayload payload,
        User user,
        BaseItem item,
        UserItemData userData,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persist an updated Jellyfin user-data row.
    /// </summary>
    /// <param name="user">Jellyfin user targeted by the command.</param>
    /// <param name="item">Jellyfin item targeted by the command.</param>
    /// <param name="userData">Updated Jellyfin user-data row.</param>
    /// <param name="cancellationToken">Cancellation token for plugin shutdown.</param>
    protected void Save(User user, BaseItem item, UserItemData userData, CancellationToken cancellationToken)
    {
        _userDataManager.SaveUserData(
            user,
            item,
            userData,
            UserDataSaveReason.TogglePlayed,
            cancellationToken);
    }

    private static TPayload? DeserializePayload(V1LeasedCommand command)
    {
        try
        {
            return command.Payload.Deserialize<TPayload>(SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseGuid(string value, out Guid guid)
    {
        return Guid.TryParseExact(value, "N", out guid)
            || Guid.TryParse(value, out guid);
    }
}
