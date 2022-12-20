using Mastonet;

namespace MeduzaRepost;

public class MastodonWriter
{
    private const string Junk = "\n\nДАННОЕ СООБЩЕНИЕ (МАТЕРИАЛ) СОЗДАНО И (ИЛИ) РАСПРОСТРАНЕНО ИНОСТРАННЫМ СРЕДСТВОМ МАССОВОЙ ИНФОРМАЦИИ, ВЫПОЛНЯЮЩИМ ФУНКЦИИ ИНОСТРАННОГО АГЕНТА, И (ИЛИ) РОССИЙСКИМ ЮРИДИЧЕСКИМ ЛИЦОМ, ВЫПОЛНЯЮЩИМ ФУНКЦИИ ИНОСТРАННОГО АГЕНТА";

    public async Task Run()
    {
        var client = new MastodonClient(Config.Get("instance"), Config.Get("access_token"));
        var instance = await client.GetInstance();
        var scheduledStatus = await client.PublishStatus(
            spoilerText: "HTML test",
            status: """<p><ul><li>list</li><li>list</li></ul></p><p><a href="https://mastodon.ml">link</a></p>""",
            visibility: Visibility.Private,
            language: "en"
        );
        
        Console.WriteLine(scheduledStatus.Id);
    }
}