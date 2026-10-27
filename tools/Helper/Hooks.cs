using System;
using System.Reflection;
using UnityEngine;

namespace SkyForceTweaks
{
    /// <summary>
    /// 由 Assembly-CSharp.dll 里的 IL 补丁调用（不经过 Unity 消息）。
    /// 独立程序集，放在 Managed/ 下由 mono 解析。
    /// </summary>
    public static class Hooks
    {
        private static int s_RevivesLeft = GeneratedConfig.RevivesPerStage;

        private static Player s_Patient;
        private static bool s_PromptActive;
        private static float s_Deadline;

        /// <summary>复活后的无敌保护（1.5s 复活 + 2.5s 护体）。</summary>
        private static Player s_Protected;
        private static float s_ProtectUntil;

        public static void ResetStage()
        {
            s_RevivesLeft = GeneratedConfig.RevivesPerStage;
            s_Patient = null;
            s_PromptActive = false;
            s_Protected = null;
            s_ProtectUntil = 0f;
            s_Shield = null;
        }

        /// <summary>
        /// 游戏启动时调一次（挂在 CardsManager.Awake 末尾），用于确认 helper 程序集能被加载。
        /// </summary>
        public static void OnGameBoot()
        {
            Debug.Log("[SFTweaks] helper loaded, revives=" + s_RevivesLeft);
        }

        /// <summary>
        /// Stage.onPlayerDestroyed(Player) 的入口钩子。
        /// 返回 true 表示"我接管了，别执行原方法"（原方法会扣命并触发 GameOver）。
        /// </summary>
        public static bool OnPlayerDestroyed(Stage stage, Player player)
        {
            if (!GeneratedConfig.ReviveEnabled || player == null || player == Player.friendPlayer)
            {
                return false;
            }

            if (s_RevivesLeft <= 0)
            {
                Debug.Log("[SFTweaks] no revives left → vanilla game over");
                return false;
            }

            s_Patient = player;
            s_PromptActive = true;
            s_Deadline = Time.realtimeSinceStartup + 30f;

            // 冻结整场。OnGUI 不受 timeScale 影响，弹窗与输入都在 OnGuiTick 里跑。
            Time.timeScale = 0f;
            Debug.Log("[SFTweaks] revive prompt, left=" + s_RevivesLeft);
            return true;
        }

        /// <summary>
        /// 由 CardsManager.OnGUI 每帧调用。timeScale=0 时依然会被触发。
        /// </summary>
        public static void OnGuiTick()
        {
            TickProtection();
            RefillHotkey.Tick(s_PromptActive);

            if (!s_PromptActive)
            {
                return;
            }

            const float width = 460f;
            const float height = 200f;
            var x = (Screen.width - width) * 0.5f;
            var y = (Screen.height - height) * 0.5f;

            GUI.skin.label.fontSize = 18;
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUI.skin.label.normal.textColor = new Color(1f, 0.95f, 0.45f, 1f);

            GUI.Box(new Rect(x, y, width, height), "");
            GUI.Label(new Rect(x, y + 16f, width, 32f), "SHIP DESTROYED");
            GUI.Label(new Rect(x, y + 56f, width, 26f), "Revives left: " + s_RevivesLeft);

            var reviveChosen = false;
            var giveUpChosen = false;

            if (GUI.Button(new Rect(x + 32f, y + 118f, 180f, 54f), "REVIVE (A)"))
            {
                reviveChosen = true;
            }
            else if (GUI.Button(new Rect(x + 248f, y + 118f, 180f, 54f), "GIVE UP (B)"))
            {
                giveUpChosen = true;
            }
            else if (Input.GetKeyDown(KeyCode.JoystickButton0) || Input.GetKeyDown(KeyCode.Return))
            {
                reviveChosen = true;
            }
            else if (Input.GetKeyDown(KeyCode.JoystickButton1) || Input.GetKeyDown(KeyCode.Escape))
            {
                giveUpChosen = true;
            }
            else if (Time.realtimeSinceStartup > s_Deadline)
            {
                giveUpChosen = true;
            }

            if (reviveChosen)
            {
                Finish(revive: true);
            }
            else if (giveUpChosen)
            {
                Finish(revive: false);
            }
        }

        private static ShipEquipment_EnergyShield s_Shield;

        /// <summary>恢复死亡时被 SetActive(false) 的主武器 GameObject。</summary>
        private static void RestoreWeaponObjects(Player player)
        {
            try
            {
                var field = typeof(Player).GetField(
                    "weapons_dictionary",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var dict = field == null ? null : field.GetValue(player) as System.Collections.IDictionary;
                if (dict == null)
                {
                    return;
                }

                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var weapon = entry.Value as Component;
                    if (weapon == null)
                    {
                        continue;
                    }

                    var name = entry.Key.ToString();
                    if (name == "SingleShot")
                    {
                        weapon.gameObject.SetActive(true);
                    }
                    else if (name == "DoubleShot")
                    {
                        // 和游戏自身的 ReviveShip 保持一致：只有装备了双发才显示。
                        weapon.gameObject.SetActive(player.EquippedDoubleShot());
                    }
                }

                Debug.Log("[SFTweaks] weapon objects restored");
            }
            catch (Exception ex)
            {
                Debug.Log("[SFTweaks] weapon restore failed: " + ex.Message);
            }
        }

