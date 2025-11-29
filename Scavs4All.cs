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
using System.Reflection;

namespace _14AfterDBLoadHook;

using Quest = Dictionary<MongoId, SPTarkov.Server.Core.Models.Eft.Common.Tables.Quest>;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class Scavs4All(DatabaseServer databaseServer, ISptLogger<Scavs4All> logger, LocaleService localeService, ModHelper modHelper) : IOnLoad
{
    // Config vars
    private bool replacePMC = false;
    private bool harderPmc = false;
    private int harderPmcMultiplier = 1;
    private bool debug = false;
    private bool verboseDebug = false;

    // Logger
    private ISptLogger<Scavs4All> logger;
    private string[] loggerBuffer;

    // Counter Vars
    private int noOfScavQuestsModified = 0;
    private int noOfPmcQuestsModified = 0;
    private int noOfTotalQuests = 0;
    private int noOfTotalQuestsModified = 0;
    private bool didHarderPmc = false;
    private float newValue = 0;

    private dynamic globalLocales;

    private Dictionary<MongoId, TemplateItem>? _itemsDb;

    public Task OnLoad()
    {
        _itemsDb = databaseServer.GetTables().Templates.Items;

        Quest quests = databaseServer.GetTables().Templates.Quests;
        //Dictionary<string, string> questsText = localeService.GetLocaleDb();

        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");

        // Set config vars from config file
        replacePMC = config.ReplacePMC;
        harderPmc = config.HarderPmc;
        harderPmcMultiplier = config.HarderPmcMultiplier;
        debug = config.Debug;
        verboseDebug = config.VerboseDebug;

        // Replace quests conditions and text
        ChangeTargets(quests);

        return Task.CompletedTask;
    }
    private void ChangeTargets(string quests)
    {
        if (verboseDebug)
        {
            logger.Info("Iterating through qeusts");
        }
        else
        {
            logger.Info("Scavs4All: Searching quest DB");
        }

        // Go through quests
        foreach (var currentQuest in quests)
        {
            noOfTotalQuests++;

            // Go through conditions of the current quest
            foreach (var questCondition in currentQuest.Value.Conditions.AvailableForFinish)
            {
                // Check if condition is number tracker (Elimination etc.)
                if (questCondition.ConditionType == "CounterCreator")
                {
                    foreach(var questSubCondition in questCondition.Counter.Conditions)
                    {
                        if(questSubCondition.ConditionType == "Kills" && questSubCondition.Target.List.Contains("Savage"))
                        {
                            if(questSubCondition.SavageRole == null || questSubCondition.SavageRole.Count == 0)
                            {
                                if(debug)
                                {
                                    logger.Info($"Found a scav kill quest condidtion in quest: \"{currentQuest.Value.Name}\" replacing kill condition with any");
                                }

                                //currentQuest.Value.Conditions.AvailableForFinish.

                                var questTextID = currentQuest.Value.Conditions.AvailableForFinish[].Id;

                                if (verboseDebug)
                                {
                                    logger.Info($"Quest ID is: {questTextID}");
                                }

                                ChangeQuestsText(ref questTextID);

                                // Track changed quests counters
                                noOfTotalQuests++;
                                noOfScavQuestsModified++;
                            }
                        }

                        // If replace pmc quests is enabled
                        if(replacePMC)
                        {
                            if (questSubCondition.ConditionType == "Kills" && questSubCondition.Target.List.Contains("AnyPmc"))
                            {
                                if(debug)
                                {
                                    logger.Info($"Found a PMC kill quest condidtion in quest: \"{currentQuest.Value.Name}\" replacing kill condition with any");
                                }

                                // TODO: Replace pmc quest with scav quest code

                                if(harderPmc)
                                {
                                    // TODO: Add code to scale up pmc kill quests with modifier

                                    if(debug)
                                    {
                                        logger.Info($"Harder PMC replacement conditions are ON and set to {harderPmcMultiplier}. Doubling kill count for: {currentQuest.Value.Name} from {questCondition.Value} to {Math.Round(newValue)}");
                                    }

                                    // TODO: Code for scaling kill amount
                                }
                                else
                                {
                                    if(debug)
                                    {
                                        logger.Info($"Harder PMC replacement conditions are OFF, not modifying quest conditions");
                                    }
                                }

                                // TODO: Code for getting ID
                                // Find quest ID to change text
                                //var questTextID = currentQuest.Value.Conditions.AvailableForFinish[].Id;

                                //ChangeQuestsTextquestTextID);

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
    /// <param name="questTextID">Quest Id to modify</param>
    private void ChangeQuestsText(ref MongoId questTextID)
    {
        foreach(var currentLocale in localeService.GetLocaleDb())
        {
            if (currentLocale.Key.Contains(questTextID.ToString()))
            {
                if (verboseDebug)
                {
                    logger.Info($"Quest text found! Original text is {null}");
                }

                currentLocale.Value = null;

                if (verboseDebug)
                {
                    logger.Info($"New quest text is {null}");
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

        if(replacePMC && harderPmc)
        {
            didHarderPmc = true;
        }

        // TODO: Make text green
        logger.Success("Scavs4All: finished searching quest DB!");
        logger.Info("--------------------------------------------");
        logger.Success($"Found a total of {noOfTotalQuests} quests");
        logger.Success($"Modified a total of {noOfTotalQuestsModified} quests");
        logger.Success($"Modified {noOfScavQuestsModified} scav kill quests");
        logger.Success($"Modified {noOfPmcQuestsModified} PMC kill quests");

        if(!didHarderPmc)
        {
            logger.Success("Did not modify PMC kill quests");
        }
        else
        {
            logger.Warning($"Scaled requied kills for PMC kill quests by {harderPmcMultiplier * 100}%");
        }
        logger.Info("--------------------------------------------");
    }
}

[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 1)]
public class AfterSptLoadHook(
    DatabaseServer databaseServer,
    ISptLogger<Scavs4All> logger) : IOnLoad
{

    private Dictionary<MongoId, TemplateItem>? _itemsDb;

    public Task OnLoad()
    {
        _itemsDb = databaseServer.GetTables().Templates.Items;

        // The modification we made above would have been processed by now by SPT, so any values we changed had
        // already been passed through the initial lifecycles (OnLoad) of SPT.

        if (_itemsDb.TryGetValue(ItemTpl.NIGHTVISION_L3HARRIS_GPNVG18_NIGHT_VISION_GOGGLES, out var nvgs))
        {
            // Lets log the state after the modification
            logger.LogWithColor($"NVGs default CanSellOnRagfair: {nvgs.Properties.CanSellOnRagfair}",
                LogTextColor.Red, LogBackgroundColor.Yellow);
        }

        return Task.CompletedTask;
    }
}

public class ModConfig
{
    public bool ReplacePMC { get; set; }
    public bool HarderPmc { get; set; }
    public int HarderPmcMultiplier { get; set; }
    public bool Debug { get; set; }
    public bool VerboseDebug { get; set; }
}

