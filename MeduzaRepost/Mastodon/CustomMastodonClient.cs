using Mastonet;

namespace MeduzaRepost;

public class CustomMastodonClient:MastodonClient
{
    public string LastErrorResponseContent { get; private set; }
    
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
            LastErrorResponseContent = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            throw;
        }
    }
}