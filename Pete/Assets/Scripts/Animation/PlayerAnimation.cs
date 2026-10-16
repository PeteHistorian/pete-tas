using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerAnimation : MonoBehaviour
{
    public enum playerAnimationState
    {
        IDLE,
        RUNNING,
        JUMPING,
        FALLING,
        ATTACKUP,
        ATTACKDOWN,
        ATTACK1,
        ATTACK2,
        UP,
        DOWN
    }

    private static playerAnimationState animationState;
    private Animator animator;
    private Rigidbody2D body;
    private bool isAttacking;
    private int currentAttack;

    private void Awake() 
    {
        body = GetComponent<Rigidbody2D>();
        animator = GetComponent<Animator>();
    }

    public void playerIsAttacking(playerAnimationState PlayerAnimationState)
    {
        if(PlayerAnimationState == playerAnimationState.ATTACK1)
        {
            if(currentAttack % 2 == 1)
                PlayerAnimationState = playerAnimationState.ATTACK1;
            else
                PlayerAnimationState = playerAnimationState.ATTACK2;

            currentAttack++;
        }
        isAttacking = true; 
        animationState = PlayerAnimationState;
    }

    public void playerStoppedAttacking()
    {
        isAttacking = false;
    }

    private void changePlayerAnimationState(playerAnimationState PlayerAnimationState, bool state)
    {
        animator.SetBool("RUNNING", false);
        animator.SetBool("IDLE", false);
        animator.SetBool("JUMPING", false);
        animator.SetBool("FALLING", false);
        animator.SetBool("UP", false);
        animator.SetBool("DOWN", false);
        animator.SetBool("ATTACKUP", false);
        animator.SetBool("ATTACKDOWN", false);
        animator.SetBool("ATTACK1", false);
        animator.SetBool("ATTACK2", false);

        animator.SetBool(PlayerAnimationState.ToString(), state);
    }

    private void Update() 
    {
        if(!isAttacking)
        {
            if(body.velocity.y > 0)
                animationState = playerAnimationState.JUMPING;
            else if(body.velocity.y < 0)
                animationState = playerAnimationState.FALLING;
            else if(body.velocity.x > 0 || body.velocity.x < 0)
                animationState = playerAnimationState.RUNNING;
            else if(PeteInput.GetAxisRaw("Vertical") > 0.5)
                animationState = playerAnimationState.UP;
            else if(PeteInput.GetAxisRaw("Vertical") < -0.5)
                animationState = playerAnimationState.DOWN;
            else
                animationState = playerAnimationState.IDLE;
        }

        changePlayerAnimationState(animationState, true);
    }
}
