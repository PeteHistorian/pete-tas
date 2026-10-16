using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HealthDrop : MonoBehaviour
{
    [SerializeField] private GameObject healthPickup;
    [SerializeField] private int chance;

    public void EnemyDrop()
    {
        if(Random.Range(0, chance) == 0)
        {
            Instantiate(healthPickup, this.transform.position, new Quaternion());
        }
    }
}
