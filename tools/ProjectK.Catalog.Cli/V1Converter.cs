using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectK.Catalog.Cli;

/// <summary>
/// One-off conversion of the parser's <c>badges.json</c>, the library's <c>probes.seed.json</c>
/// and the embedded УПЮ award images into catalog v2. Ids of badges and probe points are kept
/// exactly, because Lilejka stores progress against them. Everything the converter had to
/// guess goes to <see cref="Report"/> for a human to check.
/// </summary>
internal sealed partial class V1Converter
{
    public List<string> Report { get; } = [];

    /// <summary>Image file name in the v2 catalog → file name in the parser's folder. They differ
    /// when the parser kept characters (ʼ, spaces) that are not allowed in v2 image names.</summary>
    public Dictionary<string, string> ImageSources { get; } = new(StringComparer.Ordinal);

    // v1 records, read loosely: the parser's output is the input here, not a contract.
    private sealed record V1File(List<V1Badge> Badges);
    private sealed record V1Badge(
        string Id,
        string Title,
        string? ImagePath,
        string? Country,
        string? Specialization,
        string? Status,
        int? Level,
        string? LastUpdated,
        string? SeekerRequirements,
        string? InstructorRequirements,
        List<string>? FixNotes);
    private sealed record V1ProbesFile(List<V1Probe> Probes);
    private sealed record V1Probe(string Id, string Title, List<V1Point> Points, List<V1Section>? Sections);
    private sealed record V1Section(string Id, string Code, string Title, List<V1Point> Points);
    private sealed record V1Point(string Id, string Title);

    private static readonly JsonSerializerOptions V1Json = new() { PropertyNameCaseInsensitive = true };

    public CatalogBundle Convert(string badgesJson, string probesJson, IReadOnlyCollection<int> awardLevels)
    {
        var v1 = JsonSerializer.Deserialize<V1File>(badgesJson, V1Json)!;
        var probes = JsonSerializer.Deserialize<V1ProbesFile>(probesJson, V1Json)!;

        var categories = new Dictionary<string, CatalogCategory>(StringComparer.Ordinal);
        var badges = v1.Badges.Select(b => ConvertBadge(b, categories)).ToList();
        ResolvePrerequisites(v1.Badges, badges);

        return new CatalogBundle
        {
            Source = new BundleSource
            {
                Name = "Пласт: бібліотека вмілостей, проби й відзначення УПЮ",
                Generator = "catalog convert-v1",
            },
            Categories = categories.Values.OrderBy(c => c.Title, StringComparer.Create(new CultureInfo("uk-UA"), false)).ToList(),
            Badges = badges,
            Probes = probes.Probes.Select((p, i) => ConvertProbe(p, i + 1)).ToList(),
            Awards = awardLevels.Order().Select(ConvertAward).ToList(),
        };
    }

    public static string AwardImageName(int level, bool colored)
    {
        return $"upyu-vidznachennia-{level}{(colored ? "" : "-bw")}.png";
    }

    private BadgeDefinition ConvertBadge(V1Badge b, Dictionary<string, CatalogCategory> categories)
    {
        var notes = new List<string>(b.FixNotes ?? []);

        var badgeCategories = ConvertCategories(b, categories, notes);
        var krai = b.Country?.Trim() switch
        {
            "Україна" => "ua",
            "Польща" => "pl",
            _ => null,
        };
        if (krai is null) Note(b.Id, notes, $"невідомий край '{b.Country}', поле krai порожнє");

        var status = b.Status?.Trim() switch
        {
            "Затверджена" => CatalogCodes.StatusApproved,
            "Апробаційна" => CatalogCodes.StatusTrial,
            _ => null,
        };
        if (status is null) Note(b.Id, notes, $"невідомий статус '{b.Status}', поставлено trial");

        var (requirements, author, split) = ParseRequirements(b.SeekerRequirements ?? string.Empty, b.Title);
        if (!split) Note(b.Id, notes, "вимоги не вдалося розбити на пункти, лишено одним текстом");
        if (requirements.Count == 0) Note(b.Id, notes, "немає вимог");

        var original = string.IsNullOrWhiteSpace(b.ImagePath) ? null : Path.GetFileName(b.ImagePath);
        var image = original is null ? null : SafeImageName(original);
        if (original is null) Note(b.Id, notes, "немає зображення");
        else ImageSources[image!] = original;

        var updatedOn = ParseDate(b.LastUpdated);
        if (updatedOn is null && !string.IsNullOrWhiteSpace(b.LastUpdated)) Note(b.Id, notes, $"не розпізнано дату '{b.LastUpdated}'");

        var instructor = CleanMarkdown(b.InstructorRequirements ?? string.Empty);

        return new BadgeDefinition
        {
            Id = b.Id,
            Family = FamilyOf(b.Id),
            Level = b.Level,
            Title = CleanTitle(b.Title),
            Categories = badgeCategories,
            Ulady = [CatalogCodes.UladYouth],
            Krai = krai,
            Status = status ?? CatalogCodes.StatusTrial,
            Images = new ImageSet { Color = image },
            Requirements = requirements,
            InstructorRequirements = instructor.Length == 0 ? null : instructor,
            Author = author,
            UpdatedOn = updatedOn,
            Notes = notes,
        };
    }

