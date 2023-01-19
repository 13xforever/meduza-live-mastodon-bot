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

    static TelegramReader() => Helpers.Log = (level, message) => Config.SpamLog.Log(LogLevel.FromOrdinal(level), message);
    
    public async Task Run()
    {
        Log.Info("Trying to log into telegram account...");
        var bot = await Client.LoginUserIfNeeded().ConfigureAwait(false);
        Log.Info($"We are logged in as {bot} (id {bot.id}) on telegram");
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
            long lastGroupId = 0;
            while (await Client.Updates_GetChannelDifference(channel, null, diffPts).ConfigureAwait(false) is {} baseDiff)
            {
                if (baseDiff is Updates_ChannelDifferenceEmpty)
                    break;
                if (baseDiff is Updates_ChannelDifference diff)
                {
                    Log.Info($"Got {diff.NewMessages.Length} new messages and {diff.OtherUpdates.Length} other updates, {(diff.Final ? "" : "not ")}final");
                    foreach (var message in diff.NewMessages.OfType<Message>().OrderBy(m => m.Date))
                    {
                        if (message.flags.HasFlag(Message.Flags.has_grouped_id))
                        {
                            if (message.grouped_id == lastGroupId)
                                continue;
                            
                            lastGroupId = message.grouped_id;
                            var group = diff.NewMessages.OfType<Message>().Where(m => m.grouped_id == lastGroupId).ToList();
                            var groupLink = await Client.Channels_ExportMessageLink(channel, group[0].id, true).ConfigureAwait(false);
                            Push(new(TgEventType.Post, new(group), diffPts, groupLink.link));
                            Log.Info($"Assembled message group {lastGroupId} of {group.Count} messages from channel diff");
                            continue;
                        }
                        lastGroupId = 0;
                        var link = await Client.Channels_ExportMessageLink(channel, message.id).ConfigureAwait(false);
                        Push(new(TgEventType.Post, new(message), diffPts, link.link));
                    }
                    foreach (var update in diff.OtherUpdates)
                        await OnUpdate(update).ConfigureAwait(false);
                    diffPts = diff.pts;
                }
                else
                    Log.Warn($"Unsupported channel difference update of type {baseDiff.GetType().Name}");
                if (baseDiff.Final)
                    break;
            }
            savedPts = diffPts;
            state.Value = savedPts.ToString();
            Log.Info("All missed updates are processed");
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

        Log.Info("Listening to live telegram updates...");
        Client.OnUpdate += OnUpdate;

        while (!Config.Cts.IsCancellationRequested)
            await Task.Delay(200).ConfigureAwait(false);
    }

    private async Task OnUpdate(IObject arg)
    {
        try
        {
            if (arg is not UpdatesBase updates)
                return;

            if (updates.UpdateList.Length > 1)
                Log.Info($"Received {updates.UpdateList.Length} updates");
            else
                Log.Debug($"Received {updates.UpdateList.Length} update");
            var processedGroups = new HashSet<long>();
            foreach (var update in updates.UpdateList)
            {
                switch (update)
                {
                    case UpdateNewMessage u when u.message.Peer.ID == channel.ID:
                    {
                        Log.Debug($"Processing NewMessage update, pts={u.pts}, count={u.pts_count}");
                        if (u.message is not Message msg)
                        {
                            Log.Warn($"Invalid message type {u.message.GetType().Name} in {nameof(UpdateNewMessage)}, skipping");
                            return;
                        }

                        if (u.pts_count > 1)
                            Log.Warn($"Got update with large pts_count {u.pts_count} for message {u.message.ID}, group id {msg.grouped_id}");
                        if (msg.flags.HasFlag(Message.Flags.has_grouped_id))
                        {
                            if (!processedGroups.Add(msg.grouped_id))
                            {
                                Log.Debug($"Skipping message {msg.id} from already processed group {msg.grouped_id}");
                                continue;
                            }

                            var groupedUpdates = updates.UpdateList
                                .OfType<UpdateNewMessage>()
                                .Select(up => (u, m: (Message)up.message))
                                .Where(t => t.m.flags.HasFlag(Message.Flags.has_grouped_id) && t.m.grouped_id == msg.grouped_id)
                                .ToList();
                            var group = new MessageGroup(msg.grouped_id, groupedUpdates.Select(t => t.m).ToList());
                            var groupLink = await Client.Channels_ExportMessageLink(channel, msg.id, true).ConfigureAwait(false);
                            Log.Info($"Created new message group {group.Id} of expected size {group.Expected}");
                            Push(new(TgEventType.Post, group, groupedUpdates.Select(t => t.u.pts).Max(), groupLink.link));
                            if (updates.UpdateList.Length == group.MessageList.Count)
                                return;
                        }
                        else
                        {
                            var link = await Client.Channels_ExportMessageLink(channel, u.message.ID).ConfigureAwait(false);
                            Push(new(TgEventType.Post, new(0, 1, msg), u.pts, link.link));
                        }
                        break;
                    }
                    case UpdateEditMessage u when u.message.Peer.ID == channel.ID:
                    {
                        Log.Debug($"Processing EditMessage update, pts={u.pts}, count={u.pts_count}");
                        var link = await Client.Channels_ExportMessageLink(channel, u.message.ID, ((Message)u.message).flags.HasFlag(Message.Flags.has_grouped_id)).ConfigureAwait(false);
                        Push(new(TgEventType.Edit, new((Message)u.message), u.pts, link.link));
                        break;
                    }
                    case UpdateDeleteMessages u:
                    {
                        Log.Debug($"Processing DeleteMessage update, pts={u.pts}, count={u.pts_count}");
                        Push(new(TgEventType.Delete, new(u.messages), u.pts));
                        break;
                    }
                    case UpdatePinnedChannelMessages u:
                    {
                        Log.Debug($"Processing PinnedMessages update, pts={u.pts}, count={u.pts_count}");
                        Push(new(TgEventType.Pin, new(u.messages), u.pts));
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
        catch (Exception e)
        {
            Log.Error(e);
            throw;
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

public sealed record TgEvent(TgEventType Type, MessageGroup Group, int pts, string? Link = null);

public sealed class MessageGroup
{
    public long Id { get; }
    public int Expected { get; }
    public List<Message> MessageList { get; }

    public MessageGroup(Message firstMessage)
    {
        Id = 0;
        Expected = 1;
        MessageList = new(1) { firstMessage };
    }
    
    public MessageGroup(List<Message> messages)
    {
        Id = 0;
        Expected = messages.Count;
        MessageList = messages;
    }

    public MessageGroup(long groupId, List<Message> messages)
    {
        Id = groupId;
        Expected = messages.Count;
        MessageList = messages;
    }
    
    public MessageGroup(long groupId, int expectedCount, Message firstMessage)
    {
        Id = groupId;
        Expected = expectedCount;
        MessageList = new(expectedCount) { firstMessage };
    }
    
    public MessageGroup(int[] messageIds)
    {
        Id = 0;
        Expected = messageIds.Length;
        MessageList = messageIds.Select(id => new Message { id = id }).ToList();
    }
}