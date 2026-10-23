using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SbStudio.Core;

/// <summary>One interface language: the strings of a Lang/*.json file.</summary>
public sealed class Language
{
    private readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);

    public Language(string code)
    {
        Code = code;
        Name = code;
    }

    /// <summary>The file name without its extension, such as "zh-Hans".</summary>
    public string Code { get; }

    /// <summary>Shown in the language menu, written in the language itself ("$name" in the file).</summary>
    public string Name { get; private set; }

    /// <summary>
    /// File in the system font folder that has the characters of this language, for scripts the interface
    /// font lacks ("$font" in the file).
    /// </summary>
    public string Font { get; private set; } = "";

    public IReadOnlyDictionary<string, string> Strings => strings;

    /// <summary>Adds the strings of a language file; a string that is already there is replaced.</summary>
    internal void Merge(string json)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var document = JsonDocument.Parse(json, options);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("a language file must be one JSON object");
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            string value = property.Value.GetString()!;
            if (property.Name == "$name") Name = value;
            else if (property.Name == "$font") Font = value;
            else if (!property.Name.StartsWith('$')) strings[property.Name] = value;
        }
    }

    internal void Remove(string key) => strings.Remove(key);
}

/// <summary>
/// The interface texts. Every string shown to the user is looked up here by key; the languages are the
/// Lang/*.json files built into this assembly plus any found by <see cref="LoadFolder"/>.
/// </summary>
public static class L
{
    /// <summary>The language that supplies whatever another one lacks. It is also the one in use until <see cref="Use"/> is called.</summary>
    public const string FallbackCode = "en";

    private const string ResourcePrefix = "Lang/";
    private static readonly Regex Placeholder = new(@"\{(\d+)[^{}]*\}", RegexOptions.Compiled);

    private static readonly List<Language> languages = new();
    private static readonly Language fallback;
    private static volatile Language current;

