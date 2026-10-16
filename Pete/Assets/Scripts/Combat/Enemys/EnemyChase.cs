using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EnemyChase : MonoBehaviour
{
    [SerializeField] private float speed;
    [SerializeField] private bool groundEnemy;
    private GameObject player;
    private Animator animator;
    private bool chasing = false;

    void Start()
    {
        player = GameObject.FindGameObjectWithTag("Player");
        animator = GetComponent<Animator>();
    }
    
    void Update()
    {
        if(chasing)
        {
            if(groundEnemy)
            {
                animator.SetBool("isWalking", true); 
            }
            Chase(groundEnemy);
        }
        else
        {
            if(groundEnemy)
            {
                animator.SetBool("isWalking", false); 
            }
        }
    }

    private void Chase(bool grounded)
    {
        if(!grounded)
        {
            transform.position = Vector2.MoveTowards(transform.position, player.transform.position, speed * Time.deltaTime);
        }
        else
        {
            transform.position = Vector2.MoveTowards(transform.position, new Vector2(player.transform.position.x, transform.position.y), speed * Time.deltaTime);
        }
    }

    private void OnTriggerEnter2D(Collider2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.CompareTag("Player"))
        {
            chasing = true;
        }    
    }

    private void OnCollisionEnter2D(Collision2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.collider.CompareTag("Player"))
        {
            GetComponent<EnemyAttack>().DamagePlayer(other.collider);
        }
    }
}
