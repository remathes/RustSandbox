namespace RustSandbox.Services;

/// <summary>Console commands offered as autocomplete suggestions (vanilla Rust + Oxide/Carbon).</summary>
public static class RustCommands
{
    public sealed record Command(string Name, string Usage, string Description);

    public static readonly Command[] All =
    {
        new("status", "status", "Players online with ping and connection time"),
        new("serverinfo", "serverinfo", "FPS, entities, memory and network as JSON"),
        new("playerlist", "playerlist", "Online players as JSON"),
        new("say", "say \"message\"", "Broadcast a chat message to everyone"),
        new("kick", "kick <steamid|name> \"reason\"", "Disconnect a player"),
        new("kickall", "kickall \"reason\"", "Disconnect every player"),
        new("ban", "ban <steamid|name> \"reason\"", "Ban an online player"),
        new("banid", "banid <steamid> \"name\" \"reason\"", "Ban by Steam ID (works offline)"),
        new("unban", "unban <steamid>", "Lift a ban"),
        new("banlistex", "banlistex", "List bans with reasons"),
        new("moderatorid", "moderatorid <steamid> \"name\" \"reason\"", "Give moderator (auth level 1)"),
        new("ownerid", "ownerid <steamid> \"name\" \"reason\"", "Give owner (auth level 2)"),
        new("removemoderator", "removemoderator <steamid>", "Remove moderator"),
        new("removeowner", "removeowner <steamid>", "Remove owner"),
        new("server.save", "server.save", "Save the world now"),
        new("server.writecfg", "server.writecfg", "Write current convars to cfg/serverauto.cfg"),
        new("server.hostname", "server.hostname \"name\"", "Change the server browser name"),
        new("server.maxplayers", "server.maxplayers <n>", "Change max players"),
        new("server.description", "server.description \"text\"", "Change the description"),
        new("env.time", "env.time <0-24>", "Set the time of day"),
        new("weather.load", "weather.load <Clear|Dust|Fog|Overcast|RainHeavy|RainMild|Storm>", "Apply a weather preset"),
        new("decay.scale", "decay.scale <0-1>", "Decay speed multiplier"),
        new("fps.limit", "fps.limit <n>", "Server frame-rate cap"),
        new("inventory.giveto", "inventory.giveto <steamid|name> <shortname> <amount>", "Give items to a player"),
        new("teleport", "teleport <player> <target player>", "Teleport one player to another"),
        new("teleportpos", "teleportpos <x> <y> <z>", "Teleport yourself to a position (in-game)"),
        new("restart", "restart <seconds> \"reason\"", "Countdown then shut down (needs a wrapper to come back)"),
        new("quit", "quit", "Save and shut down the server"),
        new("oxide.plugins", "oxide.plugins", "List loaded Oxide plugins"),
        new("oxide.reload", "oxide.reload <name|*>", "Reload a plugin"),
        new("oxide.load", "oxide.load <name>", "Load a plugin"),
        new("oxide.unload", "oxide.unload <name>", "Unload a plugin"),
        new("oxide.grant", "oxide.grant <user|group> <name> <permission>", "Grant a permission"),
        new("oxide.revoke", "oxide.revoke <user|group> <name> <permission>", "Revoke a permission"),
        new("oxide.usergroup", "oxide.usergroup <add|remove> <player> <group>", "Add or remove a player from a group"),
        new("oxide.show", "oxide.show <perms|perm|user|group|groups> [name]", "Inspect permissions"),
        new("c.plugins", "c.plugins", "List loaded Carbon plugins"),
        new("c.reload", "c.reload <name|*>", "Reload a Carbon plugin"),
        new("c.grant", "c.grant <user|group> <name> <permission>", "Grant a Carbon permission"),
    };

    public static IEnumerable<Command> Suggest(string input, int take = 8)
    {
        var text = input.TrimStart();
        if (text.Length == 0 || text.Contains(' ')) return Enumerable.Empty<Command>();
        return All.Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) && !c.Name.Equals(text, StringComparison.OrdinalIgnoreCase))
            .Concat(All.Where(c => !c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) && c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Take(take);
    }
}
