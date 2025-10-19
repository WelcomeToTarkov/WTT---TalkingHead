// WTT-LoudMouth.cs (full, bots unchanged, paths separate)
using System.Formats.Asn1;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Server;
using SPTarkov.Server.Core.Models.Spt.Templates;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace WTTTalkingHeads;

using Json = SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

public class CustomCustomizationProperties
{
    public string Name { get; set; }
    public string ShortName { get; set; }
    public string Description { get; set; }
    public List<string> Side { get; set; }
    public object Prefab { get; set; }
    public List<string> ProfileVersions { get; set; }
    public bool AvailableAsDefault { get; set; }
    public bool IsNotRandom { get; set; }
}
public record VoiceConfig
{
    public bool AddVoiceToPlayer { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? SideSpecificVoice { get; init; }
    public Dictionary<string, int>? AddToBotTypes { get; init; }
    public Dictionary<string, Dictionary<string, string>>? Locales { get; init; }
}
// No ModMetadata here
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class LoudMouthMod(
ISptLogger<LoudMouthMod> logger,
DatabaseService databaseService,
JsonUtil jsonUtil) : IOnLoad
{
    public Task OnLoad()
    {
        ProcessCustomVoices();
        logger.Success("[WTT-TalkingHeads] LoudMouth: Loading complete.");
        return Task.CompletedTask;
    }
    private void ProcessCustomVoices()
    {
        string modPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        string voicesJsonPath = Path.Combine(modPath, "db", "voices");  // Separate from heads
        if (!Directory.Exists(voicesJsonPath))
        {
            logger.Warning($"[WTT-TalkingHeads] LoudMouth: Voices directory not found: {voicesJsonPath}");
            return;
        }
        var configFiles = Directory.GetFiles(voicesJsonPath, "*.json");
        var templates = databaseService.GetTemplates();
        var locales = databaseService.GetLocales();
        var customLocales = new Dictionary<string, Dictionary<string, string>>(); // lang -> {key: value}
        foreach (var filePath in configFiles)
        {
            try
            {
                var fileContents = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(fileContents);
                var root = doc.RootElement;
                foreach (var prop in root.EnumerateObject())
                {
                    var voiceId = prop.Name;
                    var voiceElement = prop.Value;
                    if (voiceElement.ValueKind != JsonValueKind.Object)
                    {
                        logger.Warning($"[WTT-TalkingHeads] LoudMouth: Skipping non-object entry for {voiceId}");
                        continue;
                    }
                    var voiceConfig = ParseVoiceConfig(voiceElement, voiceId, logger);
                    ProcessVoiceConfig(voiceId, voiceConfig, templates, logger);
                    // Build custom locales
                    var voiceNameKey = $"{voiceId} Name";
                    var voiceShortKey = $"{voiceId} ShortName";
                    var voiceDescKey = $"{voiceId} Description";
                    foreach (var (lang, langDict) in voiceConfig.Locales ?? new Dictionary<string, Dictionary<string, string>>())
                    {
                        if (!customLocales.TryGetValue(lang, out var customLangDict))
                        {
                            customLangDict = new Dictionary<string, string>();
                            customLocales[lang] = customLangDict;
                        }
                        if (langDict.TryGetValue("Name", out var name))
                        {
                            customLangDict[voiceNameKey] = name;
                        }
                        if (langDict.TryGetValue("ShortName", out var shortName))
                        {
                            customLangDict[voiceShortKey] = shortName;
                        }
                        if (langDict.TryGetValue("Description", out var desc))
                        {
                            customLangDict[voiceDescKey] = desc;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[WTT-TalkingHeads] LoudMouth: Error processing file {filePath}: {ex.Message}");
            }
        }
        var fallback = new Dictionary<string, string>();
        foreach (var (localeCode, lazyLocale) in locales.Global)
        {
            lazyLocale.AddTransformer(localeData =>
            {
                if (localeData is null) { return localeData; }
                var customLocale = customLocales.GetValueOrDefault(localeCode, fallback);
                foreach (var (key, value) in customLocale) { localeData[key] = value; }
                return localeData;
            });
        }
    }
    private VoiceConfig ParseVoiceConfig(JsonElement voiceElement, string voiceId, ISptLogger<LoudMouthMod> logger)
    {
        var addToPlayer = voiceElement.TryGetProperty("addVoiceToPlayer", out var addProp) ? addProp.GetBoolean() : false;
        var name = voiceElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        var sideSpecific = voiceElement.TryGetProperty("sideSpecificVoice", out var sideProp) ? sideProp.GetString() : null;
        var addToBotTypes = new Dictionary<string, int>();
        if (voiceElement.TryGetProperty("addToBotTypes", out var botTypesProp) && botTypesProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var botProp in botTypesProp.EnumerateObject())
            {
                var botType = botProp.Name.ToLowerInvariant();
                var weight = botProp.Value.GetInt32();
                addToBotTypes[botType] = weight;
            }
        }
        else if (voiceElement.TryGetProperty("addToBotTypes", out _))
        {
            logger.Warning($"[WTT-TalkingHeads] LoudMouth: addToBotTypes is not object for voice {voiceId}");
        }
        // In ParseVoiceConfig (LoudMouth.cs), replace locales block:
        var locales = new Dictionary<string, Dictionary<string, string>>();
        if (voiceElement.TryGetProperty("locales", out var localesProp) && localesProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var langProp in localesProp.EnumerateObject())
            {
                var lang = langProp.Name;
                var localeDict = new Dictionary<string, string>();
                var langValue = langProp.Value;
                if (langValue.ValueKind == JsonValueKind.String)
                {
                    // Treat string as Name
                    localeDict["Name"] = langValue.GetString() ?? "";
                }
                else if (langValue.ValueKind == JsonValueKind.Object)
                {
                    if (langValue.TryGetProperty("Name", out var nameEl)) localeDict["Name"] = nameEl.GetString() ?? "";
                    if (langValue.TryGetProperty("ShortName", out var shortEl)) localeDict["ShortName"] = shortEl.GetString() ?? "";
                    if (langValue.TryGetProperty("Description", out var descEl)) localeDict["Description"] = descEl.GetString() ?? "";
                }
                else
                {
                    logger.Warning($"[WTT-TalkingHeads] LoudMouth: Invalid locales value kind {langValue.ValueKind} for lang {lang} in voice {voiceId}");
                }
                locales[lang] = localeDict;
            }
        }
        else if (voiceElement.TryGetProperty("locales", out _))
        {
            logger.Warning($"[WTT-TalkingHeads] LoudMouth: locales is not object for voice {voiceId}");
        }
        return new VoiceConfig
        {
            AddVoiceToPlayer = addToPlayer,
            Name = name,
            SideSpecificVoice = sideSpecific,
            AddToBotTypes = addToBotTypes,
            Locales = locales
        };
    }
    private void ProcessVoiceConfig(string voiceId, VoiceConfig voiceConfig, Templates templates, ISptLogger<LoudMouthMod> logger)
    {
        var newVoice = CreateVoice(voiceId, voiceConfig);
        templates.Customization[new MongoId(voiceId)] = newVoice;
        logger.Info($"[WTT-TalkingHeads] LoudMouth: Added voice to customization: {voiceId}");

        if (voiceConfig.AddVoiceToPlayer)
        {
            var bots = databaseService.GetBots().Types;
            var sides = voiceConfig.SideSpecificVoice != null ? new[] { voiceConfig.SideSpecificVoice } : new[] { "Usec", "Bear" };
            foreach (var side in sides)
            {
                var botKey = side.ToLower() == "usec" ? "pmcusec" : "pmcbear";
                if (bots.TryGetValue(botKey, out var bot))
                {
                    bot.BotAppearance ??= new Appearance();  // Ensure exists
                    bot.BotAppearance.Voice ??= new Dictionary<MongoId, double>();  // MongoId keys, double weights (matches Appearance signature)
                    bot.BotAppearance.Voice[voiceId] = 1;  // Vanilla weight
                }
            }
            logger.Info($"[WTT-TalkingHeads] LoudMouth: Added voice {voiceId} to player selection");
        }

        if (voiceConfig.AddToBotTypes != null)
        {
            ProcessBotVoices(voiceConfig.AddToBotTypes, voiceId, logger);
        }
        AddVoiceToCustomizationStorage(templates, voiceId);
    }
    private CustomizationItem CreateVoice(string voiceId, VoiceConfig voiceConfig)
    {
        var side = voiceConfig.SideSpecificVoice != null ? new[] { voiceConfig.SideSpecificVoice } : new[] { "Usec", "Bear" };
        var newVoice = new CustomizationItem
        {
            Id = voiceId,
            Name = voiceConfig.Name,
            Parent = new MongoId("5fc100cf95572123ae738483"),  // Voice parent ID (adjust if changed in 4.0)
            Type = "Item",
            Properties = new CustomizationProperties
            {
                Name = $"{voiceId} Name",
                ShortName = $"{voiceId} ShortName",
                Description = $"{voiceId} Description",
                Side = side.ToList(),
                Prefab = voiceConfig.Name,
                ProfileVersions = new List<string>(),
                AvailableAsDefault = voiceConfig.AddVoiceToPlayer,
                IsNotRandom = true
            },
            Prototype = string.Empty
        };
        return newVoice;
    }
    private void ProcessBotVoices(Dictionary<string, int> addToBotTypes, string voiceId, ISptLogger<LoudMouthMod> logger)
    {
        foreach (var (botType, weight) in addToBotTypes)
        {
            if (databaseService.GetBots().Types.TryGetValue(botType, out var botTypeData))
            {
                botTypeData.BotAppearance ??= new Appearance();
                botTypeData.BotAppearance.Voice = new Dictionary<MongoId, double>();
                botTypeData.BotAppearance.Voice[voiceId] = weight;
                logger.Info($"[WTT-TalkingHeads] LoudMouth: Added voice {voiceId} to bot {botType} with weight {weight}");
            }
            else
            {
                logger.Warning($"[WTT-TalkingHeads] LoudMouth: Bot type {botType} not found");
            }
        }
    }
    private static void AddVoiceToCustomizationStorage(Templates templates, string voiceId)
    {
        var customizationStorage = templates.CustomisationStorage;
        var voiceStorage = new CustomisationStorage
        {
            Id = voiceId,
            Source = CustomisationSource.DEFAULT,
            Type = CustomisationType.VOICE  // Use VOICE if enum has it; else FACE or adjust
        };
        customizationStorage.Add(voiceStorage);
    }

}