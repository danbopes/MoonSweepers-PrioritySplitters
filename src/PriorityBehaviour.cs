using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using MoonCleaners;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace PrioritySplitters;

// Lives on BepInEx's manager object and checks the toggle key each frame.
public sealed class PriorityBehaviour : MonoBehaviour
{
    private const float AimRadius = 2f;
    private KeyControl toggleKey;
    private string toggleKeyName;
    private MoonHudNotice notice;
    private MoonActionPromptCard card;
    private MoonPlayerHud hud;
    private MoonWalker walker;
    private Camera view;
    private bool wasInGame;
    private float nextSync;
    private int failures;
    private float nextFailureLog;

    public PriorityBehaviour(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (HandOffPatch.Faulted || !Plugin.Enabled.Value)
            return;
        try
        {
            var inGame = MoonSession.InGame;
            if (!inGame)
            {
                if (wasInGame)
                {
                    MachineLayout.Reset();
                    PriorityCone.Reset();
                    HidePrompt();
                    card = null;
                    notice = null;
                    hud = null;
                    walker = null;
                    view = null;
                }
                wasInGame = false;
                return;
            }
            wasInGame = true;

            MachineLayout.Poll();
            var world = MachineLayout.World;
            if (world is null)
                return;

            if (TogglePressed())
                Toggle(world);
            UpdatePrompt();

            // Views spawn and despawn as the player moves, so the cones are re-checked a few times a second.
            var now = Time.unscaledTime;
            if (now >= nextSync)
            {
                nextSync = now + 0.5f;
                PriorityCone.Sync();
            }
            failures = 0;
        }
        catch (Exception e)
        {
            Recover(e);
        }
    }

    // An error while the world changes under the mod, such as a scene swap part-way through a frame, must not
    // take the mod down for the rest of the session: the layout is dropped and built again next frame. Only an
    // error that keeps coming back frame after frame disables the mod, the way a routing error does.
    [HideFromIl2Cpp]
    private void Recover(Exception e)
    {
        failures++;
        MachineLayout.Reset();
        PriorityCone.Reset();
        if (failures >= 300)
        {
            HandOffPatch.Fault(e);
            return;
        }
        var now = Time.unscaledTime;
        if (now >= nextFailureLog)
        {
            nextFailureLog = now + 5f;
            Plugin.Logger.LogWarning($"Error {failures} in the priority behaviour, dropping the layout to rebuild it: {e}");
        }
    }

