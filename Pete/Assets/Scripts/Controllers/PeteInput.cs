using UnityEngine;

// Every input the game reads goes through here.
// In normal play it just forwards to Unity's Input, so nothing changes.
// When the TAS tool is running it hands out the TAS inputs for the current frame instead.
public static class PeteInput
{
    [System.Flags]
    public enum Buttons
    {
        None = 0,
        Left = 1,
        Right = 2,
        Up = 4,
        Down = 8,
        Jump = 16,
        Attack = 32
    }

    // Buttons held on the current TAS frame and on the frame before it.
    public static Buttons TasHeld;
    public static Buttons TasPrevious;

    // "Pressed this frame" only counts during the frame itself, not while the TAS tool is paused.
    public static bool TasEdgesLive;

    // Used by the online mode's automatic test: buttons pressed by a script instead of the keyboard.
    public static bool Scripted;
    public static Buttons ScriptHeld;
    public static Buttons ScriptPrevious;

    public static float GetAxisRaw(string axis)
    {
        if(Scripted)
        {
            return ScriptAxis(axis);
        }
        if(!TasHooks.Active)
        {
            return Input.GetAxisRaw(axis);
        }
        return TasAxis(axis);
    }

    public static float GetAxis(string axis)
    {
        if(Scripted)
        {
            return ScriptAxis(axis);
        }
        if(!TasHooks.Active)
        {
            return Input.GetAxis(axis);
        }
        return TasAxis(axis);
    }

    public static bool GetButton(string button)
    {
        if(Scripted)
        {
            Buttons s = ButtonFor(button);
            return s != Buttons.None && (ScriptHeld & s) != 0;
        }
        if(!TasHooks.Active)
        {
            return Input.GetButton(button);
        }
        Buttons b = ButtonFor(button);
        return b != Buttons.None && (TasHeld & b) != 0;
    }

    public static bool GetButtonDown(string button)
    {
        if(Scripted)
        {
            Buttons s = ButtonFor(button);
            return s != Buttons.None && (ScriptHeld & s) != 0 && (ScriptPrevious & s) == 0;
        }
        if(!TasHooks.Active)
        {
            return Input.GetButtonDown(button);
        }
        Buttons b = ButtonFor(button);
        return b != Buttons.None && TasEdgesLive && (TasHeld & b) != 0 && (TasPrevious & b) == 0;
    }

    public static bool GetKeyDown(KeyCode key)
    {
        if(!TasHooks.Active)
        {
            return Input.GetKeyDown(key);
        }
        // Keyboard shortcuts like R (reset) are handled by the TAS tool itself.
        return false;
    }

    private static float ScriptAxis(string axis)
    {
        Buttons held = TasHeld;
        TasHeld = ScriptHeld;
        float v = TasAxis(axis);
        TasHeld = held;
        return v;
    }

    private static float TasAxis(string axis)
    {
        if(axis == "Horizontal")
        {
            return ((TasHeld & Buttons.Right) != 0 ? 1f : 0f) - ((TasHeld & Buttons.Left) != 0 ? 1f : 0f);
        }
        if(axis == "Vertical")
        {
            return ((TasHeld & Buttons.Up) != 0 ? 1f : 0f) - ((TasHeld & Buttons.Down) != 0 ? 1f : 0f);
        }
        return 0f;
    }

    private static Buttons ButtonFor(string button)
    {
        if(button == "Jump")
        {
            return Buttons.Jump;
        }
        if(button == "Fire1")
        {
            return Buttons.Attack;
        }
        // Pause, Reset and the rest are never pressed by the TAS.
        return Buttons.None;
    }
}