        /// <summary>复活时给一层能量护盾（走游戏自己的 ActivateOnce，bLuckyShield=true 不扣槽）。</summary>
        private static void GiveReviveShield(Player player)
        {
            try
            {
                var shield = GetOrCreateEnergyShield(player);
                if (shield == null)
                {
                    Debug.Log("[SFTweaks] energy shield unavailable");
                    return;
                }

                shield.ActivateOnce(true);
                Debug.Log("[SFTweaks] energy shield on");
            }
            catch (Exception ex)
            {
                Debug.Log("[SFTweaks] energy shield failed: " + ex.Message);
            }
        }

        private static ShipEquipment_EnergyShield GetOrCreateEnergyShield(Player player)
        {
            if (s_Shield != null)
            {
                return s_Shield;
            }

            var field = typeof(Player).GetField(
                "m_COEnergyShield",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field != null)
            {
                var existing = field.GetValue(player) as ShipEquipment_EnergyShield;
                if (existing != null)
                {
                    s_Shield = existing;
                    return existing;
                }
            }

            if (player._EnergyShieldPrefab == null)
            {
                return null;
            }

            var go = UnityEngine.Object.Instantiate(player._EnergyShieldPrefab) as GameObject;
            if (go == null)
            {
                return null;
            }

            var shield = go.GetComponent<ShipEquipment_EnergyShield>();
            if (shield == null)
            {
                return null;
            }

            go.transform.SetParent(player.transform, false);
            go.transform.localPosition = Vector3.zero;
            shield._Player = player;
            shield.SetUpgradeLvl(SaveGame.UpgradeLevelEnergyShield, SaveGame.CollectedNumEnergyShield);

            if (field != null)
            {
                field.SetValue(player, shield);
            }

            s_Shield = shield;
            Debug.Log("[SFTweaks] energy shield component created");
            return shield;
        }

        /// <summary>把玩家重新登记回 Player._Players（并触发 EventPlayerCreated）。</summary>
        private static void ReRegisterPlayer(Player player)
        {
            try
            {
                var method = typeof(Player).GetMethod(
                    "LaunchPlayerCreatedEvent",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (method != null)
                {
                    method.Invoke(player, null);
                }
                else if (Player._Players != null && !Player._Players.Contains(player))
                {
                    Player._Players.Add(player);
                }

                Debug.Log("[SFTweaks] re-registered, players=" + Player._Players.Count);
            }
            catch (Exception ex)
            {
                Debug.Log("[SFTweaks] re-register failed: " + ex.Message);
            }
        }

        private static void TickProtection()
        {
            if (s_ProtectUntil <= 0f || Time.realtimeSinceStartup < s_ProtectUntil)
            {
                return;
            }

            s_ProtectUntil = 0f;
            if (s_Protected != null)
            {
                s_Protected.DisableInvincibilty();
                Debug.Log("[SFTweaks] protection ended");
            }

            s_Protected = null;
        }

        private static void Finish(bool revive)
        {
            var patient = s_Patient;
            s_Patient = null;
            s_PromptActive = false;

            // 先恢复时间，再走后续流程（GameOver 动画和 ReviveShip 都依赖 timeScale=1）。
            Time.timeScale = 1f;

            if (revive && patient != null)
            {
                s_RevivesLeft--;

                // 先记录复活前的真实状态，方便定位"协程不跑"这类问题。
                Debug.Log("[SFTweaks] revive accepted, left=" + s_RevivesLeft
                          + " activeInHierarchy=" + patient.gameObject.activeInHierarchy
                          + " enabled=" + patient.enabled
                          + " state=" + patient.FlyingState
                          + " ship=" + (patient.ShipModel != null && patient.ShipModel.gameObject.activeInHierarchy));

                // 同步复活（不走协程，避免组件/对象状态导致协程不执行）：
                // 游戏自带的 ReviveShip 也只做这些事，区别是它用 yield 分两步。
                patient.gameObject.SetActive(true);
                patient.enabled = true;

                if (patient.ShipModel != null)
                {
                    patient.ShipModel.gameObject.SetActive(true);
                }

                if (patient._ShipCollider != null)
                {
                    patient._ShipCollider.enabled = true;
                }

                patient.Shield = patient.CurrentMaxShield;
                patient.m_fReviveTimer = 1.5f;
                patient.FlyingState = Player.EState.REVIVING;   // 倒计时结束后 UpdateReviving 自动切 FLYING

                // EvShipDestroyed 末尾会把玩家从 Player._Players 摘掉，
                // 而 Bonus.OnTriggerEnter 是靠遍历 _Players 找出玩家的 —— 不补回来就吃不到任何东西。
                ReRegisterPlayer(patient);

                // 死亡时 SingleShot/DoubleShot 的 GameObject 被 SetActive(false)，
                // EnableWeapons() 只改 m_bActive 字段、管不到 GameObject，这里精确反向恢复。
                RestoreWeaponObjects(patient);

                // 套甲：复活时给一层游戏原生的能量护盾（不消耗存档槽位）。
                GiveReviveShield(patient);

                // 护体：复活期间 + 之后 2.5 秒无敌（REVIVING 状态本身也不吃伤害，这里覆盖切回 FLYING 之后）。
                patient.EnableInvincibilty();
                s_Protected = patient;
                s_ProtectUntil = Time.realtimeSinceStartup + 4f;

                Debug.Log("[SFTweaks] after revive state=" + patient.FlyingState
                          + " ship=" + (patient.ShipModel != null && patient.ShipModel.gameObject.activeInHierarchy)
                          + " shield=" + patient.Shield);
                return;
            }

            Debug.Log("[SFTweaks] revive declined → vanilla game over");
            var stage = Stage.singleton;
            if (stage != null)
            {
                stage.EvGameOver();
            }
        }
    }
}
