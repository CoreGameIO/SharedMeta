using UnityEngine;
using UnityEngine.InputSystem;
using Expedition.Shared;

/// <summary>
/// Keyboard input controller for the expedition game.
/// WASD/Arrows = move, R+direction = remove obstacle, B = buy energy, U = update energy.
/// Works alongside the D-Pad UI buttons from ExpeditionUIGenerator.
/// Uses new Input System package.
/// </summary>
public class ExpeditionInputController : MonoBehaviour
{
    [SerializeField] private ExpeditionGameManager gameManager;
    [SerializeField] private ExpeditionUIGenerator ui;

    private bool _awaitingRemoveDirection;
    private bool _processing;
    private Keyboard _kb;

    void OnEnable()
    {
        _kb = Keyboard.current;
    }

    void Update()
    {
        if (_kb == null) _kb = Keyboard.current;
        if (_kb == null || !gameManager.IsConnected || _processing) return;

        // R key toggles remove obstacle mode
        if (_kb.rKey.wasPressedThisFrame)
        {
            _awaitingRemoveDirection = true;
            ui.SetStatus("Remove obstacle: press direction...");
            return;
        }

        // Direction keys
        int dx = 0, dy = 0;
        if (_kb.wKey.wasPressedThisFrame || _kb.upArrowKey.wasPressedThisFrame) dy = -1;
        else if (_kb.sKey.wasPressedThisFrame || _kb.downArrowKey.wasPressedThisFrame) dy = 1;
        else if (_kb.aKey.wasPressedThisFrame || _kb.leftArrowKey.wasPressedThisFrame) dx = -1;
        else if (_kb.dKey.wasPressedThisFrame || _kb.rightArrowKey.wasPressedThisFrame) dx = 1;

        if (dx != 0 || dy != 0)
        {
            if (_awaitingRemoveDirection)
            {
                _awaitingRemoveDirection = false;
                DoRemoveObstacle(dx, dy);
            }
            else
            {
                DoMove(dx, dy);
            }
            return;
        }

        // B = Buy energy
        if (_kb.bKey.wasPressedThisFrame)
        {
            _processing = true;
            DoBuyEnergy();
            return;
        }

        // U = Update (regen) energy
        if (_kb.uKey.wasPressedThisFrame)
        {
            _processing = true;
            DoUpdateEnergy();
            return;
        }

        // N = New expedition (when complete) — shows generation mode choice
        if (_kb.nKey.wasPressedThisFrame)
        {
            var state = gameManager.ExpeditionState;
            if (state != null && state.IsComplete)
            {
                ui.ShowGenerationModeChoice();
            }
        }

        // Escape = Cancel remove mode
        if (_kb.escapeKey.wasPressedThisFrame && _awaitingRemoveDirection)
        {
            _awaitingRemoveDirection = false;
            ui.SetStatus("");
        }
    }

    private async void DoMove(int dx, int dy)
    {
        _processing = true;
        try
        {
            var result = await gameManager.Move(dx, dy);
            ui.SetStatus(ExpeditionUIGenerator.MoveResultToString(result));
        }
        finally
        {
            _processing = false;
        }
    }

    private async void DoRemoveObstacle(int dx, int dy)
    {
        _processing = true;
        try
        {
            bool removed = await gameManager.RemoveObstacle(dx, dy);
            ui.SetStatus(removed ? "Obstacle removed! (-5 energy)" : "Cannot remove obstacle.");
        }
        finally
        {
            _processing = false;
        }
    }

    private async void DoBuyEnergy()
    {
        try
        {
            await gameManager.BuyEnergy();
        }
        finally
        {
            _processing = false;
        }
    }

    private async void DoUpdateEnergy()
    {
        try
        {
            await gameManager.UpdateEnergy();
        }
        finally
        {
            _processing = false;
        }
    }

}
