using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections;
using System.IO;
using System.Numerics;
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

        // Panel dimensions
        private const float PANEL_WIDTH = 220f;
        private const float PANEL_MARGIN = 10f;
        private const float PANEL_Y = 100f;

        // GUI Styles
        private GUIStyle panelBgStyle;
        private GUIStyle titleStyle;
        private GUIStyle slotLabelStyle;
        private GUIStyle buttonStyle;
        private GUIStyle buttonDisabledStyle;
        private GUIStyle statusStyle;
        private GUIStyle hintStyle;
        private bool stylesInitialized = false;

        // Texture cache
        private Texture2D _panelBgTex;
        private Texture2D _buttonNormalTex;
        private Texture2D _buttonHoverTex;
        private Texture2D _buttonActiveTex;
        private Texture2D _buttonDisabledTex;
        private Texture2D _slotFilledTex;

        // Cached components
        private MonoBehaviour pauseMenu;
        private MethodInfo changeSceneMethod;
        private MonoBehaviour saveloaderComp;
        private MethodInfo manualSaveMethod;
        private FieldInfo isPausingGameField;
        private FieldInfo settingsMenuOpenField;
        private FieldInfo menuOpenField;

        // confirmation menu
        private bool showConfirm = false;
        private string confirmMessage = "";
        private Action confirmYesAction = null;
        private Action confirmNoAction = null;

        private void Awake()
        {
            Logger = base.Logger;

            Directory.CreateDirectory(SlotBasePath);
            for (int i = 0; i < MAX_SLOTS; i++)
                Directory.CreateDirectory(GetSlotPath(i));

            CheckAndRecoverFromCrash();

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
        // GUI Style initialization (must be called from OnGUI)
        // -------------------------

        private Texture2D MakeTex(int w, int h, Color col)
        {
            var tex = new Texture2D(w, h);
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = col;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private void InitStyles()
        {
            if (stylesInitialized)
            {
                return;
            }
            stylesInitialized = true;

            _panelBgTex = MakeTex(1, 1, new Color(0.05f, 0.07f, 0.12f, 0.93f));
            _buttonNormalTex = MakeTex(1, 1, new Color(0.12f, 0.22f, 0.38f, 1f));
            _buttonHoverTex = MakeTex(1, 1, new Color(0.20f, 0.40f, 0.70f, 1f));
            _buttonActiveTex = MakeTex(1, 1, new Color(0.08f, 0.55f, 0.85f, 1f));
            _buttonDisabledTex = MakeTex(1, 1, new Color(0.15f, 0.15f, 0.18f, 1f));
            _slotFilledTex = MakeTex(1, 1, new Color(0.10f, 0.35f, 0.20f, 1f));

            panelBgStyle = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(10, 10, 10, 10),
                alignment = TextAnchor.UpperLeft
            };
            panelBgStyle.normal.background = _panelBgTex;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            titleStyle.normal.textColor = new Color(0.45f, 0.85f, 1f);

            slotLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            slotLabelStyle.normal.textColor = Color.white;

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                border = new RectOffset(0, 0, 0, 0)
            };
            buttonStyle.normal.background = _buttonNormalTex;
            buttonStyle.hover.background = _buttonHoverTex;
            buttonStyle.active.background = _buttonActiveTex;
            buttonStyle.normal.textColor = Color.white;
            buttonStyle.hover.textColor = Color.white;
            buttonStyle.active.textColor = Color.white;

            buttonDisabledStyle = new GUIStyle(buttonStyle);
            buttonDisabledStyle.normal.background = _buttonDisabledTex;
            buttonDisabledStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);

            statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter
            };
            statusStyle.normal.textColor = new Color(1f, 0.85f, 0.3f);

            hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter
            };
            hintStyle.normal.textColor = new Color(0.6f, 0.65f, 0.75f);
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
            {
                File.Delete(WorkingFilePath);
            }
        }

        private bool ShouldSkipFile(string file)
        {
            return Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase);
        }

        private void ClearDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

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
            }
        }

        private void CheckAndRecoverFromCrash()
        {
            if (!File.Exists(WorkingFilePath))
            {
                return;
            }

            string[] lines = File.ReadAllLines(WorkingFilePath);
            string operation = "", src = "", dest = "";

            foreach (var line in lines)
            {
                if (line.StartsWith("operation="))
                {
                    operation = line.Substring("operation=".Length);
                } 
                else if (line.StartsWith("src="))
                {
                    src = line.Substring("src=".Length);
                } 
                else if (line.StartsWith("dest=")) 
                {
                    dest = line.Substring("dest=".Length);
                }
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
                {
                    continue;
                }
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
                    {
                        if (t.Name == name)
                        {
                            return t;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private bool IsPlayerInGame()
        {
            if (!pauseMenu)
            {
                return false;
            }
            if (isPausingGameField == null)
            {
                return false; 
            }
            return (bool)isPausingGameField.GetValue(pauseMenu);
        }

        private bool IsPauseMenuActive()
        {
            if (!IsPlayerInGame())
            {
                return false;
            }
            if (!pauseMenu)
            {
                return false;
            }
            if (menuOpenField == null || settingsMenuOpenField == null)
            {
                return false;
            }
            bool inSettings = (bool)settingsMenuOpenField.GetValue(pauseMenu);
            bool isPaused = (bool)menuOpenField.GetValue(pauseMenu);
            return !inSettings && isPaused;
        }

        private bool IsCurrentlyOnMainMenu()
        {
            
            if (IsPlayerInGame())
            {
                return false;
            }

            if (!pauseMenu)
            {
                return true;
            }

            // Also make sure the settings menu isn't open on top
            if (settingsMenuOpenField != null)
            {
                bool inSettings = (bool)settingsMenuOpenField.GetValue(pauseMenu);
                if (inSettings)
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsPanelVisible()
        {
            return IsCurrentlyOnMainMenu() || IsPauseMenuActive();
        }

        private void TryFindComponents()
        {
            if (!pauseMenu)
            {
                var obj = FindObjectOfType(GetTypeByName("pauseMenuScript")) as MonoBehaviour;
                if (obj)
                {
                    pauseMenu = obj;
                    changeSceneMethod = pauseMenu.GetType().GetMethod("changeScene",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    isPausingGameField = pauseMenu.GetType().GetField("isPausingGame",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    settingsMenuOpenField = pauseMenu.GetType().GetField("settingsMenuOpen",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    menuOpenField = pauseMenu.GetType().GetField("menuOpen",
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
                    Logger.LogInfo("Found Saveloader");
                }
            }
        }

        // -------------------------
        // Wait helpers
        // -------------------------

        private IEnumerator WaitForSaveComplete(string statusPrefix)
        {
            // IsSaving appears to always be true (likely a feature flag, not save state).
            // Use a fixed delay instead to give the game time to flush its save to disk.
            const float saveDelay = 1.5f;
            ShowStatus(statusPrefix);
            Logger.LogInfo($"[Save] Waiting {saveDelay}s for game to write save data...");
            yield return new WaitForSecondsRealtime(saveDelay);
            Logger.LogInfo("[Save] Delay complete, assuming save finished.");
        }

        private IEnumerator WaitForMainMenu()
        {
            float timeout = 15f;
            while (timeout > 0f)
            {
                timeout -= Time.unscaledDeltaTime;

                if (IsCurrentlyOnMainMenu())
                {
                    break;
                }

                ShowStatus($"Waiting for menu... ({timeout:F0}s)");
                yield return null;
            }

            yield return new WaitForSecondsRealtime(0.5f);
        }

        // -------------------------
        // Core operations
        // -------------------------
        private void ShowConfirmation(string message, Action onYes, Action onNo = null)
        {
            confirmMessage = message;
            confirmYesAction = onYes;
            confirmNoAction = onNo;
            showConfirm = true;
        }

        private IEnumerator DoSaveToSlot(int slot)
        {
            // Show confirmation first
            ShowConfirmation(
                $"Overwrite Slot {slot}?",
                onYes: () => StartCoroutine(DoSaveToSlotConfirmed(slot, true)),
                onNo: () => StartCoroutine(DoSaveToSlotConfirmed(slot, false))
            );

            yield break; // stop original coroutine until user responds
        }

        // This is the actual saving logic called after user clicks YES
        private IEnumerator DoSaveToSlotConfirmed(int slot, bool confirmed)
        {
            if (!confirmed)
            {
                ShowStatus($"Save canceled");
                yield break;
            }

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

                ClearDirectory(dest);
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
            if (!SlotHasData(slot))
            {
                ShowStatus($"Slot {slot} is empty!");
                yield break;
            }

            ShowConfirmation(
                $"Load Slot {slot}?\nCurrent progress will be overwritten!",
                onYes: () => StartCoroutine(DoLoadFromSlotConfirmed(slot, true)),
                onNo: () => StartCoroutine(DoLoadFromSlotConfirmed(slot, false))
            );

            yield break; // wait for player response
        }

        private IEnumerator DoLoadFromSlotConfirmed(int slot, bool confirmation)
        {
            if (!confirmation)
            {
                ShowStatus("Load canceled");
                yield break;
            }

            isBusy = true;

            if (IsCurrentlyOnMainMenu())
            {
                Logger.LogInfo("Already on main menu - save assumed complete.");
                ShowStatus("Already at menu, loading slot...");
            }
            else
            {
                ShowStatus("Returning to menu...");

                if (pauseMenu && changeSceneMethod != null)
                {
                    changeSceneMethod.Invoke(pauseMenu, null);
                    Logger.LogInfo("Called changeScene()");
                    yield return StartCoroutine(WaitForMainMenu());
                }
                else
                {
                    Logger.LogWarning("pauseMenuScript not found - skipping scene change!");
                }
            }

            ShowStatus("Backing up current save...");
            try
            {
                string backupPath = Path.Combine(SlotBasePath, "Backup_BeforeLoad");
                ClearDirectory(backupPath);
                CopyDirectory(GameSavePath, backupPath);
                Logger.LogInfo("Backup complete");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Backup failed (continuing anyway): {ex.Message}");
            }

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
            {
                statusTimer -= Time.unscaledDeltaTime;
            }

            if (isBusy)
            {
                return;
            }

            TryFindComponents();
        }

        // -------------------------
        // GUI
        // -------------------------

        private void DrawPanel(float panelX)
        {
            float rowH = 30f;
            float btnH = 32f;
            float spacing = 6f;

            float panelHeight =
                10f
                + 22f            // title
                + spacing
                + rowH           // slot display
                + spacing
                + btnH           // prev/next row
                + spacing
                + btnH           // save button
                + spacing
                + btnH           // load button
                + spacing
                + (statusTimer > 0f ? 40f : 0f)
                + (isBusy ? 24f : 0f)
                + 10f;

            Rect panelRect = new Rect(panelX, PANEL_Y, PANEL_WIDTH, panelHeight);
            GUI.Box(panelRect, GUIContent.none, panelBgStyle);

            float y = PANEL_Y + 10f;
            float x = panelX + 8f;
            float w = PANEL_WIDTH - 16f;

            // Title
            GUI.Label(new Rect(x, y, w, 22f), "== SAVE SLOTS ==", titleStyle);
            y += 22f + spacing;

            // Current slot indicator
            bool hasCurrent = SlotHasData(currentSlot);
            string slotLabel = hasCurrent
                ? $"◉  Slot {currentSlot}  [SAVED]"
                : $"○  Slot {currentSlot}  [empty]";

            GUI.DrawTexture(new Rect(x, y, w, rowH), hasCurrent ? _slotFilledTex : _buttonDisabledTex);
            GUI.Label(new Rect(x, y, w, rowH), slotLabel, slotLabelStyle);
            y += rowH + spacing;

            // Prev / Next buttons
            float halfW = (w - 4f) / 2f;
            if (GUI.Button(new Rect(x, y, halfW, btnH), "◀  Prev", buttonStyle))
            {
                currentSlot = (currentSlot - 1 + MAX_SLOTS) % MAX_SLOTS;
                ShowStatus($"Slot {currentSlot}{(SlotHasData(currentSlot) ? "" : " [empty]")}");
            }
            if (GUI.Button(new Rect(x + halfW + 4f, y, halfW, btnH), "Next  ▶", buttonStyle))
            {
                currentSlot = (currentSlot + 1) % MAX_SLOTS;
                ShowStatus($"Slot {currentSlot}{(SlotHasData(currentSlot) ? "" : " [empty]")}");
            }
            y += btnH + spacing;

            // Save button
            if (isBusy)
            {
                GUI.Button(new Rect(x, y, w, btnH), "Busy...", buttonDisabledStyle);
            }
            else if (GUI.Button(new Rect(x, y, w, btnH), "Save to Slot", buttonStyle))
            {
                StartCoroutine(DoSaveToSlot(currentSlot));
            }
            y += btnH + spacing;

            // Load button
            bool canLoad = hasCurrent && !isBusy;
            if (!canLoad)
            {
                GUI.Button(new Rect(x, y, w, btnH), "Load Slot  [empty]", buttonDisabledStyle);
            }
            else if (GUI.Button(new Rect(x, y, w, btnH), "Load Slot", buttonStyle))
            {
                StartCoroutine(DoLoadFromSlot(currentSlot));
            }
            y += btnH + spacing;

            // Status message
            if (statusTimer > 0f)
            {
                GUI.Label(new Rect(x, y, w, 38f), statusMessage, statusStyle);
                y += 38f + spacing;
            }

            // Busy indicator
            if (isBusy)
            {
                GUI.Label(new Rect(x, y, w, 22f), "Working...", statusStyle);
            }
        }

        private void OnGUI()
        {
            InitStyles();

            if (showConfirm)
            {
                float panelW = 300f;
                float panelH = 120f;
                Rect panelRect = new Rect(
                    (Screen.width - panelW) / (IsCurrentlyOnMainMenu() ? 2 : 6),
                    (Screen.height - panelH) / 2,
                    panelW,
                    panelH
                );

                GUI.Box(panelRect, "", panelBgStyle);

                GUI.Label(new Rect(panelRect.x + 10, panelRect.y + 10, panelW - 20, 60), confirmMessage, statusStyle);

                float btnW = 100f;
                float btnH = 32f;
                float spacing = 20f;
                float btnY = panelRect.y + panelH - btnH - 10f;

                // YES button
                if (GUI.Button(new Rect(panelRect.x + spacing, btnY, btnW, btnH), "Yes", buttonStyle))
                {
                    showConfirm = false;
                    confirmYesAction?.Invoke();
                }

                // NO button
                if (GUI.Button(new Rect(panelRect.x + panelW - btnW - spacing, btnY, btnW, btnH), "No", buttonStyle))
                {
                    showConfirm = false;
                    confirmNoAction?.Invoke();
                }

                // block everything else while modal is open
                return;
            }

            if (!IsPanelVisible())
            {
                return;
            }

            if (IsCurrentlyOnMainMenu())
            {
                // Right side on main menu
                DrawPanel(Screen.width - PANEL_WIDTH - PANEL_MARGIN);
            }
            else
            {
                // Left side during pause
                DrawPanel(PANEL_MARGIN);
            }
        }
    }
}