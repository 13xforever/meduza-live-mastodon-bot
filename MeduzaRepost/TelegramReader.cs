using System.Collections.Concurrent;
using MeduzaRepost.Database;
using NLog;
using TL;
using WTelegram;

namespace MeduzaRepost;

public sealed class TelegramReader: IDisposable, IObservable<TgEvent>
{
    private readonly Client client = new(Config.Get);
    private readonly ILogger log = Config.Log;
    private readonly ConcurrentDictionary<IObserver<TgEvent>, Unsubscriber> subscribers = new();

    static TelegramReader() => Helpers.Log = (level, message) => Config.Log.Log(LogLevel.FromOrdinal(level), message);
    
    public async Task Run()
    {
        var bot = await client.LoginUserIfNeeded().ConfigureAwait(false);
        log.Info($"We are logged-in as {bot} (id {bot.id})");
        var chats = await client.Messages_GetAllChats().ConfigureAwait(false);
        log.Debug($"Open chat count: {chats.chats.Count}");
        if (chats.chats.Values.FirstOrDefault(chat => chat is Channel { username: "meduzalive" }) is not Channel channel)
        {
            log.Error("Meduza channel (@meduzalive) is not available");
            return;
        }

        // check and init saved pts value if needed
        log.Info($"Reading channel #{channel.ID}: {channel.Title}");
        await using var db = new BotDb();
        if (db.BotState.FirstOrDefault(s => s.Key == "pts")?.Value is not { Length: > 0 } ptsVal
            || !int.TryParse(ptsVal, out var savedPts)
            || savedPts == 0)
        {
            log.Info("No saved pts value, initializing state");
            var dialogs = await client.Messages_GetPeerDialogs(channel.ToInputPeer()).ConfigureAwait(false);
            if (dialogs.dialogs is not [Dialog dialog] || dialog.Peer.ID != channel.ID)
            {
                log.Error("Failed to fetch current channel status");
                return;
            }

            savedPts = dialog.pts;
            log.Info($"Got initial pts value: {savedPts}");
            db.BotState.Add(new() { Key = "pts", Value = savedPts.ToString() });
            await db.SaveChangesAsync(Config.Cts.Token).ConfigureAwait(false);
        }
        else
        {
            // check missed updates
            var diff = await client.Updates_GetChannelDifference(channel, null, savedPts).ConfigureAwait(false);
            do
            {
                foreach (var message in diff.NewMessages.OfType<Message>())
                    Push(new(TgEventType.Post, message));
                foreach (var update in diff.OtherUpdates)
                    await OnUpdate(update).ConfigureAwait(false);
            } while (!diff.Final);
        }

        client.OnUpdate += OnUpdate;

    }

    private async Task OnUpdate(IObject arg)
    {
        if (arg is not UpdatesBase updates)
            return;

        foreach (var update in updates.UpdateList)
        {
            switch (update)
            {
                case UpdateNewMessage u:
                {
                    Push(new(TgEventType.Post, (Message)u.message));
                    await UpdatePts(u.pts).ConfigureAwait(false);
                    break;
                }
                case UpdateEditMessage u:
                {
                    Push(new(TgEventType.Edit, (Message)u.message));
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
                default:
                {
                    log.Debug($"Ignoring update of type {update.GetType().Name}");
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
                log.Error(e, "Failed to push telegram event");
            }
    }

    private async Task UpdatePts(int pts)
    {
        await using var db = new BotDb();
        var state = db.BotState.First(s => s.Key == "pts");
        var savedPts = int.Parse(state.Value);
        if (pts != savedPts + 1)
            log.Warn($"Unexpected pts update: saved pts was {savedPts} and new pts is {pts}");
        if (pts > savedPts)
        {
            state.Value = pts.ToString();
            await db.SaveChangesAsync(Config.Cts.Token).ConfigureAwait(false);
        }
        else
            log.Warn($"Ignoring request to update pts from {savedPts} to {pts}");
    }

    public void Dispose()
    {
        foreach (var observer in subscribers.Keys)
            observer.OnCompleted();
        client.Dispose();
    }

    public IDisposable Subscribe(IObserver<TgEvent> observer)
    {
        if (!subscribers.TryGetValue(observer, out var unsubscriber))
        {
            unsubscriber = new Unsubscriber(subscribers, observer);
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

public sealed record TgEvent(TgEventType Type, Message Message); 