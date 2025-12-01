using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
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
    private int noOfScavQuestsModified = 0;
    private int noOfPmcQuestsModified = 0;
    private int noOfTotalQuests = 0;
    private int noOfTotalQuestsModified = 0;
    private bool didHarderPmc = false;
    private double newValue;

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
            m_logger.Info("Iterating through quests");
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
                var currentCondition = currentQuest.Conditions.AvailableForFinish[i];

                // Check if condition is number tracker (Elimination etc.)
                if (currentCondition.ConditionType == "CounterCreator")
                {
                    // If it's a counter creator iterate through the subconditions
                    //foreach (QuestConditionCounterCondition currentSubCondition in currentCondition.Counter.Conditions)
                    for (int j = 0; j < currentCondition.Counter.Conditions.Count; ++j)
                    {
                        var currentSubCondition = currentCondition.Counter.Conditions[j];

                        // Check if the current subcondidtion is a kill condition and if it requires scav kills
                        if (currentSubCondition.ConditionType == "Kills" && currentSubCondition.Target.Item == "Savage")
                        {
                            // Make sure the quest isn't a quest to kill bosses
                            if (currentSubCondition.SavageRole == null || currentSubCondition.SavageRole.Count == 0)
                            {
                                if (debug)
                                {
                                    m_logger.Info($"Found a scav kill quest condidtion in quest: \"{currentQuest.Name}\" replacing kill condition with any");
                                }

                                // Replace the condition target
                                currentSubCondition.Target = new(null, "Any");

                                if (verboseDebug)
                                {
                                    m_logger.Info($"Quest ID is: {currentSubCondition.Id}");
                                }

                                // Modify quest condidtion text
                                ChangeQuestsText(currentCondition.Id);

                                // Track changed quests counters
                                noOfTotalQuests++;
                                noOfScavQuestsModified++;
                            }
                        }

                        // If replace pmc quests is enabled
                        if (replacePMC)
                        {
                            // Check if the quest is a pmc kill quest
                            if (currentSubCondition.ConditionType == "Kills" && currentSubCondition.Target.Item == "AnyPmc")
                            {
                                if (debug)
                                {
                                    m_logger.Info($"Found a PMC kill quest condidtion in quest: \"{currentQuest.Name}\" replacing kill condition with any");
                                }

                                // Process quest to any target
                                currentSubCondition.Target = new(null, "Any");

                                // Check if we have harder pmcwithall turned on, if we do we need to double tha amouht needed
                                if (harderPmc)
                                {
                                    // Get scaled up number of kills needed
                                    newValue = currentCondition.Value.Value * harderPmcMultiplier;

                                    if (debug)
                                    {
                                        m_logger.Info($"Harder PMC replacement conditions are ON and set to {harderPmcMultiplier}. Doubling kill count for: {currentQuest.Name} from {currentCondition.Value} to {Math.Round(newValue)}");
                                    }

                                    // Scale the kills, rounding up to nearest whole number
                                    currentCondition.Value = Math.Ceiling(newValue);
                                }
                                else
                                {
                                    if (debug)
                                    {
                                        m_logger.Info($"Harder PMC replacement conditions are OFF, not modifying quest conditions");
                                    }
                                }


                                // Modify quest condidtion text
                                ChangeQuestsText(currentCondition.Id);

                                // Track changed quests counters
                                noOfTotalQuestsModified++;
                                noOfPmcQuestsModified++;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Changes relevant quest text with s4a text
    /// </summary>
    /// <param name="questTextID">MongoID of the quest to modify</param>
    private void ChangeQuestsText(MongoId? questTextID)
    {
        // No locale with ID found
        if (questTextID == null)
        {
            if (debug)
            {
                m_logger.Warning("Quest text ID is null, cannot change quest text");
            }

            return;
        }
        else // ID exists
        {
            // Find the locale
            if (localesDb.ContainsKey(questTextID.Value))
            {

                if (verboseDebug)
                {
                    m_logger.Info($"Quest text found! Original text is {null}");
                }


                // Edit quest condition text, through a transformer, since SPT uses lazy loading for locales
                if (databaseService.GetLocales().Global.TryGetValue("en", out var lazyloadedValue))
                {
                    lazyloadedValue.AddTransformer(lazyloadedValueData =>
                    {
                        lazyloadedValueData[questTextID.Value] += " (S4A)";
                        return lazyloadedValueData;
                    });
                }

                if (verboseDebug)
                {
                    m_logger.Info($"New quest text is {null}");
                }
            }
        }
    }

    /// <summary>
    /// Process quest condition to replace target with any
    /// </summary>
    /// <param name="specificCondition">The requirements for a condition to modify</param>
    private static void ProcessQuest(QuestConditionCounterCondition specificCondition)
    {

        // Replace condition with any target
        var target = specificCondition.Target;

        // Process quest conditions
        if (target.IsItem)
        {
            var item = target.Item;
            item = "Any";
        }
        //else if (target.IsList)
        //{
        //    var list = target.List;

        //    for (int i = 0; i < list.Count; ++i)
        //    {
        //        list[i] = "Any";
        //    }
        //}
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
            m_logger.Success("Did not modify PMC kill quests");
        }
        else
        {
            m_logger.Warning($"Scaled requied kills for PMC kill quests by {harderPmcMultiplier * 100}%");
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