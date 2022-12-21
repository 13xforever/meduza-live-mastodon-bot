using System.Collections.Concurrent;
using Mastonet;
using Mastonet.Entities;
using MeduzaRepost.Database;
using Microsoft.EntityFrameworkCore;
using NLog;
using TL;

namespace MeduzaRepost;

public class MastodonWriter: IObserver<TgEvent>, IDisposable
{
    private const string Junk = "ДАННОЕ СООБЩЕНИЕ (МАТЕРИАЛ) СОЗДАНО И (ИЛИ) РАСПРОСТРАНЕНО ИНОСТРАННЫМ СРЕДСТВОМ МАССОВОЙ ИНФОРМАЦИИ, ВЫПОЛНЯЮЩИМ ФУНКЦИИ ИНОСТРАННОГО АГЕНТА, И (ИЛИ) РОССИЙСКИМ ЮРИДИЧЕСКИМ ЛИЦОМ, ВЫПОЛНЯЮЩИМ ФУНКЦИИ ИНОСТРАННОГО АГЕНТА";
#if DEBUG
    private const Visibility Visibility = Mastonet.Visibility.Private;
#else    
    private const Visibility Visibility = Mastonet.Visibility.Public;
#endif
    
    private static readonly ILogger Log = Config.Log;
    private static readonly char[] SentenceEndPunctuation = { '.', '!', '?' };

    private readonly MastodonClient client = new(Config.Get("instance"), Config.Get("access_token"));
    private readonly BotDb db = new();
    private readonly ConcurrentQueue<TgEvent> events = new();

    private TelegramReader reader;
    private int maxLength, maxAttachments, linkReserved, maxVideoSize, maxImageSize;
    private HashSet<string> mimeTypes;
    private bool SupportsMarkdown = false;
    
