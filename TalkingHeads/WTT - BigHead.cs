// WTT-BigHead.cs (full, with locales applied to Global)
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
public record HeadConfig
{
    public string Parent { get; init; } = string.Empty;
    public string Prototype { get; init; } = string.Empty;
    public string[] Side { get; init; } = Array.Empty<string>();
    public string Path { get; init; } = string.Empty;
    public bool AddHeadToPlayer { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public bool AvailableAsDefault { get; init; }
    public bool IntegratedArmorVest { get; init; }
    public XYZ WatchPosition { get; init; } = new();
    public XYZ WatchRotation { get; init; } = new();
    public Prefab WatchPrefab { get; init; } = new();
    public Dictionary<string, Dictionary<string, string>> Locales { get; init; } = new();
}
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.wtt.TalkingHeads";
    public override string Name { get; init; } = "WTT-TalkingHeads";
    public override string Author { get; init; } = "RockaHorse, GroovypenguinX";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = true;
    public override string? License { get; init; } = "MIT";
}
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class BigHeadMod(
ISptLogger<BigHeadMod> logger,
DatabaseService databaseService,
JsonUtil jsonUtil) : IOnLoad
{
    public Task OnLoad()
    {
        ProcessCustomHeads();
        logger.Success("[WTT-TalkingHeads] BigHead: Loading complete.");
        return Task.CompletedTask;
    }
    private void ProcessCustomHeads()
    {
        string modPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        string headsJsonPath = Path.Combine(modPath, "db", "heads");  // Separate from voices
        if (!Directory.Exists(headsJsonPath))
        {
            logger.Warning($"[WTT-TalkingHeads] BigHead: Heads directory not found: {headsJsonPath}");
            return;
        }
        var configFiles = Directory.GetFiles(headsJsonPath, "*.json");
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
                    var headId = prop.Name;
                    var headElement = prop.Value;
                    if (headElement.ValueKind != JsonValueKind.Object)
                    {
                        logger.Warning($"[WTT-TalkingHeads] BigHead: Skipping non-object entry for {headId}");
                        continue;
                    }
                    var headConfig = ParseHeadConfig(headElement, headId, logger);
                    ProcessHeadConfig(headId, headConfig, templates, logger);
                    // Build custom locales
                    var headNameKey = $"{headId} Name";
                    var headShortKey = $"{headId} ShortName";
                    var headDescKey = $"{headId} Description";
                    foreach (var (lang, langDict) in headConfig.Locales)
                    {
                        if (!customLocales.TryGetValue(lang, out var customLangDict))
                        {
                            customLangDict = new Dictionary<string, string>();
                            customLocales[lang] = customLangDict;
                        }
                        if (langDict.TryGetValue("Name", out var name))
                        {
                            customLangDict[headNameKey] = name;
                        }
                        if (langDict.TryGetValue("ShortName", out var shortName))
                        {
                            customLangDict[headShortKey] = shortName;
                        }
                        if (langDict.TryGetValue("Description", out var desc))
                        {
                            customLangDict[headDescKey] = desc;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[WTT-TalkingHeads] BigHead: Error processing file {filePath}: {ex.Message}");
            }
        }
        // Apply custom locales to Global using transformer
        var fallback = new Dictionary<string, string>();
        foreach (var (localeCode, lazyLocale) in locales.Global)
        {
            lazyLocale.AddTransformer(localeData =>
            {
                if (localeData is null)
                {
                    return localeData;
                }

                var customLocale = customLocales.GetValueOrDefault(localeCode, fallback);

                foreach (var (key, value) in customLocale)
                {
                    if (!localeData.ContainsKey(key))
                    {
                        localeData[key] = value;
                    }
                }

                return localeData;
            });
            logger.Info($"[WTT-TalkingHeads] BigHead: Applied transformer for locale {localeCode}");
        }
    }
    private HeadConfig ParseHeadConfig(JsonElement headElement, string headId, ISptLogger<BigHeadMod> logger)
    {
        var props = headElement.GetProperty("_props");
        var name = props.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
        var description = props.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : "";
        var shortName = props.TryGetProperty("ShortName", out var s) ? s.GetString() ?? "" : "";
        var availableDefault = props.TryGetProperty("AvailableAsDefault", out var ad) ? ad.GetBoolean() : false;
        var integratedVest = props.TryGetProperty("IntegratedArmorVest", out var iv) ? iv.GetBoolean() : false;
        var addToPlayer = headElement.TryGetProperty("addHeadToPlayer", out var atp) ? atp.GetBoolean() : false;
        var prefab = props.TryGetProperty("Prefab", out var p) ? p.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "" : "";
        var side = new List<string>();
        if (props.TryGetProperty("Side", out var sideProp) && sideProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideProp.EnumerateArray())
            {
                side.Add(item.GetString() ?? "");
            }
        }
        var watchPos = new XYZ();
        if (props.TryGetProperty("WatchPosition", out var wpProp))
        {
            watchPos.X = wpProp.TryGetProperty("x", out var px) ? px.GetSingle() : 0;
            watchPos.Y = wpProp.TryGetProperty("y", out var py) ? py.GetSingle() : 0;
            watchPos.Z = wpProp.TryGetProperty("z", out var pz) ? pz.GetSingle() : 0;
        }
        var watchRot = new XYZ();
        if (props.TryGetProperty("WatchRotation", out var wrProp))
        {
            watchRot.X = wrProp.TryGetProperty("x", out var rx) ? rx.GetSingle() : 0;
            watchRot.Y = wrProp.TryGetProperty("y", out var ry) ? ry.GetSingle() : 0;
            watchRot.Z = wrProp.TryGetProperty("z", out var rz) ? rz.GetSingle() : 0;
        }
        var watchPrefab = new Prefab { Path = "", Rcid = "" };
        if (props.TryGetProperty("WatchPrefab", out var wfProp))
        {
            watchPrefab.Path = wfProp.TryGetProperty("path", out var wfp) ? wfp.GetString() ?? "" : "";
            watchPrefab.Rcid = wfProp.TryGetProperty("rcid", out var wfr) ? wfr.GetString() ?? "" : "";
        }
        var parent = headElement.TryGetProperty("_parent", out var parentProp) ? parentProp.GetString() ?? "" : "";
        var proto = headElement.TryGetProperty("_proto", out var protoProp) ? protoProp.GetString() ?? "" : "";
        var locales = new Dictionary<string, Dictionary<string, string>>();
        if (headElement.TryGetProperty("locales", out var localesProp) && localesProp.ValueKind == JsonValueKind.Object)
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
                    logger.Warning($"[WTT-TalkingHeads] BigHead: Invalid locales value kind {langValue.ValueKind} for lang {lang} in head {headId}");
                }
                locales[lang] = localeDict;
            }
        }
        else if (headElement.TryGetProperty("locales", out _))
        {
            logger.Warning($"[WTT-TalkingHeads] BigHead: locales is not object for head {headId}");
        }
        return new HeadConfig
        {
            Parent = parent,
            Prototype = proto,
            Side = side.ToArray(),
            Path = prefab,
            AddHeadToPlayer = addToPlayer,
            Name = name,
            Description = description,
            ShortName = shortName,
            AvailableAsDefault = availableDefault,
            IntegratedArmorVest = integratedVest,
            WatchPosition = watchPos,
            WatchRotation = watchRot,
            WatchPrefab = watchPrefab,
            Locales = locales
        };
    }
    private void ProcessHeadConfig(string headId, HeadConfig headConfig, Templates templates, ISptLogger<BigHeadMod> logger)
    {
        var iCustomizationHead = GenerateHeadSpecificConfig(headId, headConfig);
        templates.Customization[new MongoId(headId)] = iCustomizationHead;
        logger.Info($"[WTT-TalkingHeads] BigHead: Added head to customization: {headId}");

        if (headConfig.AddHeadToPlayer)
        {
            var bots = databaseService.GetBots().Types;
            foreach (var s in headConfig.Side)
            {
                var botKey = s.ToLower() == "usec" ? "pmcusec" : "pmcbear";
                if (bots.TryGetValue(botKey, out var bot))
                {
                    bot.BotAppearance ??= new Appearance();
                    bot.BotAppearance.Head ??= new Dictionary<MongoId, double>();
                    bot.BotAppearance.Head[headId] = 10;
                }
            }
            logger.Info($"[WTT-TalkingHeads] BigHead: Added head {headId} to player selection");
        }

        AddHeadToCustomizationStorage(templates, headId);
    }
    private static CustomizationItem GenerateHeadSpecificConfig(string headId, HeadConfig headConfig)
    {
        return new CustomizationItem
        {
            Id = headId,
            Name = headConfig.Name,
            Parent = new MongoId(headConfig.Parent),
            Type = "Item",
            Properties = new CustomizationProperties
            {
                AvailableAsDefault = headConfig.AvailableAsDefault,
                Name = $"{headId} Name",
                ShortName = $"{headId} ShortName",
                Description = $"{headId} Description",
                Side = headConfig.Side.ToList(),
                BodyPart = "Head",
                IntegratedArmorVest = headConfig.IntegratedArmorVest,
                ProfileVersions = new List<string>(),
                Prefab = new Prefab { Path = headConfig.Path, Rcid = "" },
                WatchPrefab = new Prefab { Path = headConfig.WatchPrefab.Path, Rcid = headConfig.WatchPrefab.Rcid },
                WatchPosition = headConfig.WatchPosition,
                WatchRotation = headConfig.WatchRotation
            },
            Prototype = headConfig.Prototype
        };
    }
    private static void AddHeadToCustomizationStorage(Templates templates, string headId)
    {
        var customizationStorage = templates.CustomisationStorage;
        var headStorage = new CustomisationStorage
        {
            Id = headId,
            Source = CustomisationSource.DEFAULT,
            Type = CustomisationType.HEAD
        };
        customizationStorage.Add(headStorage);
    }
    public class PrefabConverter : JsonConverter<Prefab>
    {
        public override Prefab Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var path = reader.GetString() ?? "";
            return new Prefab { Path = path, Rcid = "" };  // Deserialize from string
        }
        public override void Write(Utf8JsonWriter writer, Prefab value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.Path);  // Serialize as path string
        }
    }
}