using System.Text;
using Mastonet;

namespace MeduzaRepost;

public class CustomMastodonClient:MastodonClient
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    
    public string? LastErrorResponseContent { get; private set; }
    
    public CustomMastodonClient(string instance, string accessToken) : base(instance, accessToken)
    { }

    public CustomMastodonClient(string instance, string accessToken, HttpClient client) : base(instance, accessToken, client)
    { }

    protected override void OnResponseReceived(HttpResponseMessage response)
    {
        response.Content.LoadIntoBufferAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        try
        {
            base.OnResponseReceived(response);
        }
        catch
        {
            using var bufferCopy = new MemoryStream();
            response.Content.CopyTo(bufferCopy, null, CancellationToken.None);
            bufferCopy.Seek(0, SeekOrigin.Begin);
            LastErrorResponseContent = Utf8.GetString(bufferCopy.ToArray());
            throw;
        }
    }
}