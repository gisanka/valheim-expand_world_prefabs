using System;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Service;
namespace ExpandWorld.Prefab;

public static class ServerDevcommands
{
  internal static bool IsInstalled => Chainloader.PluginInfos.ContainsKey("server_devcommands");
  internal static Action<string, Action<Exception>>? RunCommand { get; private set; }

  public static void Init()
  {
    if (!Chainloader.PluginInfos.TryGetValue("server_devcommands", out var plugin)) return;
    var assembly = plugin.Instance.GetType().Assembly;
    var method = assembly.GetType("ServerDevcommands.Api")?.GetMethod("RunCommand", [typeof(string), typeof(Action<Exception>)]);
    if (method != null && method.IsStatic)
      RunCommand = Delegate.CreateDelegate(typeof(Action<string, Action<Exception>>), method, false) as Action<string, Action<Exception>>;
    var settings = assembly.GetType("ServerDevcommands.Settings");
    if (settings?.GetField("configServerChatName")?.GetValue(null) is not ConfigEntry<string> entry) return;
    ServerClient.SetName(entry.Value);
    entry.SettingChanged += (s, e) => ServerClient.SetName(entry.Value);
  }
}
