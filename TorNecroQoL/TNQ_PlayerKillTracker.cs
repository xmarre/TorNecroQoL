// TNQ_PlayerKillTracker.cs
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.CampaignSystem;

namespace TorNecroQoL
{
    internal static class TNQ_KillSnapshot
    {
        public static int LastMissionPlayerKills;
    }

    internal sealed class TNQ_PlayerKillTracker : MissionBehavior
    {
        private const bool DEBUG_PK = true;
        private int _playerKills;

        public override void OnBehaviorInitialize()
        {
            _playerKills = 0;
            if (DEBUG_PK) Logger.Info("[TNQ] KillTracker attached");
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow blow)
        {
            if (affected == null) return;
            // count only hard kills (not KOs/despawns) and only vs enemies & humans
            if (state != AgentState.Killed && state != AgentState.Unconscious) return;
            if (!affected.IsHuman) return;

            // resolve killer: mount trample -> attribute to rider
            Agent killer = affector;
            if (killer == null) return;
            if (killer.IsMount && killer.RiderAgent != null)
                killer = killer.RiderAgent;

            // require the killer to be the player
            bool isPlayer =
                killer.IsMainAgent
                || (killer.MissionPeer != null
                    && killer.MissionPeer.ControlledAgent != null
                    && killer.MissionPeer.ControlledAgent.IsMainAgent);

            if (isPlayer)
            {
                // avoid friendly fire counting
                if (affected.Team != null && killer.Team != null && !affected.Team.IsEnemyOf(killer.Team)) return;
                _playerKills++;
                if (DEBUG_PK) Logger.Info($"[TNQ] +1 ({state}) by player; total={_playerKills}");
                return;
            }
            var hero = (killer.Character as CharacterObject)?.HeroObject;
            if (hero != null && ReferenceEquals(hero, Hero.MainHero))
            {
                if (affected.Team != null && killer.Team != null && !affected.Team.IsEnemyOf(killer.Team)) return;
                _playerKills++;
                if (DEBUG_PK) Logger.Info($"[TNQ] +1 ({state}) by Hero.MainHero; total={_playerKills}");
            }
        }

        protected override void OnEndMission()
        {
            TNQ_KillSnapshot.LastMissionPlayerKills = _playerKills;
        }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    }
}
