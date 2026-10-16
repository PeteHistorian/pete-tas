using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.SceneManagement;

public class PlayerHealth : MonoBehaviour
{
    private Move move;
    private Rigidbody2D body;
    private KnockbackTrigger knockbackTrigger;
    [SerializeField] TextMeshProUGUI healthText;

    [SerializeField] private int maxPlayerHP;
    private int playerHP = 5;

    [SerializeField] private float kbForce;
    [SerializeField] private float kbTime;
    private float kbCounter;

    private void Start() 
    {
        move = GetComponent<Move>(); 
        body = GetComponent<Rigidbody2D>();
        knockbackTrigger = GetComponent<KnockbackTrigger>();   
        playerHP = maxPlayerHP;
        RefreshHealthUi();
    }

    private void Update()
    {
        kbCounter -= Time.deltaTime;
        if(kbCounter > 0)
            move.canMove = false;
        else
            move.canMove = true;
     
    }

    private void CheckIfPlayerIsDead()
    {
        if(playerHP <= 0)
        {
            Death();
        }
    }

    public void PlayerDamage(int damage, Transform kbOrigin)
    {
        playerHP -= damage;
        AudioManager.Instance.PlaySound("P_Hit");
        GetComponent<HitColor>().ChangeToHitColor();
        kbCounter = kbTime;
        knockbackTrigger.Knockback(kbOrigin);
        RefreshHealthUi();
        CheckIfPlayerIsDead();
    }

    public void HealPlayer(int healthAmount)
    {
        if(playerHP + healthAmount <= maxPlayerHP)
        {
            playerHP += healthAmount;
        }
        RefreshHealthUi();
    }

    private void RefreshHealthUi()
    {
        healthText.text = "" + playerHP;
    }

    private void Death()
    {
        LevelReset.RESETLEVEL();
    }
}
