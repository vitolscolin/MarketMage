using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MarketMage.Windows;

namespace MarketMage;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/marketmage";

    public readonly WindowSystem WindowSystem = new("MarketMage");
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        MainWindow = new MainWindow(DataManager, Log, PluginInterface, PlayerState);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open MarketMage.",
        });

        Framework.Update += OnUpdate;
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        Log.Information("MarketMage loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        CommandManager.RemoveHandler(CommandName);

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
    }

    private void OnUpdate(IFramework framework) => MainWindow.UpdateOpportunities();

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    private void OpenMainUi()
    {
        MainWindow.IsOpen = true;
    }
}
