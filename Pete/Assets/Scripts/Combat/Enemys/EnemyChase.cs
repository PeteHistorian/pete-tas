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
        // Online, on a joining player's game: the host moves the enemies
        if(NetHooks.IsClient) return;
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
        Vector3 target = player.transform.position;
        // Online: chase whichever player is closest
        if(NetHooks.NearestPlayer != null)
        {
            Vector3? nearest = NetHooks.NearestPlayer(transform.position);
            if(nearest.HasValue) target = nearest.Value;
        }
        if(!grounded)
        {
            transform.position = Vector2.MoveTowards(transform.position, target, speed * Time.deltaTime);
        }
        else
        {
            transform.position = Vector2.MoveTowards(transform.position, new Vector2(target.x, transform.position.y), speed * Time.deltaTime);
        }
    }

    // Online: the host starts the chase when another player comes close
    public void StartChasing()
    {
        chasing = true;
    }

    public bool IsChasing
    {
        get { return chasing; }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if(TasHooks.MuteCallbacks) return;
        if(NetHooks.IsClient) return;
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
