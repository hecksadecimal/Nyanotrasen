using Content.Server.Administration;
using Content.Server.Atmos.Components;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.Atmos.Commands
{
    [AdminCommand(AdminFlags.Debug)]
    public sealed class AddAtmosCommand : IConsoleCommand
    {
        [Dependency] private readonly IEntityManager _entities = default!;

        public string Command => "addatmos";
        public string Description => "Adds atmos support to a grid.";
        public string Help => $"{Command} <GridId>";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length < 1)
            {
                shell.WriteLine(Help);
                return;
            }

            var entMan = IoCManager.Resolve<IEntityManager>();

            if (!EntityUid.TryParse(args[0], out var euid))
            {
                shell.WriteError($"Failed to parse euid '{args[0]}'.");
                return;
            }

            if (!entMan.HasComponent<IMapGridComponent>(euid))
            {
                shell.WriteError($"Euid '{euid}' does not exist or is not a grid.");
                return;
            }

            if (_entities.HasComponent<IAtmosphereComponent>(euid))
            {
                shell.WriteLine("Grid already has an atmosphere.");
                return;
            }

            _entities.AddComponent<GridAtmosphereComponent>(euid);

            shell.WriteLine($"Added atmosphere to grid {euid}.");
        }
    }
}
