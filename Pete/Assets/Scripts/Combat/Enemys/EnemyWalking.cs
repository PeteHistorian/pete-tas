using UnityEngine;

public class EnemyWalking : MonoBehaviour
{
    [SerializeField] private float speed;
    private Transform enemy;
    private FlipSprite flipSprite;
    private int direction = 1;

    void Start()    
    {
        enemy = this.transform;
        flipSprite = GetComponent<FlipSprite>();
    }

    void Update()
    {
        MoveEnemy(direction);
    }

    private void OnTriggerEnter2D(Collider2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.CompareTag("Player"))
        {
            GetComponent<EnemyAttack>().DamagePlayer(other);
        }
        else
        {
            direction = direction*(-1);
            flipSprite._FlipSprite(direction);
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

    private void MoveEnemy(int _direction)
    {
        enemy.position = new Vector3(enemy.position.x + Time.deltaTime * _direction * speed, enemy.position.y, enemy.position.z);
    }
}