    public async Task Run(TelegramReader telegramReader)
    {
        reader = telegramReader;
        var instance = await client.GetInstanceV2().ConfigureAwait(false);
        var user = await client.GetCurrentUser().ConfigureAwait(false);
        Log.Info($"We're logged in as {user.UserName} (#{user.Id}) on {client.Instance}");
        maxLength = instance.Configuration.Statutes.MaxCharacters;
        maxAttachments = instance.Configuration.Statutes.MaxMediaAttachments;
        linkReserved = instance.Configuration.Statutes.CharactersReservedPerUrl;
        mimeTypes = new(instance.Configuration.MediaAttachments.SupportedMimeTypes);
        maxVideoSize = instance.Configuration.MediaAttachments.VideoSizeLimit;
        maxImageSize = instance.Configuration.MediaAttachments.ImageSizeLimit;

        while (!Config.Cts.IsCancellationRequested)
        {
            if (events.TryDequeue(out var evt))
            {
                switch (evt.Type)
                {
                    case TgEventType.Post:
                    {
                        try
                        {
                            if (db.MessageMaps.AsNoTracking().FirstOrDefault(m => m.TelegramId == evt.Message.id) is { MastodonId.Length: > 0 })
                                return;

                            if (evt.Message.message is null or "" && evt.Message.flags.HasFlag(Message.Flags.has_grouped_id))
                                return;
                            
                            string? replyStatusId = null;
                            Attachment? attachment = null;
                            Photo? srcImg = null;
                            Document? srcDoc = null;
                            if (evt.Message.ReplyTo is { reply_to_msg_id: > 0 } replyTo)
                            {
                                if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == replyTo.reply_to_msg_id) is { MastodonId.Length: > 0 } map)
                                    replyStatusId = map.MastodonId;
                            }
                            if (evt.Message.media is MessageMediaPhoto { photo: Photo photo })
                                srcImg = photo;
                            else if (evt.Message.media is MessageMediaDocument { document: Document doc } && mimeTypes.Contains(doc.mime_type))
                                srcDoc = doc;
                            else if (evt.Message.media is MessageMediaWebPage { webpage: WebPage {photo: Photo embedImage } })
                                srcImg = embedImage;
                            else if (evt.Message.media is MessageMediaWebPage { webpage: WebPage { document: Document embedDoc } } && mimeTypes.Contains(embedDoc.mime_type))
                                srcDoc = embedDoc;
                            if (srcImg is not null)
                            {
                                try
                                {
                                    await using var memStream = Config.MemoryStreamManager.GetStream();
                                    await reader.Client.DownloadFileAsync(srcImg, memStream).ConfigureAwait(false);
                                    memStream.Seek(0, SeekOrigin.Begin);
                                    if (memStream.Length < maxImageSize)
                                        attachment = await client.UploadMedia(memStream, srcImg.id.ToString()).ConfigureAwait(false);
                                }
                                catch (Exception e)
                                {
                                    Log.Error(e, "Failedto download image");
                                }
                            }
                            else if (srcDoc?.size < maxVideoSize)
                            {
                                try
                                {
                                    await using var memStream = Config.MemoryStreamManager.GetStream();
                                    await reader.Client.DownloadFileAsync(srcDoc, memStream).ConfigureAwait(false);
                                    memStream.Seek(0, SeekOrigin.Begin);
                                    attachment = await client.UploadMedia(memStream, srcDoc.Filename ?? srcDoc.id.ToString()).ConfigureAwait(false);
                                }
                                catch (Exception e)
                                {
                                    Log.Error(e, $"Failedto download media file of type {srcDoc.mime_type}, {srcDoc.size}");
                                }
                            }

                            var (title, body) = FormatTitleAndBody(evt.Message, evt.Link);
                            var status = client.PublishStatus(
                                spoilerText: title,
                                status: body,
                                replyStatusId: replyStatusId,
                                mediaIds: attachment is null ? null : new[] { attachment.Id },
                                visibility: Visibility,
                                language: "ru"
                            ).ConfigureAwait(false).GetAwaiter().GetResult();
                            db.MessageMaps.Add(new() { TelegramId = evt.Message.id, MastodonId = status.Id });
                            await db.SaveChangesAsync().ConfigureAwait(false);
                            Log.Info($"Posted new status from {evt.Link} to {status.Url}");
                        }
                        catch (Exception e)
                        {
                            Log.Error(e);
                        }
                        break;
                    }
                    case TgEventType.Edit:
                    {
                        if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == evt.Message.id) is { MastodonId.Length: > 0 } map)
                        {
                            var status = await client.GetStatus(map.MastodonId).ConfigureAwait(false);
                            Log.Warn($"Status edit is not implemented! Please adjust content manually from {evt.Link} for {status.Url}");
                        }
                        break;
                    }
                    case TgEventType.Delete:
                    {
                        if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == evt.Message.id) is { MastodonId.Length: > 0 } map)
                            try
                            {
                                await client.DeleteStatus(map.MastodonId).ConfigureAwait(false);
                                db.MessageMaps.Remove(map);
                                await db.SaveChangesAsync().ConfigureAwait(false);
                                Log.Info($"Removed status {map.MastodonId}");
                            }
                            catch (Exception e)
                            {
                                Log.Warn(e, "Failed to delete status");
                            }
                        break;
                    }
                    case TgEventType.Pin:
                    {
                        if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == evt.Message.id) is { MastodonId.Length: > 0 } map)
                            try
                            {
                                if (db.BotState.FirstOrDefault(s => s.Key == "pin_id") is { Value.Length: > 0 } pinState)
                                {
                                    if (pinState.Value == map.MastodonId)
                                        return;
                                    
                                    var pin = await client.Unpin(pinState.Value).ConfigureAwait(false);
                                    Log.Info($"Unpinned {pin.Url}");
                                }
                                else
                                    pinState = db.BotState.Add(new() { Key = "pin_id", Value = "0" }).Entity;
                                var status = await client.Pin(map.MastodonId).ConfigureAwait(false);
                                Log.Info($"Pinned new message {status.Url}");
                                pinState.Value = status.Id;
                                await db.SaveChangesAsync().ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                Log.Warn(e, $"Failed to pin message {map.MastodonId}");
                            }
                        break;
                    }
                    default:
                    {
                        Log.Error($"Unknown event type {evt.Type}");
                        break;
                    }
                }
            }
            await Task.Delay(1000).ConfigureAwait(false);
        }
    }

    public void OnCompleted() => Config.Cts.Cancel(false);
    public void OnError(Exception e) => Log.Error(e);
    public void OnNext(TgEvent evt) => events.Enqueue(evt);

    private (string? title, string body) FormatTitleAndBody(Message message, string? link)
    {
        link ??= $"https://t.me/meduzalive/{message.id}";
        var text = message.message;
        if (message.media is MessageMediaWebPage { webpage: WebPage page })
            text += $"\n\n{page.url}";
        
        var paragraphs = text
            .Replace(Junk, "")
            .Replace("\n\n\n\n", "\n\n")
            .Replace("\n\n\n", "\n\n")
            .Split("\n")
            .ToList();
        paragraphs = Reduce(paragraphs, link);
        
        if (paragraphs.Count > 2)
        {
            var title = paragraphs[0];
            var body = string.Join('\n', paragraphs.Skip(1));
            return (title, body);
        }
        
        if (paragraphs.Count > 1)
        {
            var parts = paragraphs[0].Split('.', 2);
            if (parts.Length == 2)
                return (parts[0], string.Join('\n', new[]{parts[1].Trim()}.Concat(paragraphs.Skip(1))));
            return (null, paragraphs[0]);
        }

        throw new InvalidOperationException("We should have at least backlink in the body");
    }

    private List<string> Reduce(List<string> paragraphs, string link)
    {
        var max = maxLength - linkReserved - link.Length - 4;
        if (GetSumLength(paragraphs) < max)
        {
            paragraphs.Add($"\n🔗 {link}");
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

    private static int GetSumLength(List<string> paragraphs) => paragraphs.Sum(p => p.Length) + (paragraphs.Count - 1) * 2;

    public void Dispose() => db.Dispose();
}