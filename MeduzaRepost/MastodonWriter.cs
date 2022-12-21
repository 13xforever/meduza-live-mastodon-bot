using System.Collections.Concurrent;
using Mastonet;
using Mastonet.Entities;
using MeduzaRepost.Database;
using NLog;
using TL;

namespace MeduzaRepost;

public class MastodonWriter: IObserver<TgEvent>, IDisposable
{
    private const string Junk = "ДАННОЕ СООБЩЕНИЕ (МАТЕРИАЛ) СОЗДАНО И (ИЛИ) РАСПРОСТРАНЕНО ИНОСТРАННЫМ СРЕДСТВОМ МАССОВОЙ ИНФОРМАЦИИ, ВЫПОЛНЯЮЩИМ ФУНКЦИИ ИНОСТРАННОГО АГЕНТА, И (ИЛИ) РОССИЙСКИМ ЮРИДИЧЕСКИМ ЛИЦОМ, ВЫПОЛНЯЮЩИМ ФУНКЦИИ ИНОСТРАННОГО АГЕНТА";
#if DEBUG
    private const Visibility Visibility = Mastonet.Visibility.Direct;
#else    
    private const Visibility Visibility = Mastonet.Visibility.Public;
#endif
    
    private static readonly ILogger Log = Config.Log;
    private static readonly char[] SentenceEndPunctuation = { '.', '!', '?' };

    private readonly MastodonClient client = new(Config.Get("instance"), Config.Get("access_token"));
    private readonly BotDb db = new();
    private readonly ConcurrentQueue<TgEvent> events = new();

    private TelegramReader reader;
    private int maxLength, maxAttachments, linkReserved;
    private bool SupportsMarkdown = false;
    
    public async Task Run(TelegramReader telegramReader)
    {
        reader = telegramReader;
        var instance = await client.GetInstanceV2().ConfigureAwait(false);
        maxLength = instance.Configuration.Statutes.MaxCharacters;
        maxAttachments = instance.Configuration.Statutes.MaxMediaAttachments;
        linkReserved = instance.Configuration.Statutes.CharactersReservedPerUrl;

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
                            var text = evt.Message.message;
                            string? replyStatusId = null;
                            Attachment? attachment = null;
                            if (evt.Message.ReplyTo is { reply_to_msg_id: > 0 } replyTo)
                            {
                                if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == replyTo.reply_to_msg_id) is { MastodonId.Length: > 0 } map)
                                    replyStatusId = map.MastodonId;
                            }
                            if (evt.Message.media is MessageMediaWebPage { webpage: WebPage page })
                                text += $"\n\n{page.url}";
                            else if (evt.Message.media is MessageMediaPhoto { photo: Photo photo })
                            {
                                try
                                {
                                    await using var memStream = Config.MemoryStreamManager.GetStream();
                                    await reader.Client.DownloadFileAsync(photo, memStream).ConfigureAwait(false);
                                    memStream.Seek(0, SeekOrigin.Begin);
                                    attachment = await client.UploadMedia(memStream, photo.id.ToString()).ConfigureAwait(false);
                                }
                                catch (Exception e)
                                {
                                    Log.Error(e, "Failed to download image");
                                }
                            }
                            else if (evt.Message.media is not null)
                            {
                                Log.Warn($"Unsupported media attachment of type {evt.Message.media.GetType()}");
                            }

                            var (title, body) = FormatTitleAndBody(text, evt.Link ?? $"https://t.me/meduzalive/{evt.Message.id}");
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
                        }
                        catch (Exception e)
                        {
                            Log.Error(e);
                        }
                        break;
                    }
                    case TgEventType.Edit:
                    {
                        Log.Warn("Status edit is not implemented yet");
                        break;
                    }
                    case TgEventType.Delete:
                    {
                        if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == evt.Message.id) is { MastodonId.Length: > 0 } map)
                        {
                            try
                            {
                                await client.DeleteStatus(map.MastodonId).ConfigureAwait(false);
                                db.MessageMaps.Remove(map);
                                await db.SaveChangesAsync().ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                Log.Warn(e, "Failed to delete status");
                            }
                        }
                        break;
                    }
                }
            }
            else
                await Task.Delay(1000).ConfigureAwait(false);
        }
    }

    public void OnCompleted() => Config.Cts.Cancel(false);
    public void OnError(Exception e) => Log.Error(e);
    public void OnNext(TgEvent evt) => events.Enqueue(evt);

    private (string? title, string body) FormatTitleAndBody(string text, string link)
    {
        var paragraphs = text
            .Replace(Junk, "")
            .Replace("\n\n\n\n", "\n\n")
            .Replace("\n\n\n", "\n\n")
            .Split("\n")
            .ToList();
        paragraphs = Reduce(paragraphs, link);
        
        if (paragraphs.Count > 1)
        {
            var title = paragraphs[0];
            var body = string.Join('\n', paragraphs.Skip(1));
            return (title, body);
        }
        
        if (paragraphs.Count == 1)
        {
            var parts = paragraphs[0].Split('.', 2);
            if (parts.Length == 2)
                return (parts[0], parts[1].Trim());
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