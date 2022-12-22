using MeduzaRepost;

namespace Tests;

[TestFixture]
public class ContentTests
{
    [Test]
    public void JunkFilterTests()
    {
        const string sample = """
            «Украина, как и США, пройдет свою Войну за независимость с достоинством» — Зеленский 

            ДАННОЕ СООБЩЕНИЕ (МАТЕРИАЛ) СОЗДАНО И (ИЛИ) РАСПРОСТРАНЕНО ИНОСТРАННЫМ СРЕДСТВОМ МАССОВОЙ ИНФОРМАЦИИ, ВЫПОЛНЯЮЩИМ ФУНКЦИИ ИНОСТРАННОГО АГЕНТА, И (ИЛИ) РОССИЙСКИМ ЮРИДИЧЕСКИМ ЛИЦОМ, ВЫПОЛНЯЮЩИМ ФУНКЦИИ ИНОСТРАННОГО АГЕНТА 

            Президент Украины отмечает, что многим жителям его страны придется отмечать Рождество при свечах (и, вероятно, в бомбоубежищах) — из-за российских ударов по энергосистеме. При этом все загадают одно желание: 

            «Миллионы украинцев желают победы и только победы».

            Позже Зеленский назвал эту победу «абсолютной» — как Франклин Делано Рузвельт в своей речи в декабре 1941 года.
            """;
        var m = MastodonWriter.Junk.Match(sample);
        Assert.That(m.Success, Is.True);

        var g = m.Groups["junk"];
        var before = sample[..(g.Index)];
        var after = sample[(g.Index + g.Length + 1)..];
        var text = before + after;
        Assert.That(text, Has.Length.LessThan(sample.Length));
    }
}