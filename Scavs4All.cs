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

namespace Scavs4All;

// Alias for easier access to quests dictionary
using Quests = Dictionary<MongoId, SPTarkov.Server.Core.Models.Eft.Common.Tables.Quest>;

// TODO: Add code to generate new config.json file if one is not found
// TODO: Add quest blacklist to exclude certain quests from being modified
// TODO: Add code to change 'Scav' or 'PMC' in quest text to 'Any Targets'

public class S4AConfig
{
    //[JsonPropertyName("ReplacePMCWithAll")]
    public bool ReplacePMCWithAll { get; set; }
    public bool HarderPMCWithAll { get; set; }
    public int HarderPMCMultiplier { get; set; }
    public bool debug { get; set; }
    public bool verboseDebug { get; set; }
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class Scavs4All(DatabaseServer databaseServer, ISptLogger<Scavs4All> logger, LocaleService localeService, ModHelper modHelper, DatabaseService databaseService) : IOnLoad
{
    // Config vars
    private bool replacePMC;
    private bool harderPmc;
    private double harderPmcMultiplier = 1d;
    private bool debug;
    private bool verboseDebug;

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
        string pathToMod;
        S4AConfig config;

        // Load config file
        try
        {
            // Load config file (Var names must match names in json file, otherwise [JsonPropertyName("")] needs to be used)
            pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            config = modHelper.GetJsonDataFromFile<S4AConfig>(pathToMod, "config.json");

            // Set config vars from config file
            replacePMC = config.ReplacePMCWithAll;
            harderPmc = config.HarderPMCWithAll;
            harderPmcMultiplier = config.HarderPMCMultiplier;
            debug = config.debug;
            verboseDebug = config.verboseDebug;
        }
        catch (Exception e) // Config file not found
        {
            m_logger.Error($"Scavs4All: Error loading config file: {e.Message}");
            return Task.CompletedTask;
        }

        // Check if pmc quests are being modified
        if (debug && verboseDebug)
        {
            m_logger.Warning($"Harder PMC quest count is {(harderPmc ? $"enabled and set to {harderPmcMultiplier}." : "disabled, not modifying quest counts")}");
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
        if (verboseDebug)
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
            if (isScavKillQuest || (isPmcKillQuest && replacePMC))
            {
                // Make sure the quest isn't a quest to kill bosses
                if (isNotBossKillQuest)
                {
                    // Found quest debug
                    if (debug)
                    {
                        if (verboseDebug)
                        {
                            m_logger.Info($"Found a {((currentSubCondition.Target.Item == "Savage") ? "scav kill" : "PMC Kill")} quest condition in quest: \"{currentQuest.QuestName}\":");
                        }
                        else
                        {
                            m_logger.Info($"Found {(replacePMC ? "PMC" : "Scav")} kill quest {currentQuest.QuestName} [{currentQuest.Id}]");
                        }
                    }

                    // Replace the condition target
                    currentSubCondition.Target = new(null, "Any");

                    // Validate condition target
                    if (currentSubCondition.Target.Item != "Any" && debug && verboseDebug)
                    {
                        m_logger.Error($"Quest target change of quest: {currentQuest.QuestName}[{currentQuest.Id}] failed! If this error appears please contact mod author");
                    }

                    // If we are dealing with pmc quests and we want to scale the kill counter
                    if (isPmcKillQuest && replacePMC && harderPmc)
                    {
                        // Var to store new kill value
                        double newValue = 0d;
                        double oldValue = currentCondition.Value.Value;

                        // Get scaled up number of kills needed
                        newValue = Math.Ceiling(currentCondition.Value.Value * harderPmcMultiplier);

                        // Scale the kills, rounding up to nearest whole number
                        currentCondition.Value = newValue;

                        // Log pmc kill quests
                        if (debug)
                        {
                            if (verboseDebug)
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
                    else if (isPmcKillQuest && replacePMC)
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
            if (debug && !verboseDebug)
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
                if (debug && verboseDebug)
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
                else if(!validatedLocale)
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
        didHarderPmc = false;

        if (replacePMC && harderPmc)
        {
            didHarderPmc = true;
        }

        m_logger.Success("Scavs4All: finished searching quest DB!");
        m_logger.Info("--------------------------------------------");
        m_logger.Success($"Found a total of {noOfTotalQuests} quests");
        m_logger.Success($"Modified a total of {noOfTotalQuestsModified} quests");
        m_logger.Success($"Modified {noOfScavQuestsModified} scav kill quests");
        m_logger.Success($"Modified {noOfPmcQuestsModified} PMC kill quests");

        if (!didHarderPmc)
        {
            m_logger.Warning("Did not scale PMC kill quest counts");
        }
        else
        {
            m_logger.Success($"Scaled requied kills for PMC kill quests by {harderPmcMultiplier * 100}%");
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