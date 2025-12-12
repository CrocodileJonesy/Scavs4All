using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using System.IO;
using System.Text.Json;
using SPTarkov.Server.Core.Utils;

namespace Scavs4All;

// Alias for easier access to quests dictionary
using Quests = Dictionary<MongoId, SPTarkov.Server.Core.Models.Eft.Common.Tables.Quest>;

// TODO: Add quest blacklist to exclude certain quests from being modified
// TODO: Add code to change 'Scav' or 'PMC' in quest text to 'Any Targets'
// TODO: Add support for scaling up scav quests
// TODO: Add support for including quests that aren't scav or pmc
// TODO: Add support for daily quests?
// TODO: Possibly add support for for raiders, rogues, cultists & bosses count as PMCs

public class S4AConfig
{
    //[JsonPropertyName("ReplacePMCWithAll")]
    public bool ReplacePmcWithAll { get; set; }
    public bool ScalePmcQuests { get; set; }
    public int ScalePmcMultiplier { get; set; }
    public bool EnableDebug { get; set; }
    public bool EnableVerboseDebug { get; set; }
}

// Load way after to include any other custom quests
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 99999)]
public class Scavs4All(DatabaseServer databaseServer, ISptLogger<Scavs4All> logger, LocaleService localeService, ModHelper modHelper, JsonUtil jsonUtil, DatabaseService databaseService) : IOnLoad
{
    // Loaded config file
    private S4AConfig s4aConfig;

    // Default config, should file be missing
    private readonly S4AConfig defaultConfig = new()
    {
        ReplacePmcWithAll = false,
        ScalePmcQuests = false,
        ScalePmcMultiplier = 2,
        EnableDebug = false,
        EnableVerboseDebug = false
    };

    // Logger
    private ISptLogger<Scavs4All> m_logger;
    private string[] loggerBuffer;

    // Counter Vars
    private int noOfScavQuestsModified;
    private int noOfPmcQuestsModified;
    private int noOfTotalQuests;
    private int noOfTotalQuestsModified;
    private bool didHarderPmc = false;

    // Database vars
    //private Dictionary<MongoId, TemplateItem>? _itemsDb;
    private Dictionary<string, string> localesDb;
    private Quests quests;

    public Task OnLoad()
    {
        //_itemsDb = databaseServer.GetTables().Templates.Items;
        localesDb = localeService.GetLocaleDb();
        quests = databaseServer.GetTables().Templates.Quests;
        this.m_logger = logger;

        // Config vars
        string pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()); ;
        string fullPath = System.IO.Path.Combine(pathToMod, "config.json");

        // Load config file
        try
        {
            // Load config file (Var names must match names in json file, otherwise [JsonPropertyName("")] needs to be used)
            s4aConfig = modHelper.GetJsonDataFromFile<S4AConfig>(pathToMod, "config.json");
        }
        catch (Exception e) // Config file not found
        {
            if (!File.Exists(fullPath))
            {
                m_logger.Warning("Scavs4All: Config file not found, generating default config file");

                // Generate default config file
                string config = jsonUtil.Serialize(defaultConfig, true);
                File.WriteAllText(fullPath, config);

                // Since no config file, assign default config as main config
                s4aConfig = defaultConfig;
            }
            else
            {
                m_logger.Warning($"Scavs4All: Error loading config file: {e.Message}");
            }
        }

        // Check if pmc quests are being modified
        if (s4aConfig.EnableDebug && s4aConfig.EnableVerboseDebug)
        {
            m_logger.Warning($"Harder PMC quest count is {(s4aConfig.ScalePmcQuests ? $"enabled and set to {s4aConfig.ScalePmcMultiplier}." : "disabled, not modifying quest counts")}");
        }

        // Replace quests conditions and text
        ChangeTargets(quests);

