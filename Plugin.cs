using MelonLoader;

[assembly: MelonInfo(
    typeof(GunModifier.Plugin),
    "Gun Modifier",
    "1.0.0",
    "GunModifier")]

[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace GunModifier
{
    public class Plugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("Gun Modifier loaded!");
            MelonLogger.Msg("Plugin initialized successfully.");
        }
    }
}
