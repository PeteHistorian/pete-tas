using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class StoreTimer : MonoBehaviour
{
    private int minutes;
    private int seconds;
    private int fraction;
    private GameObject finishTimeText;

    public void setFinishTime(int minutes, int seconds, int fraction)
    {
        this.minutes = minutes;
        this.seconds = seconds;
        this.fraction = fraction;
    }

    public void DisplayFinishTime()
    {
        finishTimeText = GameObject.FindGameObjectWithTag("FinishTimeText");
        finishTimeText.GetComponent<TMP_Text>().text = string.Format("{0:00}:{1:00}:{2:00}", minutes, seconds, fraction);
    }
}
