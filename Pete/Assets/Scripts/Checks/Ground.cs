using UnityEngine;

public class Ground : MonoBehaviour
{
    private bool onGround;
    private float friction;
    private PhysicsMaterial2D material;

    private void OnCollisionEnter2D(Collision2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        EvaluateCollision(other);
        RetrieveFriction(other);
    }

    private void OnCollisionStay2D(Collision2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        EvaluateCollision(other);
        RetrieveFriction(other); 
    }

    private void OnCollisionExit2D(Collision2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        onGround = false;
        friction = 0;    
    }

    private void EvaluateCollision(Collision2D collision)
    {
        for(int i = 0; i < collision.contactCount; i++)
        {
            Vector2 normal = collision.GetContact(i).normal;
            onGround |= normal.y >= 0.9f;
        }
    }

    private void RetrieveFriction(Collision2D collision)
    {
        material = collision.rigidbody.sharedMaterial;

        friction = 0;

        if(material != null)
        {
            friction = material.friction;
        }
    }

    public bool GetOnGround()
    {
        return onGround;
    }

    public float GetFriction()
    {
        return friction;
    }
}
