// TNQ_PlayerKillTracker.cs
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
        private int _playerKills;

        public override void OnBehaviorInitialize()
        {
            _playerKills = 0;
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow blow)
        {
            if (affected == null) return;
            // count only hard kills (not KOs/despawns) and only vs enemies & humans
            if (state != AgentState.Killed) return;
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
                return;
            }
            var hero = killer.Character?.HeroObject;
            if (hero != null && ReferenceEquals(hero, Hero.MainHero))
            {
                if (affected.Team != null && killer.Team != null && !affected.Team.IsEnemyOf(killer.Team)) return;
                _playerKills++;
            }
        }

        public override void OnMissionEnded()
        {
            TNQ_KillSnapshot.LastMissionPlayerKills = _playerKills;
        }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    }
}
