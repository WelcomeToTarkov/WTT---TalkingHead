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

namespace WTTBigHead;

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
    public override string ModGuid { get; init; } = "com.wtt.BigHead";
    public override string Name { get; init; } = "WTT-BigHead";
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

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class BigHeadMod(
    ISptLogger<BigHeadMod> logger,
    DatabaseService databaseService,
    JsonUtil jsonUtil) : IOnLoad
{
    public Task OnLoad()
    {
        ProcessCustomHeads();

        logger.Success("[WTT-BigHead] Database: Loading complete.");
        return Task.CompletedTask;
    }

    private void ProcessCustomHeads()
    {
        string modPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        string headsJsonPath = Path.Combine(modPath, "db", "heads");

        if (!Directory.Exists(headsJsonPath))
        {
            logger.Warning($"[WTT-BigHead] Heads directory not found: {headsJsonPath}");
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

                    var headConfig = ParseHeadConfig(headElement, logger);

                    ProcessHeadConfig(headId, headConfig, templates, logger);

                    // Build custom locales
                    var headNameKey = $"{headId} Name";
                    var headShortKey = $"{headId} ShortName";
                    var headDescKey = $"{headId} Description";

                    foreach (var lang in headConfig.Locales.Keys)
                    {
                        if (!customLocales.TryGetValue(lang, out var langDict))
                        {
                            langDict = new Dictionary<string, string>();
                            customLocales[lang] = langDict;
                        }

                        if (headConfig.Locales[lang].TryGetValue("Name", out var name))
                        {
                            langDict[headNameKey] = name;
                        }
                        else
                        {
                            langDict[headNameKey] = headConfig.Name; // Fallback
                        }

                        langDict[headShortKey] = headConfig.ShortName;
                        langDict[headDescKey] = headConfig.Description;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[WTT-BigHead] Failed to process {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        // Apply transformers for lazy loading
        var fallback = customLocales.GetValueOrDefault("en", new Dictionary<string, string>());
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
                    localeData[key] = value;
                }

                return localeData;
            });
        }

        logger.Info($"[WTT-BigHead] Applied locale transformers for {customLocales.Count} languages");
    }

    private static HeadConfig ParseHeadConfig(JsonElement headElement, ISptLogger<BigHeadMod> logger)
    {
        var props = headElement.GetProperty("_props");

        var side = props.TryGetProperty("Side", out var sideProp) ? sideProp.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();
        var prefab = props.TryGetProperty("Prefab", out var prefabProp) ? prefabProp.GetProperty("path").GetString() ?? string.Empty : string.Empty;
        var addToPlayer = headElement.TryGetProperty("addHeadToPlayer", out var addProp) ? addProp.GetBoolean() : false;
        var name = props.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        var description = props.TryGetProperty("Description", out var descProp) ? descProp.GetString() ?? string.Empty : string.Empty;
        var shortName = props.TryGetProperty("ShortName", out var shortProp) ? shortProp.GetString() ?? string.Empty : string.Empty;
        var availableDefault = props.TryGetProperty("AvailableAsDefault", out var availProp) ? availProp.GetBoolean() : false;
        var integratedVest = props.TryGetProperty("IntegratedArmorVest", out var vestProp) ? vestProp.GetBoolean() : false;

        var watchPos = new XYZ();
        if (props.TryGetProperty("WatchPosition", out var wpProp))
        {
            watchPos.X = wpProp.TryGetProperty("x", out var x) ? x.GetSingle() : 0;
            watchPos.Y = wpProp.TryGetProperty("y", out var y) ? y.GetSingle() : 0;
            watchPos.Z = wpProp.TryGetProperty("z", out var z) ? z.GetSingle() : 0;
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
        if (headElement.TryGetProperty("locales", out var localesProp))
        {
            // Assume locales is {lang: name}
            foreach (var langProp in localesProp.EnumerateObject())
            {
                var lang = langProp.Name;
                var localeName = langProp.Value.GetString() ?? "";
                locales[lang] = new Dictionary<string, string> { { "Name", localeName } };
            }
        }

        logger.Info($"[WTT-BigHead] Parsed head: Name={name}, Path={prefab}, Side={string.Join(",", side)}, AddToPlayer={addToPlayer}");

        return new HeadConfig
        {
            Parent = parent,
            Prototype = proto,
            Side = side,
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
        logger.Info($"[WTT-BigHead] Added head to customization: {headId}");

        if (headConfig.AddHeadToPlayer)
        {
            templates.Character.Add(headId);
            logger.Info($"[WTT-BigHead] Added head to character list: {headId}");
        }

        AddHeadToCustomizationStorage(templates, headId);
    }

    private static CustomizationItem GenerateHeadSpecificConfig(string headId, HeadConfig headConfig)
    {
        return new CustomizationItem
        {
            Id = headId,
            Name = null,
            Parent = new MongoId(headConfig.Parent),
            Type = "Item",
            Properties = new CustomizationProperties
            {
                AvailableAsDefault = headConfig.AvailableAsDefault,
                Name = null,
                ShortName = null,
                Description = null,
                Side = headConfig.Side.ToList(),
                BodyPart = "Head",
                IntegratedArmorVest = headConfig.IntegratedArmorVest,
                ProfileVersions = new List<string>(),
                Prefab = new Prefab { Path = headConfig.Path, Rcid = "" },
                WatchPrefab = headConfig.WatchPrefab,
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
}