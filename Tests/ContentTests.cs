using System.Text.RegularExpressions;
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
        var m = MastodonWriter.Junk().Match(sample);
        Assert.That(m.Success, Is.True);

        var g = m.Groups["junk"];
        var before = sample[..(g.Index)];
        var after = sample[(g.Index + g.Length)..];
        var text = before + after;
        Assert.That(text, Has.Length.LessThan(sample.Length));
    }

    [Test]
    public void RegexTest()
    {
        var s = "asdf qwer weriotu";
        var m = Regex.Match(s, "we");
        var g = m.Groups[0];
        var start = s[..(g.Index)];
        var end = s[(g.Index + g.Length)..];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(start, Is.EqualTo("asdf q"));
            Assert.That(end, Is.EqualTo("r weriotu"));
        }

        m = Regex.Match(s, "asd");
        g = m.Groups[0];
        start = s[..(g.Index)];
        end = s[(g.Index + g.Length)..];
        Assert.That(start, Is.EqualTo(""));
        Assert.That(end, Is.EqualTo("f qwer weriotu"));

        m = Regex.Match(s, "otu");
        g = m.Groups[0];
        start = s[..(g.Index)];
        end = s[(g.Index + g.Length)..];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(start, Is.EqualTo("asdf qwer weri"));
            Assert.That(end, Is.EqualTo(""));
        }
    }

    [TestCase("Завершается триста пятнадцатый день полномасштабной войны")]
    public void ImportantTest(string title)
    {
        var matches = MastodonWriter.Important().Matches(title);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(matches, Is.Not.Empty);
            Assert.That(MastodonWriter.Important().IsMatch(title));
        }
    }
}