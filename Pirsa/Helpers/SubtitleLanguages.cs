using System.Globalization;

namespace Pirsa.Helpers;

/// <summary>
/// Turns Stremio/OpenSubtitles language codes (ISO 639-2 or 639-1) into names
/// suitable for the subtitle picker.
/// </summary>
public static class SubtitleLanguages
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["eng"] = "English",
        ["es"] = "Spanish", ["spa"] = "Spanish", ["spl"] = "Spanish (Latin America)",
        ["fr"] = "French", ["fre"] = "French", ["fra"] = "French",
        ["de"] = "German", ["ger"] = "German", ["deu"] = "German",
        ["it"] = "Italian", ["ita"] = "Italian",
        ["pt"] = "Portuguese", ["por"] = "Portuguese", ["pob"] = "Portuguese (Brazil)",
        ["ru"] = "Russian", ["rus"] = "Russian",
        ["ja"] = "Japanese", ["jpn"] = "Japanese",
        ["ko"] = "Korean", ["kor"] = "Korean",
        ["zh"] = "Chinese", ["chi"] = "Chinese", ["zho"] = "Chinese",
        ["ar"] = "Arabic", ["ara"] = "Arabic",
        ["hi"] = "Hindi", ["hin"] = "Hindi",
        ["tr"] = "Turkish", ["tur"] = "Turkish",
        ["pl"] = "Polish", ["pol"] = "Polish",
        ["nl"] = "Dutch", ["dut"] = "Dutch", ["nld"] = "Dutch",
        ["sv"] = "Swedish", ["swe"] = "Swedish",
        ["no"] = "Norwegian", ["nor"] = "Norwegian",
        ["da"] = "Danish", ["dan"] = "Danish",
        ["fi"] = "Finnish", ["fin"] = "Finnish",
        ["cs"] = "Czech", ["cze"] = "Czech", ["ces"] = "Czech",
        ["sk"] = "Slovak", ["slo"] = "Slovak", ["slk"] = "Slovak",
        ["el"] = "Greek", ["gre"] = "Greek", ["ell"] = "Greek",
        ["he"] = "Hebrew", ["heb"] = "Hebrew",
        ["th"] = "Thai", ["tha"] = "Thai",
        ["vi"] = "Vietnamese", ["vie"] = "Vietnamese",
        ["id"] = "Indonesian", ["ind"] = "Indonesian",
        ["ms"] = "Malay", ["may"] = "Malay", ["msa"] = "Malay",
        ["uk"] = "Ukrainian", ["ukr"] = "Ukrainian",
        ["ro"] = "Romanian", ["ron"] = "Romanian", ["rum"] = "Romanian",
        ["hu"] = "Hungarian", ["hun"] = "Hungarian",
        ["bg"] = "Bulgarian", ["bul"] = "Bulgarian",
        ["hr"] = "Croatian", ["hrv"] = "Croatian",
        ["sr"] = "Serbian", ["srp"] = "Serbian",
        ["sl"] = "Slovenian", ["slv"] = "Slovenian",
        ["et"] = "Estonian", ["est"] = "Estonian",
        ["lv"] = "Latvian", ["lav"] = "Latvian",
        ["lt"] = "Lithuanian", ["lit"] = "Lithuanian",
        ["fa"] = "Persian", ["per"] = "Persian",
        ["sq"] = "Albanian", ["alb"] = "Albanian", ["sqi"] = "Albanian",
        ["mk"] = "Macedonian", ["mac"] = "Macedonian", ["mkd"] = "Macedonian",
        ["bn"] = "Bengali", ["ben"] = "Bengali",
        ["ta"] = "Tamil", ["tam"] = "Tamil",
        ["te"] = "Telugu", ["tel"] = "Telugu",
        ["ml"] = "Malayalam", ["mal"] = "Malayalam",
        ["kn"] = "Kannada", ["kan"] = "Kannada",
        ["mr"] = "Marathi", ["mar"] = "Marathi",
        ["gu"] = "Gujarati", ["guj"] = "Gujarati",
        ["pa"] = "Punjabi", ["pun"] = "Punjabi",
        ["ur"] = "Urdu", ["urd"] = "Urdu",
        ["tl"] = "Filipino", ["fil"] = "Filipino",
        ["sw"] = "Swahili", ["swa"] = "Swahili",
        ["af"] = "Afrikaans", ["afr"] = "Afrikaans",
        ["ka"] = "Georgian", ["kat"] = "Georgian",
        ["az"] = "Azerbaijani", ["aze"] = "Azerbaijani",
        ["kk"] = "Kazakh", ["kaz"] = "Kazakh",
        ["uz"] = "Uzbek", ["uzb"] = "Uzbek",
        ["be"] = "Belarusian", ["bel"] = "Belarusian",
        ["bs"] = "Bosnian", ["bos"] = "Bosnian",
        ["mne"] = "Montenegrin",
        ["abk"] = "Abkhazian",
        ["am"] = "Amharic", ["amh"] = "Amharic",
        ["hy"] = "Armenian", ["arm"] = "Armenian", ["hye"] = "Armenian",
        ["eu"] = "Basque", ["baq"] = "Basque", ["eus"] = "Basque",
        ["ca"] = "Catalan", ["cat"] = "Catalan",
        ["is"] = "Icelandic", ["ice"] = "Icelandic", ["isl"] = "Icelandic",
        ["ga"] = "Irish", ["gle"] = "Irish",
        ["lo"] = "Lao", ["lao"] = "Lao",
        ["mn"] = "Mongolian", ["mon"] = "Mongolian",
        ["ne"] = "Nepali", ["nep"] = "Nepali",
        ["si"] = "Sinhala", ["sin"] = "Sinhala",
        ["so"] = "Somali", ["som"] = "Somali",
        ["bo"] = "Tibetan", ["tib"] = "Tibetan", ["bod"] = "Tibetan",
        ["cy"] = "Welsh", ["wel"] = "Welsh", ["cym"] = "Welsh",
        ["yi"] = "Yiddish", ["yid"] = "Yiddish",
        ["zu"] = "Zulu", ["zul"] = "Zulu",
    };

    public static string DisplayName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "Unknown";
        }

        var normalized = code.Trim().ToLowerInvariant();
        if (Names.TryGetValue(normalized, out var name))
        {
            return name;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(normalized);
            if (!string.IsNullOrWhiteSpace(culture.DisplayName) &&
                !culture.DisplayName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return culture.DisplayName;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return normalized.ToUpperInvariant();
    }
}
