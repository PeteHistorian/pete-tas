using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Jump : MonoBehaviour
{
    [SerializeField] private InputController input = null;
    [SerializeField, Range(0f, 10f)] private float jumpHeight = 3f;
    [SerializeField, Range(0, 5)] private int maxAirJumps = 1;
    [SerializeField, Range(0f, 5f)] private float downwardMovementMultiplier = 3f;
    [SerializeField, Range(0f, 5f)] private float upwardMovementMultiplier = 1.7f;
    [SerializeField, Range(0f, 0.3f)] private float coyoteTime = 0.2f;
    [SerializeField, Range(0f, 0.3f)] private float jumpBufferTime = 0.2f;

    private Rigidbody2D body;
    private Ground ground;
    private Vector2 velocity;

    private int jumpPhase;
    private float defaultGravityScale, jumpSpeed, coyoteCounter, jumpBufferCounter;
    private bool desiredJump, onGround, isJumping, hasLanded;

    private TimerManager timerManager;
    private float currentTime;


    void Awake()
    {
        body = GetComponent<Rigidbody2D>();
        ground = GetComponent<Ground>();
        defaultGravityScale = 1f; 
    }

    void Update()
    {
        desiredJump |= input.RetrieveJumpInput();
        timerManager = GameObject.FindGameObjectWithTag("TimerManager").GetComponent<TimerManager>();
    }

    private void LandingSound(bool onGround)
    {
        if(onGround)
        {
            if(!hasLanded && timerManager.timerOn)
            {
                AudioManager.Instance.PlaySound("P_Landing");
                hasLanded = true;
            }
        }
        else
        {
            hasLanded = false;
        }
        
    }

    private void FixedUpdate() 
    {   
        onGround = ground.GetOnGround();
        velocity = body.velocity;

        LandingSound(onGround);

        if(onGround && body.velocity.y == 0)
        {
            //Variablen Resetten
            jumpPhase = 0;
            coyoteCounter = coyoteTime;
            isJumping = false;
        }
        else
        {
            //CoyoteTime 
            coyoteCounter -= Time.deltaTime;
        }

        if(desiredJump)
        {
            desiredJump = false;
            jumpBufferCounter = jumpBufferTime;
        }
        else if(!desiredJump && jumpBufferCounter > 0)
        {
            jumpBufferCounter -= Time.deltaTime;
        }

        if(jumpBufferCounter > 0)
        {
            JumpAction();
        }

        //Gravity Scale
        if(input.RetrieveJumpHoldInput() && body.velocity.y > 0)
        {
            body.gravityScale = upwardMovementMultiplier;
        }
        else if(!input.RetrieveJumpHoldInput() || body.velocity.y < 0)
        {
            body.gravityScale = downwardMovementMultiplier;
        }
        else if(body.velocity.y == 0)
        {
            body.gravityScale = defaultGravityScale;
        }

        body.velocity = velocity;
    }

    private void JumpAction()
    {
         if(coyoteCounter > 0f || jumpPhase < maxAirJumps && isJumping)
         {
            if(isJumping)
            {
            jumpPhase ++;
            } 

            jumpBufferCounter = 0f;
            coyoteCounter = 0f;
            jumpSpeed = Mathf.Sqrt(-2f * Physics2D.gravity.y * jumpHeight);
            isJumping = true;
            timerManager.StartTimer();

            if(velocity.y > 0f)
            {
                jumpSpeed = Mathf.Max(jumpSpeed - velocity.y, 0f); 
            }
            velocity.y += jumpSpeed;
            AudioManager.Instance.PlaySound("P_Jump");
         }       
    }
}
