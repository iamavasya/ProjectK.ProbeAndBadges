using System.Text;

namespace ProjectK.Catalog.Cli;

/// <summary>Human-readable summary of a v1 → v2 conversion: what was produced and what an
/// editor should look at by hand.</summary>
internal static class MigrationReport
{
    public static string Build(CatalogPackage package, List<string> notes, ValidationReport validation, List<string> unusedImages)
    {
        var b = package.Bundle;
        var sb = new StringBuilder();
        sb.AppendLine("# Звіт конвертації v1 → v2");
        sb.AppendLine();
        sb.AppendLine($"- Вмілостей: {b.Badges.Count} (затверджених {b.Badges.Count(x => x.Status == CatalogCodes.StatusApproved)}, апробаційних {b.Badges.Count(x => x.Status == CatalogCodes.StatusTrial)})");
        sb.AppendLine($"- Категорій: {b.Categories.Count}");
        sb.AppendLine($"- Вмілостей з передумовами: {b.Badges.Count(x => x.Prerequisites.Count > 0)}");
        sb.AppendLine($"- Вимоги розбито на пункти: {b.Badges.Count(x => x.Requirements.Count > 1)} з {b.Badges.Count}");
        sb.AppendLine($"- Проб: {b.Probes.Count}, точок проби: {b.Probes.Sum(p => p.Points.Count + p.Sections.Sum(s => s.Points.Count))}");
        sb.AppendLine($"- Відзначень: {b.Awards.Count}");
        sb.AppendLine($"- Зображень: {package.Images.Count}, {package.Images.Values.Sum(i => (long)i.Length) / 1024 / 1024} МБ");
        sb.AppendLine($"- Перевірка: {validation.Errors.Count()} помилок, {validation.Warnings.Count()} попереджень");
        sb.AppendLine();

        Section(sb, "Що перевірити вручну", notes);
        Section(sb, "Помилки валідації", validation.Errors.Select(i => i.ToString()));
        Section(sb, "Попередження валідації", validation.Warnings.Select(i => i.ToString()));
        Section(sb, "Зображення в теці парсера, на які нічого не посилається", unusedImages);

        var largest = package.Images
            .OrderByDescending(i => i.Value.Length)
            .Take(10)
            .Select(i => $"{i.Key}: {i.Value.Length / 1024} КБ");
        Section(sb, "Найбільші зображення", largest);

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        sb.AppendLine($"## {title} ({list.Count})");
        sb.AppendLine();
        if (list.Count == 0) sb.AppendLine("Немає.");
        foreach (var line in list) sb.AppendLine($"- {line}");
        sb.AppendLine();
    }
}
