using System;
using System.Collections.Generic;
using System.Linq;
using Data;
using Service;

namespace ExpandWorld.Prefab;

public class Commands
{

  public static void Run(Rule info, Functions f)
  {
    if (info.Commands.Length == 0) return;
    var commands = info.Commands.Select(c => f.Replace(c, true, false)).ToArray();
    Run(commands);
  }

  public static void Run(IEnumerable<string> commands)
  {
    foreach (var cmd in commands)
    {
      try
      {
        if (ServerDevcommands.RunCommand is { } runCommand)
          runCommand(cmd, e => Log.Error($"Failed to run command: {cmd}\n{e.Message}"));
        else
        {
          if (!ServerDevcommands.IsInstalled)
          {
            var name = cmd.Split(' ')[0].ToLower();
            if (!Terminal.commands.ContainsKey(name))
              throw new InvalidOperationException($"Unknown command '{name}'.");
          }
          Console.instance.TryRunCommand(cmd);
        }
      }
      catch (Exception e)
      {
        Log.Error($"Failed to run command: {cmd}\n{e.Message}");
      }
    }
  }
}