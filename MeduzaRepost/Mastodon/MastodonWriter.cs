using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mastonet;
using Mastonet.Entities;
using MeduzaRepost.Database;
using Microsoft.EntityFrameworkCore;
using NLog;
using TL;

namespace MeduzaRepost;

public sealed class MastodonWriter: IObserver<TgEvent>, IDisposable
{
    internal static readonly Regex Junk = new(
        @"^(?<junk>(\s|«)*ДАННОЕ\s+СООБЩЕНИЕ\b.+\bВЫПОЛНЯЮЩИМ\s+ФУНКЦИИ\s+ИНОСТРАННОГО\s+АГЕНТА?(\.|\s)*)$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.ExplicitCapture
    );
    
    internal static readonly Regex Important = new("""
                (?<important>(
                    (^❗)
                    |((начинается|подходит\s+к\s+концу|завершается).+день)
                    |((принят|подписа[лн]|одобр(ил|ен)|внес(ен|ли)).+закон)
                    |(главн[ыо][ем]\s+([ко]\s+)?(фото(графи)?|событ|новост|.*\b(минут|момент)))
                    |(призыв|мобилизаци[ия]|повестк[ауе]|воинск\w+\s+уч[её]т)
                    |(ЛГБТ\+?|трансгендер)
                ))
                """,
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace
    );

    private static readonly TimeLimitedQueue PostLimitQueue = new();
    
#if DEBUG
    private const Visibility NormalVisibility = Visibility.Private;
    private const Visibility ImportantVisibility = Visibility.Private;
#else    
    private const Visibility NormalVisibility = Visibility.Unlisted;
    private const Visibility ImportantVisibility = Visibility.Public;
#endif
    
    private static readonly ILogger Log = Config.Log.WithPrefix("mastodon");
    private static readonly char[] SentenceEndPunctuation = { '.', '!', '?' };

    private readonly CustomMastodonClient client = new(Config.Get("instance")!, Config.Get("access_token")!);
    private readonly BotDb db = new();
    private readonly ConcurrentQueue<TgEvent> events = new();
    private readonly ConcurrentDictionary<long, Status> pins = new();

    private TelegramReader reader = null!;
    private int maxLength, maxAttachments, linkReserved, maxVideoSize, maxImageSize, maxDescriptionLength;
    private HashSet<string> mimeTypes = null!;
    //private bool SupportsMarkdown = false;
    
    public async Task Run(TelegramReader telegramReader)
    {
        reader = telegramReader;
        Log.Info("Trying to get mastodon information...");
        var instance = await client.GetInstanceV2().ConfigureAwait(false);
        var user = await client.GetCurrentUser().ConfigureAwait(false);
        Log.Info($"We're logged in as {user.UserName} (#{user.Id}) on {client.Instance}");
        maxLength = instance.Configuration.Statutes.MaxCharacters;
        //SupportsMarkdown = instance.Configuration.Statutes.SupportedMimeTypes?.Any(t => t == "text/markdown") is true;
        maxDescriptionLength = Math.Min(maxLength, Config.MaxDescriptionLength);
        maxAttachments = instance.Configuration.Statutes.MaxMediaAttachments;
        linkReserved = instance.Configuration.Statutes.CharactersReservedPerUrl;
        mimeTypes = new(instance.Configuration.MediaAttachments.SupportedMimeTypes);
        maxVideoSize = instance.Configuration.MediaAttachments.VideoSizeLimit;
        maxImageSize = instance.Configuration.MediaAttachments.ImageSizeLimit;
        Log.Info($"Limits: description={maxDescriptionLength}, status length={maxLength}, attachments={maxAttachments}");
        
        Log.Info("Reading mastodon pins...");
        var pinnedStatusList = await client.GetAccountStatuses(user.Id, pinned: true).ConfigureAwait(false);
        var pinIds = pinnedStatusList.Select(s => s.Id).ToList();
        var pinMaps = await db.MessageMaps.Where(m => pinIds.Contains(m.MastodonId)).ToListAsync().ConfigureAwait(false);
        foreach (var pinMap in pinMaps)
            pins[pinMap.TelegramId] = pinnedStatusList.First(s => s.Id == pinMap.MastodonId);
        Log.Info($"Got {pins.Count} pin{(pins.Count == 1 ? "" : "s")}");

        while (!Config.Cts.IsCancellationRequested)
        {
            if (events.TryDequeue(out var evt))
            {
                var success = false;
                do
                {
                    try
                    {
                        await TryToHandle(evt).ConfigureAwait(false);
                        success = true;
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Will retry in a minute");
                        await Task.Delay(TimeSpan.FromMinutes(1));
                    }
                } while (!success);
            }
            await Task.Delay(1000).ConfigureAwait(false);
        }
    }

