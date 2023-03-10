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
                    |(главные\s+(фото(графии)?|события))
                    |(призыв|мобилизаци[ия])
                ))
                """,
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace
    );
    
#if DEBUG
    private const Visibility NormalVisibility = Visibility.Private;
    private const Visibility ImportantVisibility = Visibility.Private;
#else    
    private const Visibility NormalVisibility = Visibility.Unlisted;
    private const Visibility ImportantVisibility = Visibility.Public;
#endif
    
    private static readonly ILogger Log = Config.Log;
    private static readonly char[] SentenceEndPunctuation = { '.', '!', '?' };

    private readonly MastodonClient client = new(Config.Get("instance")!, Config.Get("access_token")!);
    private readonly BotDb db = new();
    private readonly ConcurrentQueue<TgEvent> events = new();

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
        maxDescriptionLength = 1500;
        maxAttachments = instance.Configuration.Statutes.MaxMediaAttachments;
        linkReserved = instance.Configuration.Statutes.CharactersReservedPerUrl;
        mimeTypes = new(instance.Configuration.MediaAttachments.SupportedMimeTypes);
        maxVideoSize = instance.Configuration.MediaAttachments.VideoSizeLimit;
        maxImageSize = instance.Configuration.MediaAttachments.ImageSizeLimit;

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
                        await UpdatePts(evt.pts).ConfigureAwait(false);
                        return;
                    }

                    if (evt.Group.MessageList[0] is { message: null or "" } msg && msg.flags.HasFlag(Message.Flags.has_grouped_id))
                    {
                        Log.Debug("Media-only message with a group flag, skipping");
                        await UpdatePts(evt.pts).ConfigureAwait(false);
                        return;
                    }

                    string? replyStatusId = null;
                    msg = evt.Group.MessageList[0];
                    if (msg.ReplyTo is { reply_to_msg_id: > 0 } replyTo)
                    {
                        if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == replyTo.reply_to_msg_id) is { MastodonId.Length: > 0 } map)
                        {
                            replyStatusId = map.MastodonId;
                            Log.Debug($"Trying to reply to {replyStatusId}");
                        }
                    }
                    var attachments = await CollectAttachmentsAsync(evt.Group).ConfigureAwait(false);
                    var (title, body) = FormatTitleAndBody(msg, evt.Link);
                    var status = await client.PublishStatus(
                        spoilerText: title,
                        status: body,
                        replyStatusId: replyStatusId,
                        mediaIds: attachments.Count > 0 ? attachments.Select(a => a.Id) : null,
                        visibility: GetVisibility(title, body),
                        language: "ru"
                    ).ConfigureAwait(false);
                    db.MessageMaps.Add(new() { TelegramId = msg.id, MastodonId = status.Id });
                    await UpdatePts(evt.pts).ConfigureAwait(false);
                    Log.Info($"Posted new status from {evt.Link} to {status.Url}{(status.Visibility == ImportantVisibility ? $" ({status.Visibility})" : "")}");
                }
                catch (Exception e)
                {
                    Log.Error(e, $"Failed to post new status for {evt.Link}");
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
                            status = await client.EditStatus(
                                statusId: map.MastodonId,
                                spoilerText: title,
                                status: body,
                                mediaIds: status.MediaAttachments.Select(a => a.Id),
                                language: "ru"
                            ).ConfigureAwait(false);
                            Log.Info($"Updated status from {evt.Link} to {status.Url}");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, $"Failed to update status for {evt.Link}");
                    }
                }
                await UpdatePts(evt.pts).ConfigureAwait(false);
                break;
            }
            case TgEventType.Delete:
            {
                foreach (var message in evt.Group.MessageList)
                {
                    if (db.MessageMaps.FirstOrDefault(m => m.TelegramId == message.id) is { MastodonId.Length: > 0 } map)
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
                            throw;
                        }
                }
                await UpdatePts(evt.pts).ConfigureAwait(false);
                break;
            }
            case TgEventType.Pin:
            {
                Log.Warn("Pins are not implemented");
                break;
            }
            default:
            {
                Log.Error($"Unknown event type {evt.Type}");
                break;
            }
        }
    }

    private Visibility GetVisibility(string? title, string body)
    {
        if (title is { Length: > 0 } && Important.IsMatch(title))
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
        foreach (var m in group.MessageList)
        {
            var info = await GetAttachmentInfoAsync(m).ConfigureAwait(false);
            if (info == default)
                continue;
            
            var attachment = await client.UploadMedia(
                data: info.data,
                fileName: info.filename,
                description: info.description
            ).ConfigureAwait(false);
            result.Add(attachment);
            if (result.Count == maxAttachments)
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
            attachmentDescription = attachmentDescription[..1499].Trim() + "…";

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

    private async Task UpdatePts(int pts)
    {
        var state = db.BotState.First(s => s.Key == "pts");
        var savedPts = int.Parse(state.Value!);
        if (pts != savedPts + 1)
            Log.Warn($"Unexpected pts update: saved pts was {savedPts} and new pts is {pts}");
        if (pts > savedPts)
            state.Value = pts.ToString();
        else
            Log.Warn($"Ignoring request to update pts from {savedPts} to {pts}");
        await db.SaveChangesAsync(Config.Cts.Token).ConfigureAwait(false);
    }

    private static int GetSumLength(List<string> paragraphs) => paragraphs.Sum(p => p.Length) + (paragraphs.Count - 1) * 2;

    public void Dispose() => db.Dispose();
}