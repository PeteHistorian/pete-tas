using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class KnockbackTrigger : MonoBehaviour
{
    [SerializeField] private float kbTime = 0.2f;
    [SerializeField] private float kbVelocity;
    private Vector3 kbDir;
    private Rigidbody2D body;

    // Times when the knockback velocity gets cleared.
    // These replace the old WaitForSeconds coroutine (same timing) so the TAS tool can save and rewind them.
    private List<double> clearVelocityTimes = new List<double>();

    public void Knockback(Transform knocker)
    {
        kbDir = transform.position - knocker.transform.position;
        GetComponent<Rigidbody2D>().velocity = kbDir.normalized * kbVelocity;
        clearVelocityTimes.Add(TasHooks.GameTime + kbTime);
    }

    private void LateUpdate()
    {
        for(int i = clearVelocityTimes.Count - 1; i >= 0; i--)
        {
            if(TasHooks.GameTime >= clearVelocityTimes[i])
            {
                clearVelocityTimes.RemoveAt(i);
                GetComponent<Rigidbody2D>().velocity = new Vector3(0,0,0);
            }
        }
    }

    private void OnDisable()
    {
        // Coroutines stop when the object is switched off, so pending timers do too
        if(TasHooks.MuteCallbacks) return;
        clearVelocityTimes.Clear();
    }

    public void ReverseKnockback(Transform knocker)
    {
        //kbDir = knocker.transform.position - GameObject.FindGameObjectWithTag("Player").transform.position;
        //GameObject.FindGameObjectWithTag("Player").GetComponent<Rigidbody2D>().velocity = kbDir.normalized * kbVelocity;
        body = GameObject.FindGameObjectWithTag("Player").GetComponent<Rigidbody2D>();
        body.velocity = new Vector3(body.velocity.x, kbVelocity, 0);
    }
}
