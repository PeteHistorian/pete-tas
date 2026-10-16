using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MeleeWeapon : MonoBehaviour
{
   private int weaponDamage;
   private Component parentTransform;
   [SerializeField] private bool down;

    private void Awake() 
    {
        weaponDamage = GetComponentInParent<MeleeAttackManager>().meleeDamage;   
        parentTransform = GetComponentInParent<Transform>();
    }

    private void OnTriggerEnter2D(Collider2D collision) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(collision.GetComponent<EnemyHealth>() && !collision.isTrigger)
        {
            collision.GetComponent<EnemyHealth>().doDamage(weaponDamage, transform, down);
            //Beim stehen bleiben kann man nur einmal angreifen weil OnTriggerEnter2D nur beim erstmaligen betreten funktioniert.
            //Absolut schrecklicher weg das Problem zu lösen
            parentTransform.transform.localPosition = new Vector3(parentTransform.transform.localPosition.x, parentTransform.transform.localPosition.y + 0.00001f, parentTransform.transform.localPosition.z);
        }
    }

    private void OnDisable() 
    {
        // While the TAS tool restores a savestate it sets the collider itself
        if(TasHooks.MuteCallbacks) return;
        GetComponent<BoxCollider2D>().enabled = false;
    }

    private void OnEnable()
    {
        if(TasHooks.MuteCallbacks) return;
        GetComponent<BoxCollider2D>().enabled = true;
        AudioManager.Instance.PlaySound("P_Swing");
    }
}