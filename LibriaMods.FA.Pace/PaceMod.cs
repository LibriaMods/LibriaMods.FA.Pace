using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LibriaMods.Unity.Shared;
using UnityEngine;

namespace LibriaMods.FA.Pace
{
    /// <summary>
    /// Forever Ago: faster walking/running (+/-) and skipping spoken lines.
    /// The player moves with root motion, so the player's animator is sped up only while moving.
    /// Game types are reached by reflection: the project builds without the game installed.
    /// Keyboard is read with GetAsyncKeyState (the game uses Rewired, Unity's input may be off).
    /// </summary>
    [LibriaMod("Forever Ago Pace", "0.1.0", Id = "LibriaMods.FA.Pace")]
    [LibriaGame("Third Shift", "ForeverAgo")]
    public class PaceMod : LibriaMod
    {
        const float Step = 0.1f;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        IConfigEntry<float> multiplier;
        IConfigEntry<bool> speedOn;
        IConfigEntry<KeyCode> keyUp, keyDown, keyToggle, keySkip;
        IConfigEntry<string> padSkip, padUp, padDown;

        Type controllerType;           // AlfredCharacterController (player movement)
        PropertyInfo controllerAnimator;
        Type dialogueManager;          // PixelCrushers.DialogueSystem.DialogueManager (static)
        PropertyInfo dmActive, dmView;
        MethodInfo viewContinueAll;

        Component player;
        Animator anim;
        float lastSet = 1f, refind;
        readonly bool[] down = new bool[256];

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);
            multiplier = context.Config("Multiplier", 1.6f, "Walk/run speed multiplier while moving (1 = normal)");
            speedOn    = context.Config("SpeedEnabled", true, "Extra speed on/off");
            keyUp      = context.Config("SpeedUpKey", KeyCode.KeypadPlus, "Increase the multiplier by 0.1 (main-row = / + also work)");
            keyDown    = context.Config("SpeedDownKey", KeyCode.KeypadMinus, "Decrease the multiplier by 0.1 (main-row - also works)");
            keyToggle  = context.Config("ToggleKey", KeyCode.F8, "Extra speed on/off");
            keySkip    = context.Config("SkipLineKey", KeyCode.Tab, "Skip the current spoken line");
            padSkip    = context.Config("PadSkipLine", "Circle, B", "Gamepad button(s) that skip a line, by Rewired name (comma separated). Empty = off");
            padUp      = context.Config("PadSpeedUp", "", "Gamepad button(s) for +0.1, e.g. \"R3\" or \"Right Stick Button\". Empty = off");
            padDown    = context.Config("PadSpeedDown", "", "Gamepad button(s) for -0.1. Empty = off");
            context.SaveConfig();

            var game = Find("Assembly-CSharp");
            var first = Find("Assembly-CSharp-firstpass");
            controllerType = game?.GetType("AlfredCharacterController");
            controllerAnimator = controllerType?.GetProperty("animator", All);
            dialogueManager = first?.GetType("PixelCrushers.DialogueSystem.DialogueManager");
            dmActive = dialogueManager?.GetProperty("isConversationActive", All);
            dmView = dialogueManager?.GetProperty("conversationView", All);
            viewContinueAll = first?.GetType("PixelCrushers.DialogueSystem.ConversationView")?.GetMethod("OnConversationContinueAll", All, null, Type.EmptyTypes, null);

