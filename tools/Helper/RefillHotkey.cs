using System;
using System.Reflection;
using UnityEngine;

namespace SkyForceTweaks
{
    /// <summary>补充库存而不触发拾取事件，避免满槽拾取自动释放武器。</summary>
    internal static class RefillHotkey
    {
        private static readonly FieldInfo LaserField = typeof(Player).GetField(
            "weapon_laser", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ShieldSlotsField = typeof(ShipEquipment_EnergyShield).GetField(
            "m_nNumActvieSlots", BindingFlags.Static | BindingFlags.NonPublic);

        private static int s_LastInputFrame = -1;
        private static float s_MessageUntil;
        private static string s_Message;

        public static void Tick(bool promptActive)
        {
            // OnGUI 每帧有多个事件；同一次按键只执行一次。
            if (Input.GetKeyDown(KeyCode.F8) && s_LastInputFrame != Time.frameCount)
            {
                s_LastInputFrame = Time.frameCount;
                var player = Player.singleton;
                if (!promptActive && Time.timeScale > 0f && player != null &&
                    (player.FlyingState == Player.EState.FLYING || player.FlyingState == Player.EState.FURY))
                {
                    try
                    {
                        Refill(player);
                        s_Message = "AMMO REFILLED";
                        Debug.Log("[SFTweaks] ammo refilled: laser=" + SaveGame.CollectedNumLaser +
                            " bomb=" + SaveGame.CollectedNumMegaBomb +
                            " shield=" + SaveGame.CollectedNumEnergyShield);
                    }
                    catch (Exception ex)
                    {
                        s_Message = "REFILL FAILED - CHECK LOG";
                        Debug.Log("[SFTweaks] ammo refill failed: " + ex);
                    }
                    s_MessageUntil = Time.realtimeSinceStartup + 2f;
                }
            }

            if (Time.realtimeSinceStartup < s_MessageUntil)
            {
                GUI.Box(new Rect(Screen.width - 306f, Screen.height - 64f, 300f, 28f), s_Message);
            }
        }

        private static void Refill(Player player)
        {
            var data = TSingleton<GlobalGameData>.singleton;
            if (data == null || LaserField == null || ShieldSlotsField == null)
            {
                throw new InvalidOperationException("Required refill fields unavailable");
            }

            var laser = LaserField.GetValue(player) as Weapon_Laser;
            if (SaveGame.UpgradeLevelLaser >= 0 && laser == null)
            {
                throw new InvalidOperationException("Equipped laser unavailable");
            }

            // 使用武器实例的容量，不能只改 SaveGame：激光另有运行时能量缓存。
            if (SaveGame.UpgradeLevelLaser >= 0)
            {
                laser.energyPercent = 1f;
                SaveGame.CollectedNumLaser = Mathf.RoundToInt(laser.TotalEnergy);
            }
            if (SaveGame.UpgradeLevelMegaBomb >= 0)
            {
                SaveGame.CollectedNumMegaBomb = data._MaxNumCollectedMegaBomb;
            }
            if (SaveGame.UpgradeLevelEnergyShield >= 0)
            {
                // 护盾扣次数读静态缓存；两处必须同步，否则下一次使用会掉回旧库存。
                ShieldSlotsField.SetValue(null, data._MaxNumCollectedEnergyShield);
                SaveGame.CollectedNumEnergyShield = data._MaxNumCollectedEnergyShield;
            }
        }
    }
}
