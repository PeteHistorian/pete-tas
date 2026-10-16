using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MeleeAttackManager : MonoBehaviour
{
    private bool meleeAttack;
    private bool canAttack = true;
    private GameObject currentWeapon;
    [SerializeField]private GameObject meleeWeapon, meleeWeaponUp, meleeWeaponDown;
    public int meleeDamage = 1;

    private FlipSprite flipSprite;
    private PlayerAnimation playerAnimation;
    private Ground ground;

    //Run this method instead of Initialization if you don't have any scripts inheriting from each other
    private void Start()
    {
        //The Character script on the player; this script on my project manages the grounded state, so if you have a different script for that reference that script
        ground = GetComponent<Ground>();
        flipSprite = GetComponent<FlipSprite>();
        playerAnimation = GetComponent<PlayerAnimation>();
    }

    private void Update()
    {
        CheckInput();
        
    }

    private void CheckInput()
    {
        //Checks if the Player is Attacking
        if (PeteInput.GetButtonDown("Fire1"))
        {
            meleeAttack = true;
        }
        else
        {
            meleeAttack = false;
        }

        
        //Attack Upwards
        if (canAttack && meleeAttack && PeteInput.GetAxis("Vertical") > 0.5)
        {
            playerAnimation.playerIsAttacking(PlayerAnimation.playerAnimationState.ATTACKUP);
        }
        //Attack Downwards 
        if (canAttack && meleeAttack && PeteInput.GetAxis("Vertical") < -0.5 && !ground.GetOnGround())
        {
            playerAnimation.playerIsAttacking(PlayerAnimation.playerAnimationState.ATTACKDOWN);
        }
        //Normal Attack
        if ((canAttack && meleeAttack && PeteInput.GetAxis("Vertical") > -0.5 && PeteInput.GetAxis("Vertical") < 0.5))
        {
            playerAnimation.playerIsAttacking(PlayerAnimation.playerAnimationState.ATTACK1);
        }

    }

    public void EnableWeapon(int weaponName)
    {
        canAttack = false;
        switch(weaponName)
        {
            case 1:
                currentWeapon = meleeWeapon;
                break;
            case 2:
                currentWeapon = meleeWeaponDown;
                break;
            case 3:
                currentWeapon = meleeWeaponUp;
                break;
            default:
                currentWeapon = null;
                break;
        }
        currentWeapon.SetActive(true);
    }

    public void DisableWeapon() 
    {
        currentWeapon.SetActive(false);
        canAttack = true;
        playerAnimation.playerStoppedAttacking();
    }

}