using UnityEngine;

[CreateAssetMenu(fileName = "PlayerController", menuName = "InputController/PlayerController")]

public class PlayerController : InputController
{
    public override bool RetrieveJumpInput()
    {
        return PeteInput.GetButtonDown("Jump");
    }

    public override float RetrieveMoveInput()
    {
        return PeteInput.GetAxisRaw("Horizontal");
    }

    public override bool RetrieveJumpHoldInput()
    {
        return PeteInput.GetButton("Jump");
    }
}