            context.Log($"player controller={controllerType != null}, animator={controllerAnimator != null}, dialogues={viewContinueAll != null}");
        }

        public override void OnUpdate()
        {
            if (Pressed(Vk(keyUp.Value)) || Pressed(0xBB)) ChangeSpeed(+Step);      // 0xBB = '=' / '+'
            if (Pressed(Vk(keyDown.Value)) || Pressed(0xBD)) ChangeSpeed(-Step);    // 0xBD = '-'
            if (Pressed(Vk(keyToggle.Value)))
            {
                speedOn.Value = !speedOn.Value;
                Context.SaveConfig();
                Toast.Show(speedOn.Value ? $"Pace x{multiplier.Value:0.0}" : "Pace off");
            }
            if (Pressed(Vk(keySkip.Value)) || Pad(padSkip.Value)) SkipLine();
            if (Pad(padUp.Value)) ChangeSpeed(+Step);
            if (Pad(padDown.Value)) ChangeSpeed(-Step);
            ApplySpeed();
        }

        public override void OnUnload()
        {
            if (anim != null && Mathf.Approximately(anim.speed, lastSet)) anim.speed = 1f;
        }

        void ChangeSpeed(float d)
        {
            multiplier.Value = Mathf.Clamp((float)Math.Round(multiplier.Value + d, 1), 0.5f, 4f);
            speedOn.Value = true;
            Context.SaveConfig();
            Toast.Show($"Pace x{multiplier.Value:0.0}");
        }

        void ApplySpeed()
        {
            refind -= Time.unscaledDeltaTime;
            if ((player == null || anim == null) && refind <= 0f && controllerType != null)
            {
                refind = 1f;
                player = UnityEngine.Object.FindObjectOfType(controllerType) as Component;
                anim = player != null ? (controllerAnimator?.GetValue(player, null) as Animator ?? player.GetComponentInChildren<Animator>()) : null;
                lastSet = anim != null ? anim.speed : 1f;
                if (anim != null) Context.Log("Player found: " + player.name);
            }
            if (anim == null || !player.gameObject.activeInHierarchy) return;

            bool moving = false;
            try { moving = anim.GetBool("IsMoving"); } catch { }
            float target = speedOn.Value && moving ? multiplier.Value : 1f;

            // Leave the animator alone if something else (a cutscene) changed its speed.
            if (Mathf.Approximately(anim.speed, lastSet))
            {
                if (!Mathf.Approximately(anim.speed, target)) { anim.speed = target; lastSet = target; }
            }
            else if (Mathf.Approximately(target, 1f)) lastSet = anim.speed;
        }

        void SkipLine()
        {
            try
            {
                // Cutscenes are Timelines with a "Continue Conversation" clip at every line change:
                // jump the playing timeline to the next one, so animation, camera and voice move on too.
                if (JumpToNextLine()) return;

                // In a cutscene the timeline drives the conversation. Continuing the conversation on its own
                // would put it one line ahead of the timeline, and every later line would be cut short.
                if (InCutscene()) { Context.Log("Cutscene: no next line to jump to, not skipping"); return; }

                // Gameplay conversations: only stop the voice. The line's sequence waits for the voice, so it then
                // ends by itself and still runs its own commands (end the examine animation, give control back...).
                // Continuing the conversation directly would skip those and leave the player stuck.
                int stopped = StopVoices();
                Context.Log(stopped > 0 ? "Skipped line (voice stopped)" : "No voice playing, nothing to skip");
            }
            catch (Exception e) { Context.Warning("Skip failed: " + (e.InnerException ?? e).Message); }
        }

        // Voices are played by RogoDigital LipSync through FMOD; Stop(true) stops the audio too.
        Type lipSyncType; PropertyInfo lsPlaying; MethodInfo lsStop; bool lsInit;
        int StopVoices()
        {
            int n = 0;
            try
            {
                if (!lsInit)
                {
                    lsInit = true;
                    lipSyncType = Find("Assembly-CSharp")?.GetType("RogoDigital.Lipsync.LipSync");
                    lsPlaying = lipSyncType?.GetProperty("IsPlaying", All);
                    lsStop = lipSyncType?.GetMethod("Stop", All, null, new[] { typeof(bool) }, null);
                }
                if (lipSyncType == null || lsStop == null) return 0;
                foreach (var ls in UnityEngine.Object.FindObjectsOfType(lipSyncType))
                    if ((bool)lsPlaying.GetValue(ls, null)) { lsStop.Invoke(ls, new object[] { true }); n++; }
            }
            catch (Exception e) { Context.Warning("StopVoices: " + (e.InnerException ?? e).Message); }
            return n;
        }

        FieldInfo gsInstance, gsCutscene;
        bool InCutscene()
        {
            try
            {
                if (gsInstance == null)
                {
                    var gs = Find("Assembly-CSharp")?.GetType("GameState");
                    gsInstance = gs?.GetField("Instance", All);
                    gsCutscene = gs?.GetField("_isCutsceneActive", All);
                }
                var inst = gsInstance?.GetValue(null);
                return inst != null && (bool)gsCutscene.GetValue(inst);
            }
            catch { return false; }
        }

        bool JumpToNextLine()
        {
            foreach (var d in UnityEngine.Object.FindObjectsOfType<UnityEngine.Playables.PlayableDirector>())
            {
                if (d == null || !d.isActiveAndEnabled || d.state != UnityEngine.Playables.PlayState.Playing || d.playableAsset == null) continue;
                double now = d.time, next = double.MaxValue;
                var timeline = d.playableAsset;
                var tracks = timeline.GetType().GetMethod("GetOutputTracks", All, null, Type.EmptyTypes, null)?.Invoke(timeline, null) as System.Collections.IEnumerable;
                if (tracks == null) continue;
                foreach (var t in tracks)
                {
                    string tn = t.GetType().Name;
                    if (tn != "ContinueConversationTrack" && tn != "ConversationTrack") continue;
                    var clips = t.GetType().GetMethod("GetClips", All, null, Type.EmptyTypes, null)?.Invoke(t, null) as System.Collections.IEnumerable;
                    if (clips == null) continue;
                    foreach (var c in clips)
                    {
                        double start = (double)c.GetType().GetProperty("start", All).GetValue(c, null);
                        if (start > now + 0.05 && start < next) next = start;
                    }
                }
                if (next == double.MaxValue) continue;
                StopVoices();           // the voice of the skipped line doesn't stop by itself when the timeline jumps
                d.time = next + 0.02;   // just inside the clip, so it fires
                d.Evaluate();
                Context.Log($"Skipped line: {d.name} {now:0.00}s -> {next:0.00}s");
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ input

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        bool Pressed(int vk)
        {
            if (vk <= 0 || vk > 255) return false;
            bool isDown = (GetAsyncKeyState(vk) & 0x8000) != 0;
            bool was = down[vk];
            down[vk] = isDown;
            if (!isDown || was) return false;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            return pid == GetCurrentProcessId();   // only when the game window has focus
        }

        // ------------------------------------------------------------------ gamepad (Rewired, via reflection)

        PropertyInfo rwReady, rwControllers;
        bool rwInit, loggedPads;

        /// <summary>True if any connected joystick's button with one of these names was just pressed.</summary>
        bool Pad(string names)
        {
            if (string.IsNullOrEmpty(names)) return false;
            try
            {
                if (!rwInit)
                {
                    rwInit = true;
                    var reInput = Find("Rewired_Core")?.GetType("Rewired.ReInput");
                    rwReady = reInput?.GetProperty("isReady", All);
                    rwControllers = reInput?.GetProperty("controllers", All);
                }
                if (rwReady == null || !(bool)rwReady.GetValue(null, null)) return false;
                var helper = rwControllers.GetValue(null, null);
                var joysticks = helper.GetType().GetProperty("Joysticks", All).GetValue(helper, null) as System.Collections.IEnumerable;
                if (joysticks == null) return false;
                var wanted = names.Split(',');
                foreach (var j in joysticks)
                {
                    var buttons = j.GetType().GetProperty("Buttons", All).GetValue(j, null) as System.Collections.IEnumerable;
                    if (buttons == null) continue;
                    foreach (var b in buttons)
                    {
                        string n = ButtonName(b);
                        if (!loggedPads) Context.Log($"gamepad button: {n}");
                        if (!(bool)b.GetType().GetProperty("justPressed", All).GetValue(b, null)) continue;
                        foreach (var w in wanted)
                            if (string.Equals(w.Trim(), n, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    loggedPads = true;
                }
            }
            catch (Exception e) { if (!loggedPads) { loggedPads = true; Context.Warning("gamepad: " + e.Message); } }
            return false;
        }

        static string ButtonName(object button)
        {
            var id = button.GetType().GetProperty("elementIdentifier", All)?.GetValue(button, null);
            return id?.GetType().GetProperty("name", All)?.GetValue(id, null) as string ?? "";
        }

        /// <summary>Unity KeyCode → Windows virtual-key code (keyboard keys only).</summary>
        static int Vk(KeyCode k)
        {
            if (k >= KeyCode.A && k <= KeyCode.Z) return 'A' + (k - KeyCode.A);
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return '0' + (k - KeyCode.Alpha0);
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return 0x60 + (k - KeyCode.Keypad0);
            if (k >= KeyCode.F1 && k <= KeyCode.F15) return 0x70 + (k - KeyCode.F1);
            switch (k)
            {
                case KeyCode.Backspace: return 0x08; case KeyCode.Tab: return 0x09; case KeyCode.Return: return 0x0D;
                case KeyCode.Space: return 0x20; case KeyCode.Escape: return 0x1B; case KeyCode.Pause: return 0x13;
                case KeyCode.PageUp: return 0x21; case KeyCode.PageDown: return 0x22; case KeyCode.End: return 0x23; case KeyCode.Home: return 0x24;
                case KeyCode.LeftArrow: return 0x25; case KeyCode.UpArrow: return 0x26; case KeyCode.RightArrow: return 0x27; case KeyCode.DownArrow: return 0x28;
                case KeyCode.Insert: return 0x2D; case KeyCode.Delete: return 0x2E;
                case KeyCode.KeypadMultiply: return 0x6A; case KeyCode.KeypadPlus: return 0x6B; case KeyCode.KeypadMinus: return 0x6D;
                case KeyCode.KeypadPeriod: return 0x6E; case KeyCode.KeypadDivide: return 0x6F; case KeyCode.KeypadEnter: return 0x0D;
                case KeyCode.LeftShift: return 0xA0; case KeyCode.RightShift: return 0xA1; case KeyCode.LeftControl: return 0xA2; case KeyCode.RightControl: return 0xA3;
                case KeyCode.LeftAlt: return 0xA4; case KeyCode.RightAlt: return 0xA5;
                default: return 0;
            }
        }

        static Assembly Find(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            return null;
        }
    }
}
