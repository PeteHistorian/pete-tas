using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HitColor : MonoBehaviour
{
    [SerializeField] private float hitColorTime;
    private SpriteRenderer spriteRenderer;

    // Times when the sprite turns back to white.
    // These replace the old WaitForSeconds coroutine (same timing) so the TAS tool can save and rewind them.
    private List<double> normalColorTimes = new List<double>();

    void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    public void ChangeToHitColor()
    {
        spriteRenderer.color = Color.red;
        normalColorTimes.Add(TasHooks.GameTime + hitColorTime);
    }

    private void LateUpdate()
    {
        for(int i = normalColorTimes.Count - 1; i >= 0; i--)
        {
            if(TasHooks.GameTime >= normalColorTimes[i])
            {
                normalColorTimes.RemoveAt(i);
                spriteRenderer.color =Color.white;
            }
        }
    }

    private void OnDisable()
    {
        // Coroutines stop when the object is switched off, so pending timers do too
        if(TasHooks.MuteCallbacks) return;
        normalColorTimes.Clear();
    }
}
