using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class TimerManager : MonoBehaviour
{
    //Timer
    [SerializeField] private TMP_Text timerText;
    private StoreTimer storeTimer;
    private float currentTime;
    public bool timerOn;

    //Player
    private float playerXpos;
    private Transform playerTransform;

    //Timer split
    private int minutes;
    private int seconds;
    private int fraction;
    private float fFraction;

    private void Start()
    {
        storeTimer = GameObject.FindGameObjectWithTag("storeTimer").GetComponent<StoreTimer>();
        playerTransform = GameObject.FindGameObjectWithTag("Player").transform;
        playerXpos = playerTransform.position.x; 
    }

    private void Update() 
    {
        if(timerOn)
        {
            CalculateTimer();
            DisplayTimer(currentTime);
        }
        else if(!timerOn)
        {
            CheckIfPlayerMoved();
        }
    }

    public void StartTimer() // Starts the Timer on first Movement
    {
        timerOn = true;
    }

    public void StopTimer() // Stops the Timer when the end Scene is reached
    {
        timerOn = false;
        storeTimer.setFinishTime(minutes, seconds, fraction);
    }

    private void CalculateTimer() // Adds time since last Frame to the total Timer
    {
        currentTime += Time.deltaTime;
    }

    private void DisplayTimer(float timeToDisplay) // Splits the total Timer and displays it onto the UI
    {
        minutes = (int) timeToDisplay / 60;
        seconds = (int) timeToDisplay % 60;
        fFraction = timeToDisplay * 100;
        //Debug.Log(timeToDisplay * 100);
        //Debug.Log(fraction);
        fraction = (int) fFraction % 100;
        //Debug.Log(fraction);
        timerText.text = string.Format("{0:00}:{1:00}:{2:00}", minutes, seconds, fraction);
    }

    private void CheckIfPlayerMoved() // Checks if the Player has moved and Starts the timer if he has
    {
        if(playerTransform.position.x != playerXpos)
        {
            StartTimer();
        }
    }
}