        // Print summary of changed quests
        PrintSummary();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Iterates through kill quests and changes them to include all scavs/pmcs
    /// </summary>
    /// <param name="quests">Quest Database</param>
    private void ChangeTargets(Quests quests)
    {
        // Debugging
        if (s4aConfig.EnableVerboseDebug)
        {
            m_logger.Info($"Scavs4All: Iterating through {quests.Count} quests");
        }
        else
        {
            m_logger.Info("Scavs4All: Searching quest DB");
        }

        // Go through quests
        foreach (Quest currentQuest in quests.Values)
        {
            // Count no of quests iterated
            noOfTotalQuests++;

            // Iterate through all the conditions of the current quest
            for (int i = 0; i < currentQuest.Conditions.AvailableForFinish.Count; ++i)
            {
                // Get quest condition
                var currentCondition = currentQuest.Conditions.AvailableForFinish[i];

                // Check if condition is number tracker (Elimination etc.)
                if (currentCondition.ConditionType == "CounterCreator")
                {
                    // If it's a counter creator iterate through the subconditions
                    //foreach (QuestConditionCounterCondition currentSubCondition in currentCondition.Counter.Conditions)
                    for (int j = 0; j < currentCondition.Counter.Conditions.Count; ++j)
                    {
                        // Get conditition parameterts 
                        var currentSubCondition = currentCondition.Counter.Conditions[j];

                        // Edit quest
                        ProcessQuest(currentQuest, currentCondition, ref currentSubCondition);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Process a quest target to change it to any
    /// </summary>
    /// <param name="currentQuest">The quest</param>
    /// <param name="currentCondition">The quest condidtion</param>
    /// <param name="currentSubCondition">The sub conditions of the condition</param>
    private void ProcessQuest(Quest currentQuest, QuestCondition currentCondition, ref QuestConditionCounterCondition currentSubCondition)
    {
        // Check if the current subcondidtion is a kill condition
        if (currentSubCondition.ConditionType == "Kills")
        {
            // Bools for ease of checking & reuse
            bool isScavKillQuest = currentSubCondition.Target.Item == "Savage"; // Scav
            bool isNotBossKillQuest = currentSubCondition.SavageRole == null || currentSubCondition.SavageRole.Count == 0; // Boss kill
            bool isPmcKillQuest = currentSubCondition.Target.Item == "AnyPmc"; // Pmc kill

            // Check if quest is scav kill or PMC kill (if enabled) and set target
            if (isScavKillQuest || (isPmcKillQuest && s4aConfig.ReplacePmcWithAll))
            {
                // Make sure the quest isn't a quest to kill bosses
                if (isNotBossKillQuest)
                {
                    // Found quest debug
                    if (s4aConfig.EnableDebug)
                    {
                        if (s4aConfig.EnableVerboseDebug)
                        {
                            m_logger.Info($"Found a {((currentSubCondition.Target.Item == "Savage") ? "scav kill" : "PMC Kill")} quest condition in quest: \"{currentQuest.QuestName}\":");
                        }
                        else
                        {
                            m_logger.Info($"Found {(s4aConfig.ReplacePmcWithAll ? "PMC" : "Scav")} kill quest {currentQuest.QuestName} [{currentQuest.Id}]");
                        }
                    }

                    // Replace the condition target
                    currentSubCondition.Target = new(null, "Any");

                    // Validate condition target
                    if (currentSubCondition.Target.Item != "Any" && s4aConfig.EnableDebug && s4aConfig.EnableVerboseDebug)
                    {
                        m_logger.Error($"Quest target change of quest: {currentQuest.QuestName}[{currentQuest.Id}] failed! If this error appears please contact mod author");
                    }

                    // If we are dealing with pmc quests and we want to scale the kill counter
                    if (isPmcKillQuest && s4aConfig.ReplacePmcWithAll && s4aConfig.ScalePmcQuests)
                    {
                        // Var to store new kill value
                        double newValue = 0d;
                        double oldValue = currentCondition.Value.Value;

                        // Get scaled up number of kills needed
                        newValue = Math.Ceiling(currentCondition.Value.Value * s4aConfig.ScalePmcMultiplier);

                        // Scale the kills, rounding up to nearest whole number
                        currentCondition.Value = newValue;

                        // Log pmc kill quests
                        if (s4aConfig.EnableDebug)
                        {
                            if (s4aConfig.EnableVerboseDebug)
                            {
                                // Ternary operator to display if we scale up pmc kill quests
                                m_logger.Success($"Doubling kill count for quest: {currentQuest.QuestName}[{currentQuest.Id}] from {oldValue} to {newValue}");
                            }
                            else
                            {
                                m_logger.Info($"Scaled PMC quest {currentQuest.QuestName}[{currentQuest.Id}]");
                            }
                        }
                    }

                    // Modify quest condidtion text
                    UpdateQuestText(currentQuest, currentCondition.Id);

                    // Track changed quests counters
                    ++noOfTotalQuestsModified;

                    // Track changed scav/pmc quests
                    if (isScavKillQuest)
                    {
                        // Track changed scav quests counter
                        ++noOfScavQuestsModified;
                    }
                    else if (isPmcKillQuest && s4aConfig.ReplacePmcWithAll)
                    {
                        // Track changed pmc quests counter
                        ++noOfPmcQuestsModified;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Changes relevant quest text with s4a text
    /// </summary>
    /// <param name="questTextID">MongoID of the quest to modify</param>
    private void UpdateQuestText(Quest quest, MongoId? questTextID)
    {
        bool validatedLocale = false;

        // Find the locale
        if (localesDb.ContainsKey(questTextID.Value))
        {
            if (s4aConfig.EnableDebug && !s4aConfig.EnableVerboseDebug)
            {
                m_logger.Success($"Quest locale for {questTextID} found!");
            }

            // Edit quest condition text, through a transformer, since SPT uses lazy loading for locales
            if (databaseService.GetLocales().Global.TryGetValue("en", out var lazyloadedValue))
            {
                lazyloadedValue.AddTransformer(lazyloadedValueData =>
                {
                    // Append (S4A) to the end of the quest text
                    lazyloadedValueData[questTextID.Value] += " (S4A)";
                    return lazyloadedValueData;
                });

                // Validate text change
                validatedLocale = lazyloadedValue.Value[questTextID].Contains(" (S4A)");

                // Debug output for validated locale change
                if (s4aConfig.EnableDebug && s4aConfig.EnableVerboseDebug)
                {
                    if (validatedLocale)
                    {
                        m_logger.Success($"Sucessfully appended quest {quest.QuestName}[{quest.Id}] locale text");
                    }
                    else
                    {
                        m_logger.Error($"Quest text modification for quest {quest.QuestName}[{quest.Id}] failed! If this error appears please contact mod author");
                    }
                }
                else if (!validatedLocale)
                {
                    m_logger.Error($"Quest text modification failed, please enable debug & verbosedebug in the config file for more info");
                }
            }
        }
    }

    /// <summary>
    /// Print summary of quest modfications
    /// </summary>
    private void PrintSummary()
    {
        m_logger.Success("Scavs4All: finished searching quest DB!");
        m_logger.Info("--------------------------------------------");
        m_logger.Success($"Found a total of {noOfTotalQuests} quests");
        m_logger.Success($"Modified a total of {noOfTotalQuestsModified} quests");
        m_logger.Success($"Modified {noOfScavQuestsModified} scav kill quests");

        // If PMC quests are being changed
        if (s4aConfig.ReplacePmcWithAll)
        {
            m_logger.Success($"Modified {noOfPmcQuestsModified} PMC kill quests");
        }
        else
        {
            logger.Warning("Did not modify PMC kill quests");
        }

        // If PMC quests are being scaled
        if (s4aConfig.ReplacePmcWithAll && s4aConfig.ScalePmcQuests)
        {
            m_logger.Success($"Scaled requied kills for PMC kill quests by {s4aConfig.ScalePmcMultiplier * 100}%");
        }
        else
        {
            m_logger.Warning("Did not scale PMC kill quests");
        }

        m_logger.Info("--------------------------------------------");
    }
}

//[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 1)]
//public class AfterSptLoadHook(DatabaseServer databaseServer, ISptLogger<Scavs4All> logger) : IOnLoad
//{

//    private Dictionary<MongoId, TemplateItem>? _itemsDb;

//    public Task OnLoad()
//    {
//        return Task.CompletedTask;
//    }
//}