using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.CampaignSystem;

namespace TorNecroQoL
{
    public class SubModule : MBSubModuleBase
    {
        static SubModule() { try { Logger.Init(); Logger.Info("--- static SubModule() hit ---"); } catch { } }

        protected override void OnSubModuleLoad()
        {
            Logger.Init();
            Logger.Info("--- TorNecroQoL start " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss\\Z") + " ---");
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            Logger.Info("[SubModule] OnBeforeInitialModuleScreenSetAsRoot()");
        }

        // keep protected internal (Bannerlord expects that)
        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            try
            {
                var starter = gameStarter as CampaignGameStarter;
                if (game != null && game.GameType is Campaign && starter != null)
                {
                    starter.AddBehavior(new TorNecroQoLBehavior());
                    Logger.Info("[SubModule] Behavior registered.");
                }
                else
                {
                    Logger.Info("[SubModule] GameStart mismatch (no Campaign/Starter).");
                }
            }
            catch (Exception ex) { Logger.Info("[SubModule EX] " + ex); }
        }
    }
}
