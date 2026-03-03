using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace IGTAPSaveSlots
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        private static readonly string GameSavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Pepper tango games", "IGTAP");

        private static readonly string SlotBasePath = Path.Combine(
            Paths.PluginPath, "..", "IGTAP_SaveSlots");

        private static readonly string WorkingFilePath = Path.Combine(SlotBasePath, "WORKING");

        private const int MAX_SLOTS = 10;
        private int currentSlot = 0;

        private bool isBusy = false;
        private string statusMessage = "";
        private float statusTimer = 0f;

        private GUIStyle slotStyle;
        private GUIStyle statusStyle;

        // Cached components
        private MonoBehaviour pauseMenu;
        private MethodInfo changeSceneMethod;
        private MonoBehaviour saveloaderComp;
        private MethodInfo manualSaveMethod;
        private FieldInfo isSavingField;
        private FieldInfo isOnMainMenuField;

        private void Awake()
        {
            Logger = base.Logger;

            Directory.CreateDirectory(SlotBasePath);
            for (int i = 0; i < MAX_SLOTS; i++)
                Directory.CreateDirectory(GetSlotPath(i));

            CheckAndRecoverFromCrash();

            slotStyle = new GUIStyle();
            slotStyle.fontSize = 20;
            slotStyle.normal.textColor = Color.cyan;
            slotStyle.alignment = TextAnchor.UpperRight;

            statusStyle = new GUIStyle();
            statusStyle.fontSize = 20;
            statusStyle.normal.textColor = Color.yellow;
            statusStyle.alignment = TextAnchor.UpperCenter;

            SceneManager.sceneLoaded += OnSceneLoaded;

            Logger.LogInfo($"Save Slot Manager loaded. Slots: {SlotBasePath}");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            pauseMenu = null;
            saveloaderComp = null;
            Logger.LogInfo($"Scene loaded: {scene.name}");
        }

        // -------------------------
        // Sentinel / crash recovery
        // -------------------------

        private void WriteSentinel(string operation, string src, string dest)
        {
            File.WriteAllText(WorkingFilePath,
                $"operation={operation}\n" +
                $"src={src}\n" +
                $"dest={dest}\n" +
                $"timestamp={DateTime.UtcNow:O}");
        }

        private void ClearSentinel()
        {
            if (File.Exists(WorkingFilePath))
                File.Delete(WorkingFilePath);
        }

        private bool ShouldSkipFile(string file)
        {
            return Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase);
        }

        private void ClearDirectory(string path)
        {
            if (!Directory.Exists(path)) return;

            foreach (var file in Directory.GetFiles(path))
            {
                if (ShouldSkipFile(file))
                {
                    continue;
                }
                File.Delete(file);
            }

            foreach (var subDir in Directory.GetDirectories(path))
            {
                Directory.Delete(subDir, recursive: true);
                // subdirs won't contain Player.log so safe to nuke entirely
            }
        }

        private void CheckAndRecoverFromCrash()
        {
            if (!File.Exists(WorkingFilePath)) return;

            string[] lines = File.ReadAllLines(WorkingFilePath);
            string operation = "", src = "", dest = "";

            foreach (var line in lines)
            {
                if (line.StartsWith("operation=")) operation = line.Substring("operation=".Length);
                else if (line.StartsWith("src=")) src = line.Substring("src=".Length);
                else if (line.StartsWith("dest=")) dest = line.Substring("dest=".Length);
            }

            Logger.LogWarning($"[RECOVERY] Found WORKING file! Last op: {operation} | {src} -> {dest}");
            ShowStatus($"Crash recovery: retrying {operation}...");

            try
            {
                if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(dest) && Directory.Exists(src))
                {
                    Logger.LogInfo($"[RECOVERY] Re-copying {src} -> {dest}");
                    ClearDirectory(dest);
                    CopyDirectory(src, dest);
                    ClearSentinel();
                    ShowStatus("Crash recovery complete!");
                    Logger.LogInfo("[RECOVERY] Done.");
                }
                else
                {
                    Logger.LogWarning("[RECOVERY] Could not recover - src missing or paths empty. Clearing WORKING file.");
                    ClearSentinel();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[RECOVERY] Recovery failed: {ex.Message}");
            }
        }

        // -------------------------
        // Helpers
        // -------------------------

        private string GetSlotPath(int slot) =>
            Path.Combine(SlotBasePath, $"Slot_{slot}");

        private bool SlotHasData(int slot) =>
            Directory.GetFiles(GetSlotPath(slot), "*", SearchOption.AllDirectories).Length > 0;

        private void CopyDirectory(string src, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.GetFiles(src))
            {
                if (Path.GetFileName(file).Equals("Player.log", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var subDir in Directory.GetDirectories(src))
            {
                string destSubDir = Path.Combine(dest, Path.GetFileName(subDir));
                CopyDirectory(subDir, destSubDir);
            }
        }

        private void ShowStatus(string msg)
        {
            statusMessage = msg;
            statusTimer = 3f;
            Logger.LogInfo($"[Status] {msg}");
        }

        private Type GetTypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == name) return t;
                }
                catch { }
            }
            return null;
        }

        private void TryFindComponents()
        {
            if (pauseMenu == null)
            {
                var obj = FindObjectOfType(GetTypeByName("pauseMenuScript")) as MonoBehaviour;
                if (obj != null)
                {
                    pauseMenu = obj;
                    changeSceneMethod = pauseMenu.GetType().GetMethod("changeScene",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    Logger.LogInfo("Found pauseMenuScript");
                }
            }

            if (saveloaderComp == null)
            {
                var obj = FindObjectOfType(GetTypeByName("Saveloader")) as MonoBehaviour;
                if (obj != null)
                {
                    saveloaderComp = obj;
                    manualSaveMethod = saveloaderComp.GetType().GetMethod("manualSave",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    isSavingField = saveloaderComp.GetType().GetField("IsSaving",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    Logger.LogInfo("Found Saveloader");
                }
            }

            if (isOnMainMenuField == null)
            {
                Type movementType = GetTypeByName("Movement");
                if (movementType != null)
                    isOnMainMenuField = movementType.GetField("isOnMainMenu",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }

        // -------------------------
        // Wait helpers
        // -------------------------

        private IEnumerator WaitForSaveComplete(string statusPrefix)
        {
            // Wait for IsSaving to flip true (may not be instant)
            float timeout = 3f;
            while (timeout > 0f && isSavingField != null && saveloaderComp != null)
            {
                if ((bool)isSavingField.GetValue(saveloaderComp)) break;
                timeout -= Time.unscaledDeltaTime;
                yield return null;
            }

            // Wait for IsSaving to go false (save finished)
            timeout = 10f;
            while (timeout > 0f && isSavingField != null && saveloaderComp != null)
            {
                Logger.LogInfo(isSavingField.GetValue(saveloaderComp));
                if (!(bool)isSavingField.GetValue(saveloaderComp)) break;
                timeout -= Time.unscaledDeltaTime;
                ShowStatus($"{statusPrefix} ({timeout:F0}s)");
                yield return null;
            }

            Logger.LogInfo("IsSaving is false - save complete");
        }

        private IEnumerator WaitForMainMenu()
        {
            float timeout = 15f;
            while (timeout > 0f)
            {
                timeout -= Time.unscaledDeltaTime;

                string sceneName = SceneManager.GetActiveScene().name.ToLower();
                if (sceneName.Contains("menu") || sceneName.Contains("main"))
                    break;

                if (isOnMainMenuField != null)
                {
                    var player = GameObject.FindWithTag("Player");
                    if (player != null)
                    {
                        var movComp = player.GetComponent("Movement") as MonoBehaviour;
                        if (movComp != null && (bool)isOnMainMenuField.GetValue(movComp))
                            break;
                    }
                }

                ShowStatus($"Waiting for menu... ({timeout:F0}s)");
                yield return null;
            }

            // Small buffer for any final writes after scene transition
            yield return new WaitForSecondsRealtime(0.5f);
        }

        // -------------------------
        // Core operations
        // -------------------------

        private IEnumerator DoSaveToSlot(int slot)
        {
            isBusy = true;
            ShowStatus("Saving game...");

            if (saveloaderComp != null && manualSaveMethod != null)
            {
                manualSaveMethod.Invoke(saveloaderComp, null);
                Logger.LogInfo("Triggered manualSave()");
                yield return StartCoroutine(WaitForSaveComplete("Waiting for save..."));
            }
            else
            {
                Logger.LogWarning("Saveloader not found - copying without triggering save");
            }

            try
            {
                string dest = GetSlotPath(slot);

                if (Directory.Exists(dest))
                    Directory.Delete(dest, recursive: true);
                Directory.CreateDirectory(dest);

                WriteSentinel("save_to_slot", GameSavePath, dest);
                CopyDirectory(GameSavePath, dest);
                ClearSentinel();

                ShowStatus($"Saved to Slot {slot}!");
                Logger.LogInfo($"Saved to Slot {slot}");
            }
            catch (Exception ex)
            {
                ShowStatus($"Save FAILED: {ex.Message}");
                Logger.LogError(ex);
            }

            isBusy = false;
        }

        private IEnumerator DoLoadFromSlot(int slot)
        {
            isBusy = true;
            ShowStatus("Returning to menu...");

            if (pauseMenu != null && changeSceneMethod != null)
            {
                changeSceneMethod.Invoke(pauseMenu, null);
                Logger.LogInfo("Called changeScene()");

                yield return StartCoroutine(WaitForSaveComplete("Waiting for autosave..."));
                yield return StartCoroutine(WaitForMainMenu());
            }
            else
            {
                Logger.LogWarning("pauseMenuScript not found - skipping scene change, game may not have saved!");
            }

            // Backup current save
            ShowStatus("Backing up current save...");
            try
            {
                string backupPath = Path.Combine(SlotBasePath, "Backup_BeforeLoad");
                if (Directory.Exists(backupPath))
                    Directory.Delete(backupPath, recursive: true);
                CopyDirectory(GameSavePath, backupPath);
                Logger.LogInfo("Backup complete");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Backup failed (continuing anyway): {ex.Message}");
            }

            // Copy slot into game save
            ShowStatus($"Loading Slot {slot}...");
            try
            {
                WriteSentinel("load_from_slot", GetSlotPath(slot), GameSavePath);
                ClearDirectory(GameSavePath);
                CopyDirectory(GetSlotPath(slot), GameSavePath);
                ClearSentinel();

                ShowStatus($"Slot {slot} loaded! Press Play to start.");
                Logger.LogInfo($"Loaded Slot {slot}");
            }
            catch (Exception ex)
            {
                ShowStatus($"Load FAILED: {ex.Message}");
                Logger.LogError(ex);
            }

            isBusy = false;
        }

        // -------------------------
        // Unity loop
        // -------------------------

        private void Update()
        {
            if (statusTimer > 0f)
                statusTimer -= Time.unscaledDeltaTime;

            if (isBusy) return;

            TryFindComponents();

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f3Key.wasPressedThisFrame)
            {
                currentSlot = (currentSlot - 1 + MAX_SLOTS) % MAX_SLOTS;
                ShowStatus($"Slot {currentSlot}{(SlotHasData(currentSlot) ? "" : " [empty]")}");
            }

            if (keyboard.f4Key.wasPressedThisFrame)
            {
                currentSlot = (currentSlot + 1) % MAX_SLOTS;
                ShowStatus($"Slot {currentSlot}{(SlotHasData(currentSlot) ? "" : " [empty]")}");
            }

            if (keyboard.f1Key.wasPressedThisFrame)
                StartCoroutine(DoSaveToSlot(currentSlot));

            if (keyboard.f2Key.wasPressedThisFrame)
            {
                if (!SlotHasData(currentSlot))
                {
                    ShowStatus($"Slot {currentSlot} is empty!");
                    return;
                }
                StartCoroutine(DoLoadFromSlot(currentSlot));
            }
        }

        private void OnGUI()
        {
            string slotText =
                $"Slot: {currentSlot}{(SlotHasData(currentSlot) ? "" : " [empty]")}\n" +
                "F3/F4: Change  F1: Save  F2: Load";
            GUI.Label(new Rect(0, 0, Screen.width - 10, 60), slotText, slotStyle);

            if (statusTimer > 0f)
                GUI.Label(new Rect(0, 70, Screen.width, 30), statusMessage, statusStyle);

            if (isBusy)
                GUI.Label(new Rect(0, 100, Screen.width, 30), "Working...", statusStyle);
        }
    }
}