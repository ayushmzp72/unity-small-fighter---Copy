using UnityEngine;
using UnityEngine.SceneManagement;

public class ModeSelectMenu : MonoBehaviour
{
    public void SelectPlayerVsPlayer()
    {
        FightLoader.instance.gameMode = GameMode.HumanVsHuman;
        SceneManager.LoadScene("DevicesScreen");
    }

    public void SelectPlayerVsAI()
    {
        FightLoader.instance.gameMode = GameMode.HumanVsAI;
        FightLoader.instance.LoadStage();
    }

    public void GoBackToMainMenu()
    {
        SceneManager.LoadScene("Menu");
    }
}