    // The key comes from the Keyboard's own per-key properties, which are plain array reads. Looking a key up by
    // display name queries the OS keyboard layout, and that call hung the game under Wine.
    [HideFromIl2Cpp]
    private bool TogglePressed()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
            return false;
        var wanted = Plugin.ToggleKey.Value;
        if (toggleKey == null || toggleKeyName != wanted)
        {
            toggleKeyName = wanted;
            toggleKey = null;
            var property = typeof(Keyboard).GetProperty(wanted.Trim().ToLowerInvariant() + "Key");
            if (property is not null && property.PropertyType == typeof(KeyControl))
                toggleKey = (KeyControl)property.GetValue(keyboard);
            if (toggleKey == null)
            {
                Plugin.Logger.LogWarning($"Toggle key '{wanted}' is not a key name the Input System knows (try p, o, space, f5, digit1), using P");
                toggleKey = keyboard.pKey;
            }
        }
        return toggleKey.wasPressedThisFrame;
    }

    [HideFromIl2Cpp]
    private void Toggle(MoonWorld world)
    {
        var machine = Aimed(out var miss);
        if (machine is null)
        {
            Plugin.Logger.LogInfo($"Toggle pressed with no splitter or merger in view: {miss}");
            Show("Look at a splitter or merger to change its priority");
            return;
        }
        var next = machine.NextPriority();
        Settings.Set(machine.Id, next);
        machine.ApplyPriority(next);
        PriorityCone.Sync();
        var text = $"{machine.Kind}: {machine.Describe(next)}";
        Plugin.Logger.LogInfo($"#{machine.Id} {text}");
        Show(text);
    }

    // The tracked machine closest to the camera's line of sight, within reach.
    [HideFromIl2Cpp]
    private Machine Aimed(out string miss)
    {
        miss = null;
        var camera = ViewCamera();
        if (camera == null)
        {
            miss = "no camera found";
            return null;
        }
        var origin = camera.transform.position;
        var forward = camera.transform.forward;
        Machine best = null;
        var bestOffset = AimRadius;
        var nearest = "";
        var nearestOffset = float.MaxValue;
        foreach (var machine in MachineLayout.All)
        {
            var toMachine = machine.AimPoint - origin;
            var along = Vector3.Dot(toMachine, forward);
            var offset = (toMachine - forward * along).magnitude;
            if (offset < nearestOffset)
            {
                nearestOffset = offset;
                nearest = $"#{machine.Id} at {machine.AimPoint} is {along:0.0}m ahead and {offset:0.0}m off the line";
            }
            if (along < 0.3f || along > Plugin.ReachMeters.Value)
                continue;
            if (offset < bestOffset)
            {
                bestOffset = offset;
                best = machine;
            }
        }
        if (best is null)
            miss = $"camera '{camera.name}' at {origin} facing {forward}; nearest {nearest}";
        return best;
    }

    // The game does not tag its view camera as MainCamera, so ask the player object for it first.
    [HideFromIl2Cpp]
    private Camera ViewCamera()
    {
        if (view != null && view.isActiveAndEnabled)
            return view;
        view = null;
        var player = Walker();
        var playerCamera = player?.viewCamera;
        if (playerCamera != null && playerCamera.isActiveAndEnabled)
            return view = playerCamera;
        var main = Camera.main;
        if (main != null)
            return view = main;
        var all = Camera.allCameras;
        for (var i = 0; all is not null && i < all.Length; i++)
        {
            if (all[i] != null && all[i].isActiveAndEnabled)
                return view = all[i];
        }
        return null;
    }

    [HideFromIl2Cpp]
    private MoonWalker Walker()
    {
        if (walker != null && walker.isActiveAndEnabled)
            return walker;
        walker = null;
        var walkers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonWalker>(), false);
        for (var i = 0; walkers is not null && i < walkers.Length; i++)
        {
            var candidate = walkers[i].TryCast<MoonWalker>();
            if (candidate != null && candidate.isActiveAndEnabled)
            {
                walker = candidate;
                break;
            }
        }
        return walker;
    }

    // The game's own interaction prompt card while a splitter or merger is in reach. It steps aside for the game's
    // prompts, menus and vehicles, and waits for the world to finish loading: building UI during the load
    // deadlocks the main thread under Wine.
    [HideFromIl2Cpp]
    private void UpdatePrompt()
    {
        if (!Plugin.ShowPrompt.Value || MoonSession.Loading || MoonLoadingScreen.Active || ToolBar() == null)
        {
            HidePrompt();
            return;
        }
        var player = Walker();
        if (player == null || player.UIBlocked || player.Mounted)
        {
            HidePrompt();
            return;
        }
        var machine = Aimed(out _);
        if (machine is null)
        {
            HidePrompt();
            return;
        }
        try
        {
            if (card == null || card.Canvas == null)
                card = MoonActionPromptCard.Create(player.transform, "Priority HUD", 95);
            if (MoonActionPromptCard.OthersShowing(card))
            {
                card.Hide();
                return;
            }
            var key = Plugin.ToggleKey.Value.Trim().ToUpperInvariant();
            card.Show(key, $"Change priority ({machine.Describe(machine.PriorityPort)})", true, 0f, false);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not show the key hint, turning it off: {e}");
            Plugin.ShowPrompt.Value = false;
            HidePrompt();
        }
    }

    [HideFromIl2Cpp]
    private RectTransform ToolBar()
    {
        if (hud == null)
        {
            var huds = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonPlayerHud>(), true);
            for (var i = 0; huds is not null && i < huds.Length && hud == null; i++)
                hud = huds[i].TryCast<MoonPlayerHud>();
        }
        return hud != null ? hud.toolBar : null;
    }

    [HideFromIl2Cpp]
    private void HidePrompt()
    {
        if (card != null && card.Canvas != null)
            card.Hide();
    }

    [HideFromIl2Cpp]
    private void Show(string text)
    {
        if (notice == null)
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonHudNotice>(), true);
            if (found is not null && found.Length > 0)
                notice = found[0].TryCast<MoonHudNotice>();
        }
        if (notice != null)
            notice.Show(text, 2.5f);
    }
}
