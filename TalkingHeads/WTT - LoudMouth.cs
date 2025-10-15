// WTT-LoudMouth.cs (full, bots unchanged, paths separate)
using System.IO;
using System.Reflection;
using System.Text.Json;
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

                    var voiceConfig = ParseVoiceConfig(voiceElement, logger);

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

        // Apply custom locales to Global
        foreach (var (lang, customLangDict) in customLocales)
        {
            if (locales.Global.TryGetValue(lang, out var lazyLangDict))
            {
                var langDict = lazyLangDict.Value;
                foreach (var (key, value) in customLangDict)
                {
                    langDict[key] = value;
                }
                logger.Info($"[WTT-TalkingHeads] LoudMouth: Applied {customLangDict.Count} custom locales for {lang}");
            }
        }
    }

    private VoiceConfig ParseVoiceConfig(JsonElement voiceElement, ISptLogger<LoudMouthMod> logger)
    {
        var addToPlayer = voiceElement.TryGetProperty("addVoiceToPlayer", out var addProp) ? addProp.GetBoolean() : false;
        var name = voiceElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        var sideSpecific = voiceElement.TryGetProperty("sideSpecificVoice", out var sideProp) ? sideProp.GetString() : null;

        var addToBotTypes = new Dictionary<string, int>();
        if (voiceElement.TryGetProperty("addToBotTypes", out var botTypesProp))
        {
            foreach (var botProp in botTypesProp.EnumerateObject())
            {
                var botType = botProp.Name.ToLowerInvariant();
                var weight = botProp.Value.GetInt32();
                addToBotTypes[botType] = weight;
            }
        }

        var locales = new Dictionary<string, Dictionary<string, string>>();
        if (voiceElement.TryGetProperty("locales", out var localesProp))
        {
            foreach (var langProp in localesProp.EnumerateObject())
            {
                var lang = langProp.Name;
                var localeDict = new Dictionary<string, string>();
                if (langProp.Value.TryGetProperty("Name", out var nameEl)) localeDict["Name"] = nameEl.GetString() ?? "";
                if (langProp.Value.TryGetProperty("ShortName", out var shortEl)) localeDict["ShortName"] = shortEl.GetString() ?? "";
                if (langProp.Value.TryGetProperty("Description", out var descEl)) localeDict["Description"] = descEl.GetString() ?? "";
                locales[lang] = localeDict;
            }
        }

        logger.Info($"[WTT-TalkingHeads] LoudMouth: Parsed voice: Name={name}, AddToPlayer={addToPlayer}, BotTypes={addToBotTypes.Count}");

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
        CreateAndAddVoice(voiceId, voiceConfig, templates);

        if (voiceConfig.AddToBotTypes != null)
        {
            ProcessBotVoices(voiceConfig.AddToBotTypes, voiceId, logger);
        }

        AddVoiceToCustomizationStorage(templates, voiceId);
    }

    private void CreateAndAddVoice(string voiceId, VoiceConfig voiceConfig, Templates templates)
    {
        var side = voiceConfig.SideSpecificVoice != null ? new[] { voiceConfig.SideSpecificVoice } : new[] { "Usec", "Bear" };

        var newVoice = new CustomizationItem
        {
            Id = voiceId,
            Name = null,
            Parent = new MongoId("5fc100cf95572123ae738483"),
            Type = "Item",
            Properties = new CustomizationProperties
            {
                Name = voiceConfig.Name,
                ShortName = voiceConfig.Name,
                Description = voiceConfig.Name,
                Side = side.ToList(),
                Prefab = new Prefab { Path = voiceConfig.Name, Rcid = "" }
            },
            Prototype = string.Empty
        };

        templates.Customization[new MongoId(voiceId)] = newVoice;

        if (voiceConfig.AddVoiceToPlayer)
        {
            templates.Character.Add(voiceId);
        }

        logger.Info($"[WTT-TalkingHeads] LoudMouth: Created and added voice: {voiceId}");
    }

    private void ProcessBotVoices(Dictionary<string, int> addToBotTypes, string voiceId, ISptLogger<LoudMouthMod> logger)
    {
        foreach (var (botType, weight) in addToBotTypes)
        {
            if (databaseService.GetBots().Types.TryGetValue(botType, out var botTypeData))
            {
                botTypeData.BotAppearance ??= new Appearance();
                botTypeData.BotAppearance.Voice ??= new Dictionary<MongoId, double>();
                botTypeData.BotAppearance.Voice[new MongoId(voiceId)] = (double)weight;
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