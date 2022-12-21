using System.Collections.Concurrent;
using MeduzaRepost.Database;
using NLog;
using TL;
using WTelegram;

namespace MeduzaRepost;

public sealed class TelegramReader: IObservable<TgEvent>, IDisposable
{
    private static readonly ILogger Log = Config.Log;
    private readonly ConcurrentDictionary<IObserver<TgEvent>, Unsubscriber> subscribers = new();
    private readonly BotDb db = new();

    private Channel channel = null!;
    internal readonly Client Client = new(Config.Get);

    static TelegramReader() => Helpers.Log = (level, message) => Log.Log(LogLevel.FromOrdinal(level), message);
    
    public async Task Run()
    {
        var bot = await Client.LoginUserIfNeeded().ConfigureAwait(false);
        Log.Info($"We are logged-in as {bot} (id {bot.id})");
        var chats = await Client.Messages_GetAllChats().ConfigureAwait(false);
        Log.Debug($"Open chat count: {chats.chats.Count}");
        if (chats.chats.Values.FirstOrDefault(chat => chat is Channel { username: "meduzalive" }) is not Channel ch)
        {
            Log.Error("Meduza channel (@meduzalive) is not available");
            return;
        }

        // check and init saved pts value if needed
        channel = ch;
        Log.Info($"Reading channel #{channel.ID}: {channel.Title}");
        if (db.BotState.FirstOrDefault(s => s.Key == "pts") is { Value: { Length: > 0 } ptsVal } state
            && int.TryParse(ptsVal, out var savedPts)
            && savedPts > 0)
        {
            // check missed updates
            Log.Info("Checking missed channel updates...");
            var diffPts = savedPts;
            while (await Client.Updates_GetChannelDifference(channel, null, diffPts).ConfigureAwait(false) is Updates_ChannelDifference diff)
            {
                Log.Info($"Got {diff.NewMessages.Length} new messages and {diff.OtherUpdates.Length} other updates, {(diff.Final ? "" : "not ")}final");
                foreach (var message in diff.NewMessages.OfType<Message>().OrderBy(m => m.Date))
                    Push(new(TgEventType.Post, message));
                foreach (var update in diff.OtherUpdates)
                    await OnUpdate(update).ConfigureAwait(false);
                diffPts = diff.pts;
                if (diff.Final)
                    break;
            }
            savedPts = diffPts;
            state.Value = savedPts.ToString();
        }
        else
        {
            Log.Info("No saved pts value, initializing state");
            var dialogs = await Client.Messages_GetPeerDialogs(channel.ToInputPeer()).ConfigureAwait(false);
            if (dialogs.dialogs is not [Dialog dialog] || dialog.Peer.ID != channel.ID)
            {
                Log.Error("Failed to fetch current channel status");
                return;
            }

            savedPts = dialog.pts;
            Log.Info($"Got initial pts value: {savedPts}");
            db.BotState.Add(new() { Key = "pts", Value = savedPts.ToString() });
        }
        await db.SaveChangesAsync(Config.Cts.Token).ConfigureAwait(false);

        Client.OnUpdate += OnUpdate;

        while (!Config.Cts.IsCancellationRequested)
            await Task.Delay(200).ConfigureAwait(false);
    }

    private async Task OnUpdate(IObject arg)
    {
        if (arg is not UpdatesBase updates)
            return;

        foreach (var update in updates.UpdateList)
        {
            switch (update)
            {
                case UpdateNewMessage u when u.message.Peer.ID == channel.ID:
                {
                    var link = await Client.Channels_ExportMessageLink(channel, u.message.ID).ConfigureAwait(false);
                    Push(new(TgEventType.Post, (Message)u.message, link.link));
                    await UpdatePts(u.pts).ConfigureAwait(false);
                    break;
                }
                case UpdateEditMessage u when u.message.Peer.ID == channel.ID:
                {
                    var link = await Client.Channels_ExportMessageLink(channel, u.message.ID).ConfigureAwait(false);
                    Push(new(TgEventType.Edit, (Message)u.message, link.link));
                    await UpdatePts(u.pts).ConfigureAwait(false);
                    break;
                }
                case UpdateDeleteMessages u:
                {
                    foreach (var id in u.messages)
                        Push(new(TgEventType.Delete, new() { id = id }));
                    await UpdatePts(u.pts).ConfigureAwait(false);
                    break;
                }
                case UpdatePinnedMessages u:
                {
                    Push(new(TgEventType.Pin, new(){id=u.messages.Max()}));
                    await UpdatePts(u.pts).ConfigureAwait(false);
                    break;
                }
                default:
                {
                    Log.Debug($"Ignoring update of type {update.GetType().Name}");
                    break;
                }
            }
        }
    }

    private void Push(TgEvent evt)
    {
        foreach (var observer in subscribers.Keys)
            try
            {
                observer.OnNext(evt);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to push telegram event");
            }
    }

    private async Task UpdatePts(int pts)
    {
        var state = db.BotState.First(s => s.Key == "pts");
        var savedPts = int.Parse(state.Value);
        if (pts != savedPts + 1)
            Log.Warn($"Unexpected pts update: saved pts was {savedPts} and new pts is {pts}");
        if (pts > savedPts)
        {
            state.Value = pts.ToString();
            await db.SaveChangesAsync(Config.Cts.Token).ConfigureAwait(false);
        }
        else
            Log.Warn($"Ignoring request to update pts from {savedPts} to {pts}");
    }

    public void Dispose()
    {
        foreach (var observer in subscribers.Keys)
            observer.OnCompleted();
        Client.Dispose();
        db.Dispose();
    }

    public IDisposable Subscribe(IObserver<TgEvent> observer)
    {
        if (!subscribers.TryGetValue(observer, out var unsubscriber))
        {
            unsubscriber = new(subscribers, observer);
            if (!subscribers.TryAdd(observer, unsubscriber))
                throw new InvalidOperationException("Observer is already subscribed");
        }
        return unsubscriber;
    }

    private record Unsubscriber(ConcurrentDictionary<IObserver<TgEvent>, Unsubscriber> Subscribers, IObserver<TgEvent> Observer) : IDisposable
    {
        public void Dispose() => Subscribers.TryRemove(Observer, out _);
    }
}

public enum TgEventType
{
    Post,
    Edit,
    Delete,
    Pin,
}

public sealed record TgEvent(TgEventType Type, Message Message, string? Link = null); 