    public void OnCompleted() => Config.Cts.Cancel(false);
    public void OnError(Exception e) => Log.Error(e);
    public void OnNext(TgEvent evt) => events.Enqueue(evt);

    private async Task TryToHandle(TgEvent evt)
    {
        switch (evt.Type)
        {
            case TgEventType.Post:
            {
                try
                {
                    if (db.MessageMaps.AsNoTracking().Any(m => m.TelegramId == evt.Group.MessageList[0].id))
                    {
                        await UpdatePts(evt.pts, evt.Group.Expected).ConfigureAwait(false);
                        return;
                    }

                    if (evt.Group.MessageList[0] is { message: null or "" } msg && msg.flags.HasFlag(Message.Flags.has_grouped_id))
                    {
                        Log.Debug("Media-only message with a group flag, skipping");
                        await UpdatePts(evt.pts, evt.Group.Expected).ConfigureAwait(false);
                        return;
                    }

                    string? replyStatusId = null;
                    msg = evt.Group.MessageList[0];
                    if (msg.ReplyTo is MessageReplyHeader { reply_to_msg_id: > 0 } replyTo)
                    {
                        if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == replyTo.reply_to_msg_id) is { MastodonId.Length: > 0 } map)
                        {
                            replyStatusId = map.MastodonId;
                            Log.Debug($"Trying to reply to {replyStatusId}");
                        }
                    }
                    var attachments = await CollectAttachmentsAsync(evt.Group).ConfigureAwait(false);
                    Log.Debug($"Collected {attachments.Count} attachment{(attachments.Count is 1 ? "" : "s")} of types: {string.Join(", ", attachments.Select(a => a.Type))}");
                    var (title, body) = FormatTitleAndBody(msg, evt.Link);
#if !DEBUG
                    var tries = 0;
                    Status? status = null;
                    do
                    {
                        try
                        {
                            status = await client.PublishStatus(
                                spoilerText: title,
                                status: body,
                                replyStatusId: replyStatusId,
                                mediaIds: attachments.Count > 0 && tries < 16 ? attachments.Select(a => a.Id) : null,
                                visibility: GetVisibility(title, body),
                                language: "ru"
                            ).ConfigureAwait(false);
                        }
                        catch (ServerErrorException e) when (e.Message is "Cannot attach files that have not finished processing. Try again in a moment!")
                        {
                            if (++tries > 15)
                                Log.Warn("📵 Failed to post with media attachments");
                            else
                            {
                                Log.Info("⏳ Waiting for media upload to be processed…");
                                await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                            }
                        }
                    } while (status is null);
                    db.MessageMaps.Add(new() { TelegramId = msg.id, MastodonId = status.Id, Pts = evt.pts });
                    await UpdatePts(evt.pts, evt.Group.Expected).ConfigureAwait(false);
                    Log.Info($"🆕 Posted new status from {evt.Link} to {status.Url} (+{evt.Group.Expected}/{evt.pts}){(status.Visibility == ImportantVisibility ? $" ({status.Visibility})" : "")}");
#else
                    Log.Info($"Posted new status from {evt.Link}");
#endif
                }
                catch (Exception e)
                {
                    Log.Error(e, $"Failed to post new status for {evt.Link}");
                    Log.Debug(client.LastErrorResponseContent);
                    throw;
                }
                break;
            }
            case TgEventType.Edit:
            {
                foreach (var message in evt.Group.MessageList)
                {
                    try
                    {
                        if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == message.id) is { MastodonId.Length: > 0 } map)
                        {
                            var status = await client.GetStatus(map.MastodonId).ConfigureAwait(false);
                            var (title, body) = FormatTitleAndBody(message, evt.Link);
                            if (title == status.SpoilerText && body == status.Text)
                            {
                                Log.Info($"Status edit did not change visible content, plz implement edit indicator ({evt.Link} → {status.Url})");
                                continue;
                            }
#if !DEBUG                            
                            status = await client.EditStatus(
                                statusId: map.MastodonId,
                                spoilerText: title,
                                status: body,
                                mediaIds: status.MediaAttachments.Select(a => a.Id),
                                language: "ru"
                            ).ConfigureAwait(false);
#endif
                            Log.Info($"📝 Updated status from {evt.Link} to {status.Url}");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, $"Failed to update status for {evt.Link}");
                    }
                }
                await UpdatePts(evt.pts, evt.Group.Expected).ConfigureAwait(false);
                break;
            }
            case TgEventType.Delete:
            {
                foreach (var message in evt.Group.MessageList)
                {
                    if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == message.id) is { MastodonId.Length: > 0 } map)
                        try
                        {
#if !DEBUG                             
                            await client.DeleteStatus(map.MastodonId).ConfigureAwait(false);
                            db.MessageMaps.Remove(map);
                            await db.SaveChangesAsync().ConfigureAwait(false);
#endif
                            Log.Info($"🗑️ Removed status {map.MastodonId}");
                        }
                        catch (Exception e)
                        {
                            Log.Warn(e, "Failed to delete status");
                            throw;
                        }
                }
                await UpdatePts(evt.pts, evt.Group.Expected).ConfigureAwait(false);
                break;
            }
            case TgEventType.Pin:
            {
                var pinList = evt.Group.MessageList.Select(m => (long)m.id).ToList();
                var toUnpin = pins.Keys.Except(pinList).ToList();
                var newPins = pinList.Except(pins.Keys).ToList();
                foreach (var id in toUnpin)
                {
                    if (pins.TryRemove(id, out var status))
                        try
                        {
                            await client.Unpin(status.Id).ConfigureAwait(false);
                            Log.Info($"🧹 Unpinned {status.Url}");
                        }
                        catch (Exception e)
                        {
                            Log.Warn(e, $"Failed to unpin {status.Url}");
                        }
                    
                }
                foreach (var id in newPins)
                {
                    if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == id) is { MastodonId.Length: > 0 } map)
                    {
                        try
                        {
                            var newStatus = await client.Pin(map.MastodonId).ConfigureAwait(false);
                            pins[id] = newStatus;
                            Log.Info($"📌 Pinned {newStatus.Url}");
                        }
                        catch (Exception e)
                        {
                            Log.Warn(e, $"Failed to pin {evt.Link} / {map.MastodonId}");
                        }
                    }
                }
                await UpdatePts(evt.pts, evt.Group.Expected).ConfigureAwait(false);
                break;
            }
            default:
            {
                Log.Error($"Unknown event type {evt.Type}");
                break;
            }
        }
    }

    private static Visibility GetVisibility(string? title, string body)
    {
        if (title is { Length: > 0 }
            && Important.IsMatch(title)
            && PostLimitQueue.TryAdd(DateTime.UtcNow))
            return ImportantVisibility;
        return NormalVisibility;
    }

    private (string? title, string body) FormatTitleAndBody(Message message, string? link)
    {
        link ??= $"https://t.me/meduzalive/{message.id}";
        var text = message.message;
        if (text is { Length: > 0 } && Junk.Match(text) is { Success: true } m)
        {
            var g = m.Groups["junk"];
            text = text[..(g.Index)] + text[(g.Index + g.Length)..];
        }
        /*else
        {
            Log.Warn($"Very sus, couldn't find a junk match in: {text}");
        }*/
        if (message.media is MessageMediaWebPage { webpage: WebPage page })
            text += $"\n\n{page.url}";
        var paragraphs = text
            .Split("\n")
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
        paragraphs = Reduce(paragraphs, link);
        
        if (paragraphs.Count > 1)
        {
            var title = paragraphs[0];
            var body = string.Join("\n\n", paragraphs.Skip(1));
            return (title, body);
        }
        
        if (paragraphs.Count == 1)
        {
            var parts = paragraphs[0].Split('.', 2);
            if (parts.Length == 2)
                return (parts[0], string.Join("\n\n", new[]{parts[1].Trim()}.Concat(paragraphs.Skip(1))));
            return (null, paragraphs[0]);
        }

        throw new InvalidOperationException("We should have at least backlink in the body");
    }

    private List<string> Reduce(List<string> paragraphs, string link)
    {
        if (paragraphs.Count > 1)
        {
            for (var i = paragraphs.Count - 1; i > 1; i--)
                if (paragraphs[i - 1].StartsWith("http")
                    && paragraphs[i - 1].StartsWith(paragraphs[i])
                    && paragraphs[i] is {Length: >10})
                {
                    paragraphs.RemoveAt(i - 1);
                    break;
                }
        }
        
        var max = maxLength - linkReserved - link.Length - 4;
        if (GetSumLength(paragraphs) < max)
        {
            paragraphs.Add($"🔗 {link}");
            return paragraphs;
        }

        max -= 4;
        while (GetSumLength(paragraphs) > max && paragraphs.Count > 1)
            paragraphs.RemoveAt(paragraphs.Count - 1);
        if (GetSumLength(paragraphs) > max)
        {
            var p = paragraphs[0];
            int idx;
            while ((idx = p[..^2].LastIndexOfAny(SentenceEndPunctuation)) > 0)
                p = p[..(idx + 1)];
            if (p.Length > max)
                p = p[..max];
            paragraphs[0] = p;
        }
        paragraphs.Add($"[…] 🔗 {link}");
        return paragraphs;
    }

    private async Task<List<Attachment>> CollectAttachmentsAsync(MessageGroup group)
    {
        var result = new List<Attachment>();
        string? firstType = null;
        foreach (var m in group.MessageList)
        {
            var info = await GetAttachmentInfoAsync(m).ConfigureAwait(false);
            if (info == default
                || info is {doc.video_thumbs: [..]} && info.data.Length > maxVideoSize
                || info is {doc.thumbs: [..]} && info.data.Length > maxImageSize
                || info.data.Length > Math.Max(maxVideoSize, maxImageSize))
                continue;

            try
            {
                var attachment = await client.UploadMedia(
                    data: info.data,
                    fileName: info.filename,
                    description: info.description
                ).ConfigureAwait(false);
                //todo: wait for processing to finish https://docs.joinmastodon.org/methods/media/#206-partial-content
                if (firstType is null)
                    firstType = attachment.Type;
                else if (attachment.Type != firstType)
                    continue;

                result.Add(attachment);
            }
            catch (Exception e)
            {
                Log.Warn(e, "Failed to upload attachment content");
                Log.Debug($"Response content: {client.LastErrorResponseContent}");
                continue;
            }
            if (result.Count == maxAttachments || firstType is "video")
                break;
        }
        return result;
    }

    private async Task<(MemoryStream data, Document? doc, string filename, string? description)> GetAttachmentInfoAsync(Message message)
    {
        Photo? srcImg = null;
        Document? srcDoc = null;
        string? attachmentDescription = null;

        if (message.media is MessageMediaPhoto { photo: Photo photo })
            srcImg = photo;
        else if (message.media is MessageMediaDocument { document: Document doc } && mimeTypes.Contains(doc.mime_type))
            srcDoc = doc;
        else if (message.media is MessageMediaWebPage { webpage: WebPage webPage })
        {
            if (webPage.photo is Photo embedImage)
                srcImg = embedImage;
            else if (webPage.document is Document embedDoc && mimeTypes.Contains(embedDoc.mime_type))
                srcDoc = embedDoc;
            attachmentDescription = webPage.description;
        }
        if (srcImg is null && srcDoc is null)
            return default;

        if (attachmentDescription?.Length > maxDescriptionLength)
            attachmentDescription = attachmentDescription[..(maxDescriptionLength-1)].Trim() + "…";

        var memStream = Config.MemoryStreamManager.GetStream();
        if (srcImg is not null)
        {
            try
            {
                await reader.Client.DownloadFileAsync(srcImg, memStream).ConfigureAwait(false);
                memStream.Seek(0, SeekOrigin.Begin);
                if (memStream.Length > maxImageSize)
                    return default;
                
                return (memStream, null, srcImg.id.ToString(), attachmentDescription);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failedto download image");
            }
        }
        else if (srcDoc?.size <= maxVideoSize)
        {
            try
            {
                await reader.Client.DownloadFileAsync(srcDoc, memStream).ConfigureAwait(false);
                memStream.Seek(0, SeekOrigin.Begin);
                return (memStream, null, srcDoc.Filename ?? srcDoc.id.ToString(), attachmentDescription);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Failedto download media file of type {srcDoc.mime_type}, {srcDoc.size}");
            }
        }
        return default;
    }

    private async Task UpdatePts(int newPts, int expectedIncrement)
    {
        var state = db.BotState.First(s => s.Key == "pts");
        var expectedState = db.BotState.FirstOrDefault(s => s.Key == "pts-next");
        var savedPts = int.Parse(state.Value!);
        var expectedPts = savedPts + 1;
        if (expectedState is { Value.Length: > 0 })
            expectedPts = int.Parse(expectedState.Value);
        else
            expectedState = db.BotState.Add(new() { Key = "pts-next", Value = expectedPts.ToString() }).Entity;
        if (newPts != expectedPts)
            Log.Warn($"Unexpected pts update: saved pts was {savedPts}, expected {expectedPts}, but new pts is {newPts}");
        if (newPts > savedPts)
        {
            state.Value = newPts.ToString();
            expectedState.Value = (newPts + expectedIncrement).ToString();
        }
        else
            Log.Warn($"Ignoring request to update pts from {savedPts} to {newPts}");
        await db.SaveChangesAsync(Config.Cts.Token).ConfigureAwait(false);
    }

    private static int GetSumLength(List<string> paragraphs) => paragraphs.Sum(p => p.Length) + (paragraphs.Count - 1) * 2;

    public void Dispose() => db.Dispose();
}