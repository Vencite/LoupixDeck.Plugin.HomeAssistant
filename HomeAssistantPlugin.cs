using LoupixDeck.PluginSdk;
using LoupixDeck.Plugin.HomeAssistant.Commands;

namespace LoupixDeck.Plugin.HomeAssistant;

public sealed class HomeAssistantPlugin : LoupixPlugin
{
    private IPluginHost? _host;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "homeassistant",
        Name = "Home Assistant",
        Version = new Version(0, 1, 0),
        SdkVersion = SdkInfo.Version,
        Author = "Vencite",
        Description = "Control Home Assistant entities directly from LoupixDeck."
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _host.Logger.Info("Home Assistant plugin scaffold loaded.");
    }

    public override void Shutdown()
    {
        _host?.Logger.Info("Home Assistant plugin scaffold stopped.");
        _host = null;
    }

    public override IEnumerable<IPluginCommand> GetCommands()
    {
        yield return new ScaffoldPingCommand();
    }

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = "Home Assistant",
            Description = "Home Assistant control",
            Section = CommandGroupSection.Plugins
        }
    ];
}