    [GeneratedRegex(@"(?<=[а-яіїєґ'’])(?=[А-ЯІЇЄҐ])")]
    private static partial Regex GluedWords();

    /// <summary>The parser glues several specializations into one string
    /// ("Альпінізм та скелелазінняСпортивна"); split them back into separate categories.</summary>
    private List<string> ConvertCategories(V1Badge b, Dictionary<string, CatalogCategory> categories, List<string> notes)
    {
        var raw = b.Specialization?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            Note(b.Id, notes, "немає спеціалізації");
            return [];
        }

        var titles = GluedWords().Split(raw).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (titles.Count > 1)
        {
            Report.Add($"{b.Id}: спеціалізацію '{raw}' розділено на {string.Join(", ", titles.Select(t => $"'{t}'"))}");
        }

        var ids = new List<string>();
        foreach (var title in titles)
        {
            var id = Slugify(title);
            categories.TryAdd(id, new CatalogCategory { Id = id, Title = title });
            ids.Add(id);
        }

        return ids;
    }

    private static ProbeDefinition ConvertProbe(V1Probe p, int order)
    {
        static List<ProbePoint> Points(IEnumerable<V1Point> points) =>
            points.Select(x => new ProbePoint { Id = x.Id, Text = x.Title.Trim() }).ToList();

        return new ProbeDefinition
        {
            Id = p.Id,
            Title = p.Title.Trim(),
            Ulad = CatalogCodes.UladYouth,
            Krai = "ua",
            Order = order,
            Points = Points(p.Points),
            Sections = (p.Sections ?? [])
                .Select(s => new ProbeSection { Id = s.Id, Code = s.Code, Title = s.Title.Trim(), Points = Points(s.Points) })
                .ToList(),
        };
    }

    private static AwardDefinition ConvertAward(int level)
    {
        string[] ordinals = ["Перше", "Друге", "Третє", "Четверте", "П'яте"];
        return new AwardDefinition
        {
            Id = $"upyu-vidznachennia-{level}",
            Group = "upyu-vidznachennia",
            Title = $"{ordinals[level - 1]} відзначення",
            Ulad = CatalogCodes.UladYouth,
            Krai = "ua",
            Level = level,
            Images = new ImageSet { Color = AwardImageName(level, true), Bw = AwardImageName(level, false) },
        };
    }

    [GeneratedRegex(@"[^a-zA-Z0-9._-]+")]
    private static partial Regex UnsafeFileNameChars();

    internal static string SafeImageName(string name)
    {
        return UnsafeFileNameChars().Replace(name.Replace("ʼ", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal), "-");
    }

    [GeneratedRegex(@"^\s*(?:\*\*)?\s*(\d{1,2})\s*[.)]\s*(?:\*\*)?\s*(.*)$")]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"^\s*\*\*[^*]+\*\*\s*$")]
    private static partial Regex BoldOnlyLine();

    [GeneratedRegex(@"^\s*Вмілість\s+розроби\w*\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorLine();

    /// <summary>Splits the parser's markdown into numbered requirements. Returns
    /// <c>split = false</c> when no numbering was found and the text stays one requirement.</summary>
    internal static (List<Requirement> Requirements, string? Author, bool Split) ParseRequirements(string markdown, string title)
    {
        var text = CleanMarkdown(markdown);
        var items = new List<(string Number, StringBuilder Text)>();
        var intro = new StringBuilder();
        string? author = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (AuthorLine().Match(line) is { Success: true } a)
            {
                author = a.Groups[1].Value.Trim();
                continue;
            }

            if (items.Count == 0 && intro.Length == 0 && BoldOnlyLine().IsMatch(line))
            {
                // "**Авіамоделювання 2**": the parser repeats the badge's title as a heading.
                continue;
            }

            if (NumberedItem().Match(line) is { Success: true } m && m.Groups[2].Value.Trim().Length > 0)
            {
                items.Add((m.Groups[1].Value, new StringBuilder(m.Groups[2].Value.Trim())));
                continue;
            }

            var target = items.Count > 0 ? items[^1].Text : intro;
            if (target.Length > 0) target.Append('\n');
            target.Append(line.Trim());
        }

        if (items.Count == 0)
        {
            var whole = intro.ToString().Trim();
            // One paragraph is one requirement; several lines without numbering need a human.
            return (whole.Length == 0 ? [] : [new Requirement { Id = "1", Text = whole }], author, !whole.Contains('\n'));
        }

        var numbersUnique = items.Select(i => i.Number).Distinct().Count() == items.Count;
        var requirements = new List<Requirement>();
        if (intro.Length > 0)
        {
            requirements.Add(new Requirement { Id = "intro", Text = intro.ToString().Trim() });
        }

        requirements.AddRange(items.Select((item, i) => new Requirement
        {
            Id = numbersUnique ? item.Number : (i + 1).ToString(CultureInfo.InvariantCulture),
            Text = item.Text.ToString().Trim(),
        }));

        return (requirements, author, true);
    }

    [GeneratedRegex(@"^\|\s*(?:<!---->|---)?\s*\|?\s*$")]
    private static partial Regex TableNoiseLine();

    [GeneratedRegex(@"^-\s{2,}")]
    private static partial Regex WideBullet();

    /// <summary>Removes what the HTML-to-markdown step of the parser left behind: one-cell
    /// tables around the text, &lt;br&gt; tags, empty comments, padded bullets.</summary>
    internal static string CleanMarkdown(string markdown)
    {
        var text = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("<!---->", string.Empty, StringComparison.Ordinal)
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace(' ', ' ');

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (TableNoiseLine().IsMatch(line))
            {
                continue;
            }

            if (line.StartsWith('|')) line = line[1..].TrimStart();
            if (line.EndsWith('|')) line = line[..^1].TrimEnd();
            line = WideBullet().Replace(line, "- ");
            lines.Add(line);
        }

        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@"(\d{2})\.(\d{2})\.(\d{4})")]
    private static partial Regex V1Date();

    internal static string? ParseDate(string? value)
    {
        if (value is null || V1Date().Match(value) is not { Success: true } m) return null;
        var iso = $"{m.Groups[3].Value}-{m.Groups[2].Value}-{m.Groups[1].Value}";
        return DateOnly.TryParseExact(iso, "yyyy-MM-dd", out _) ? iso : null;
    }

    [GeneratedRegex(@"^(?<base>.+?)-(?:i|ii|iii|iv|v)(?<copy>-\d+)?(?:-proba)?$")]
    private static partial Regex LevelSuffix();

    /// <summary>"vporyad-ii" → "vporyad"; "vporyad-ii-2" (a second badge with the same title,
    /// from another krai) → "vporyad-2", so the two families stay apart.</summary>
    internal static string? FamilyOf(string id)
    {
        var m = LevelSuffix().Match(id);
        return m.Success ? m.Groups["base"].Value + m.Groups["copy"].Value : null;
    }

    [GeneratedRegex(@"(?<=\s)[ІIі]{1,3}(?=$|[\s.,;)])")]
    private static partial Regex RomanLevel();

    /// <summary>"Впоряд ІI" (Cyrillic І mixed with Latin I) → "Впоряд II".</summary>
    internal static string CleanTitle(string title)
    {
        return RomanLevel().Replace(Regex.Replace(title.Trim(), @"\s+", " "), m => new string('I', m.Value.Length));
    }

    /// <summary>"Здобуде вмілість «Авіамоделювання 1»" becomes a link to avtoznavstvo-i. Only
    /// requirements that talk about earning a badge are searched, and the longest title wins,
    /// so "Акторство I" never matches inside "Акторство II".</summary>
    private void ResolvePrerequisites(List<V1Badge> source, List<BadgeDefinition> badges)
    {
        var byTitle = source
            .GroupBy(b => NormalizeTitle(b.Title))
            .Where(g => g.Key.Length > 3)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Id).ToList());
        var titles = byTitle.Keys.OrderByDescending(t => t.Length).ToList();

        for (var i = 0; i < badges.Count; i++)
        {
            var badge = badges[i];
            var found = new List<string>();
            foreach (var requirement in badge.Requirements)
            {
                var text = NormalizeTitle(requirement.Text);
                if (!text.Contains("здобу", StringComparison.Ordinal) || !text.Contains("вміліст", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var title in titles)
                {
                    var at = IndexOfWord(text, title);
                    if (at < 0) continue;
                    text = text.Remove(at, title.Length).Insert(at, new string(' ', title.Length));
                    var candidates = byTitle[title].Where(id => id != badge.Id).ToList();
                    if (candidates.Count == 1) found.Add(candidates[0]);
                    else if (candidates.Count > 1)
                    {
                        // Same title in two krai: prefer the badge's own family, then its own krai.
                        var sameFamily = candidates.Where(id => badges.First(b => b.Id == id).Family == badge.Family).ToList();
                        if (sameFamily.Count > 0) candidates = sameFamily;
                        var sameKrai = candidates.Count == 1
                            ? candidates
                            : candidates.Where(id => badges.First(b => b.Id == id).Krai == badge.Krai).ToList();
                        if (sameKrai.Count == 1) found.Add(sameKrai[0]);
                        else Note(badge.Id, badge.Notes, $"передумова '{title}' неоднозначна: {string.Join(", ", candidates)}");
                    }
                }
            }

            if (found.Count > 0)
            {
                badges[i] = badge with { Prerequisites = found.Distinct().ToList() };
            }
        }
    }

    private static int IndexOfWord(string text, string word)
    {
        var at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            var end = at + word.Length;
            var beforeOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var afterOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeOk && afterOk) return at;
            at++;
        }

        return -1;
    }

    [GeneratedRegex(@"\b([1-5])\b")]
    private static partial Regex ArabicLevel();

    internal static string NormalizeTitle(string value)
    {
        var text = value.ToLowerInvariant()
            .Replace('«', ' ').Replace('»', ' ').Replace('"', ' ').Replace('“', ' ').Replace('”', ' ')
            .Replace('’', '\'').Replace('ʼ', '\'')
            .Replace("*", string.Empty, StringComparison.Ordinal);
        text = ArabicLevel().Replace(text, m => m.Value switch
        {
            "1" => "i",
            "2" => "ii",
            "3" => "iii",
            "4" => "iv",
            _ => "v",
        });

        // Latin I/V in titles are sometimes typed as Cyrillic І.
        text = Regex.Replace(text, @"(?<=\s)([іi]{1,3})(?=$|[\s.,;)])", m => new string('i', m.Value.Length));
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private void Note(string id, List<string> notes, string message)
    {
        notes.Add(message);
        Report.Add($"{id}: {message}");
    }

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "h", ['ґ'] = "g", ['д'] = "d", ['е'] = "e", ['є'] = "ie",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "y", ['і'] = "i", ['ї'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l",
        ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ь'] = "", ['ю'] = "iu",
        ['я'] = "ia", ['\''] = "", ['’'] = "", ['ʼ'] = "",
    };

    /// <summary>Ukrainian national transliteration (2010), lowercased, words joined by '-'.</summary>
    internal static string Slugify(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value.ToLowerInvariant())
        {
            if (Translit.TryGetValue(c, out var latin)) sb.Append(latin);
            else if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else sb.Append('-');
        }

        return Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
    }
}
