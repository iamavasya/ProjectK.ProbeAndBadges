using ProjectK.Catalog.Cli;

namespace ProjectK.Catalog.Tests;

public class V1ConverterTests
{
    [Fact]
    public void ParseRequirements_SplitsBoldNumberedItems_AndTakesAuthorAndTitleOut()
    {
        const string markdown = "**Авіамоделювання 2**\n\n**1.** Здобуде вмілість «Авіамоделювання 1».\n\n**2.**Знає бортову апаратуру.\n\nВмілість розробив ст.пл. Володя Максимчук, ЧоК";

        var (requirements, author, split) = V1Converter.ParseRequirements(markdown, "Авіамоделювання II");

        Assert.True(split);
        Assert.Equal(["1", "2"], requirements.Select(r => r.Id));
        Assert.Equal("Здобуде вмілість «Авіамоделювання 1».", requirements[0].Text);
        Assert.Equal("Знає бортову апаратуру.", requirements[1].Text);
        Assert.Equal("ст.пл. Володя Максимчук, ЧоК", author);
    }

    [Fact]
    public void ParseRequirements_StripsOneCellTables_AndKeepsSubBulletsWithTheirItem()
    {
        const string markdown = "| <!----> |\n| --- |\n| 1.Знати історію\n\n2. Виконай ОДНЕ з наступного:\n<br>-          Зроби лінію\n<br>-          Веди щоденник |";

        var (requirements, _, split) = V1Converter.ParseRequirements(markdown, "x");

        Assert.True(split);
        Assert.Equal(2, requirements.Count);
        Assert.Equal("Знати історію", requirements[0].Text);
        Assert.Equal("Виконай ОДНЕ з наступного:\n- Зроби лінію\n- Веди щоденник", requirements[1].Text);
    }

    [Fact]
    public void ParseRequirements_OneParagraph_IsOneRequirementAndNotFlagged()
    {
        var (requirements, _, split) = V1Converter.ParseRequirements("Мати довідку про курси.", "x");

        Assert.True(split);
        Assert.Single(requirements);
    }

    [Fact]
    public void ParseRequirements_SeveralUnnumberedLines_IsFlagged()
    {
        var (requirements, _, split) = V1Converter.ParseRequirements("**І. Перше**\n\n*а) щось*\n\n*б) інше*", "x");

        Assert.False(split);
        Assert.Single(requirements);
    }

    [Theory]
    [InlineData("aktorstvo-ii", "aktorstvo")]
    [InlineData("genealogiya-ii-proba", "genealogiya")]
    [InlineData("vporyad-ii-2", "vporyad-2")]
    [InlineData("pionirstvo", null)]
    public void FamilyOf_StripsLevel_AndKeepsDuplicateTitlesApart(string id, string? family)
    {
        Assert.Equal(family, V1Converter.FamilyOf(id));
    }

    [Theory]
    [InlineData("Впоряд ІI", "Впоряд II")]
    [InlineData("Впоряд ІІІ", "Впоряд III")]
    [InlineData("Акторство  I", "Акторство I")]
    public void CleanTitle_UsesLatinRomanNumerals(string title, string expected)
    {
        Assert.Equal(expected, V1Converter.CleanTitle(title));
    }

    [Theory]
    [InlineData("ср, 29.04.2020", "2020-04-29")]
    [InlineData("", null)]
    [InlineData("31.02.2020", null)]
    public void ParseDate_ReadsParserDates(string value, string? expected)
    {
        Assert.Equal(expected, V1Converter.ParseDate(value));
    }

    [Theory]
    [InlineData("Сноубординг та лещетарство", "snoubordynh-ta-leshchetarstvo")]
    [InlineData("Рольового руху", "rolovoho-rukhu")]
    public void Slugify_Transliterates(string title, string slug)
    {
        Assert.Equal(slug, V1Converter.Slugify(title));
    }

    [Fact]
    public void Convert_LinksPrerequisites_SplitsGluedCategories_AndKeepsIds()
    {
        const string badges = """
            { "Badges": [
              { "Id": "aktorstvo-i", "Title": "Акторство I", "ImagePath": "/x/a1.svg", "Country": "Україна",
                "Specialization": "Мистецька", "Status": "Затверджена", "Level": 1, "LastUpdated": "вт, 28.04.2020",
                "SeekerRequirements": "1. Прочитати п'єсу.", "InstructorRequirements": "", "FixNotes": [] },
              { "Id": "aktorstvo-ii", "Title": "Акторство ІI", "ImagePath": "/x/a2.svg", "Country": "Польща",
                "Specialization": "МистецькаСпортивна", "Status": "Апробаційна", "Level": 2, "LastUpdated": "",
                "SeekerRequirements": "1. Здобуде вмілість «Акторство 1».\n2. Зіграє роль.", "InstructorRequirements": null, "FixNotes": [] }
            ] }
            """;
        const string probes = """{ "Probes": [ { "Id": "probe-1", "Title": "Перша проба", "Points": [], "Sections": [ { "Id": "s-a", "Code": "А", "Title": "А", "Points": [ { "Id": "p-1", "Title": "Знає" } ] } ] } ] }""";

        var converter = new V1Converter();
        var bundle = converter.Convert(badges, probes, [1, 2]);

        var second = bundle.Badges.Single(b => b.Id == "aktorstvo-ii");
        Assert.Equal(["aktorstvo-i"], second.Prerequisites);
        Assert.Equal(["mystetska", "sportyvna"], second.Categories);
        Assert.Equal("Акторство II", second.Title);
        Assert.Equal("pl", second.Krai);
        Assert.Equal(CatalogCodes.StatusTrial, second.Status);
        Assert.Equal("p-1", bundle.Probes.Single().Sections.Single().Points.Single().Id);
        Assert.Equal(["upyu-vidznachennia-1", "upyu-vidznachennia-2"], bundle.Awards.Select(a => a.Id));
        Assert.Contains(converter.Report, line => line.StartsWith("aktorstvo-ii: спеціалізацію", StringComparison.Ordinal));
    }
}