    static L()
    {
        var assembly = typeof(L).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal)) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            Get(resource[ResourcePrefix.Length..^".json".Length]).Merge(reader.ReadToEnd());
        }
        fallback = Get(FallbackCode);
        current = fallback;
        Check(null);
    }

    public static IReadOnlyList<Language> Languages => languages;
    public static Language Current => current;

    /// <summary>Changes whenever the language does; whatever caches translated text compares it.</summary>
    public static int Generation { get; private set; }

    /// <summary>
    /// Loads the *.json files of a folder: a new file name adds a language, a known one replaces strings of
    /// that language. A file that cannot be read is skipped.
    /// </summary>
    /// <returns>What was wrong with the files, for the log.</returns>
    public static List<string> LoadFolder(string folder)
    {
        var problems = new List<string>();
        if (!Directory.Exists(folder)) return problems;
        // Older portable builds can leave regional file names behind. Merge those first so a renamed
        // catalog takes precedence, and both names update one language rather than creating duplicates.
        foreach (string file in Directory.GetFiles(folder, "*.json")
            .OrderBy(f => CanonicalCode(Path.GetFileNameWithoutExtension(f))
                .Equals(Path.GetFileNameWithoutExtension(f), StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // A file that cannot be read must not leave an empty language behind.
                string code = CanonicalCode(Path.GetFileNameWithoutExtension(file));
                var language = Find(code) ?? new Language(code);
                language.Merge(File.ReadAllText(file));
                Add(language);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                problems.Add($"{file}: {ex.Message}");
            }
        }
        Check(problems);
        Generation++;
        return problems;
    }

    /// <summary>The code of the available language closest to a locale name such as "zh-Hant", or null.</summary>
    public static string? Match(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        string wanted = CanonicalCode(locale.Trim());
        var found = Find(wanted);
        if (found != null) return found.Code;
        if (Primary(wanted).Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            var parts = wanted.Split('-');
            // Honour an explicit script before the region: zh-Hans-HK is still simplified Chinese.
            bool simplified = parts.Contains("Hans", StringComparer.OrdinalIgnoreCase);
            bool traditional = !simplified && (parts.Contains("Hant", StringComparer.OrdinalIgnoreCase)
                || parts.Any(p => p.Equals("TW", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("HK", StringComparison.OrdinalIgnoreCase) || p.Equals("MO", StringComparison.OrdinalIgnoreCase)));
            found = Find(traditional ? "zh-Hant" : "zh-Hans");
            if (found != null) return found.Code;
        }
        found = Find(Primary(wanted))
            ?? languages.FirstOrDefault(l => Primary(l.Code).Equals(Primary(wanted), StringComparison.OrdinalIgnoreCase));
        return found?.Code;
    }

    /// <summary>Switches to a language by its code. An unknown code changes nothing.</summary>
    public static bool Use(string code)
    {
        var language = Find(code);
        if (language == null) return false;
        if (language != current)
        {
            current = language;
            Generation++;
        }
        return true;
    }

    /// <summary>The text of a key in the current language. An unknown key comes back as it is, which makes it easy to spot.</summary>
    public static string T(string key) => Text(current, key);

    /// <summary>The text of a key with its {0}, {1}… filled in.</summary>
    public static string T(string key, params object?[] args) => Format(Text(current, key), args);

    /// <summary>Like <see cref="T(string)"/> for one particular language.</summary>
    public static string Text(Language language, string key) =>
        language.Strings.TryGetValue(key, out string? text) || fallback.Strings.TryGetValue(key, out text) ? text : key;

    private static string Format(string template, object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            // A broken text is better shown unfilled than taking the editor down.
            return template;
        }
    }

    private static string Primary(string code) => code.Split('-')[0];

    private static string CanonicalCode(string code)
    {
        code = code.Replace('_', '-');
        if (code.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
        if (code.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
        return code;
    }

    private static Language? Find(string code)
    {
        string canonical = CanonicalCode(code);
        return languages.FirstOrDefault(l => l.Code.Equals(canonical, StringComparison.OrdinalIgnoreCase));
    }

    private static void Add(Language language)
    {
        if (languages.Contains(language)) return;
        languages.Add(language);
        languages.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));
    }

    private static Language Get(string code)
    {
        var language = Find(code) ?? new Language(code);
        Add(language);
        return language;
    }

    /// <summary>The numbers of the {0}, {1}… a text asks for.</summary>
    public static SortedSet<int> PlaceholdersOf(string text)
    {
        var set = new SortedSet<int>();
        foreach (Match match in Placeholder.Matches(text.Replace("{{", "").Replace("}}", "")))
            set.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        return set;
    }

    /// <summary>
    /// Drops translations that could not be filled in: ones asking for a value the fallback text does not
    /// get, or with unbalanced braces. The fallback text is then shown in their place.
    /// </summary>
    private static void Check(List<string>? problems)
    {
        foreach (var language in languages)
        {
            if (language == fallback) continue;
            foreach (var (key, text) in language.Strings.ToArray())
            {
                if (!fallback.Strings.TryGetValue(key, out string? reference)) continue;
                var expected = PlaceholdersOf(reference);
                if (expected.Count == 0) continue;
                bool usable = PlaceholdersOf(text).IsSubsetOf(expected);
                try
                {
                    if (usable) _ = string.Format(CultureInfo.InvariantCulture, text, new object?[expected.Max + 1]);
                }
                catch (FormatException)
                {
                    usable = false;
                }
                if (usable) continue;
                language.Remove(key);
                problems?.Add($"{language.Code}: \"{key}\" does not have the same {{0}}, {{1}}… as the {FallbackCode} text and was ignored");
            }
        }
    }
}

/// <summary>A value built from translated text, rebuilt after the language changes.</summary>
public sealed class Localized<T>
{
    private readonly Func<T> build;
    private T? value;
    private int generation = -1;

    public Localized(Func<T> build) => this.build = build;

    public T Value
    {
        get
        {
            if (generation != L.Generation)
            {
                value = build();
                generation = L.Generation;
            }
            return value!;
        }
    }
}
